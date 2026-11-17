using System.Diagnostics;
using System.Globalization;
using System.Text;
using SystemAssistant.Models.Optimization;
using SystemAssistant.Utils.Windows;

namespace SystemAssistant.Utils.Diagnostics;

/// <summary>
/// 外部命令执行器。支持实时输出事件、取消、以及按需 UAC 提权执行。
/// 提权执行时因 Windows 限制无法直接重定向输出，改为临时脚本写日志文件 + 轮询读取。
/// </summary>
public sealed class CommandRunner
{
    private static readonly Lazy<Encoding> ConsoleEncodingLazy = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    /// <summary>命令输出的本地控制台编码（中文 Windows 通常为 GBK）。</summary>
    public static Encoding ConsoleEncoding => ConsoleEncodingLazy.Value;

    /// <summary>每产生一行输出时触发（后台线程）。UI 层需要自行切回 UI 线程。</summary>
    public event Action<CommandOutputLine>? OutputReceived;

    /// <summary>
    /// 执行一条命令。commandLine 为完整命令文本，例如 "sfc /scannow"。
    /// </summary>
    /// <param name="asAdmin">是否以管理员权限执行；为 true 且当前非管理员时会弹出 UAC。</param>
    public Task<CommandExecutionResult> RunAsync(string commandLine, CancellationToken token = default, bool asAdmin = false)
        => asAdmin && !PrivilegeHelper.IsAdministrator
            ? RunElevatedAsync(commandLine, token)
            : RunDirectAsync(commandLine, token);

    /// <summary>执行简单的短命令，不订阅输出事件。</summary>
    public static async Task<CommandExecutionResult> RunQuickAsync(string fileName, string arguments, CancellationToken token = default)
    {
        var psi = CreateStartInfo(fileName, arguments);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Process? process = null;
        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(token);
            return new CommandExecutionResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            // 取消时结束子进程，避免 reg.exe / 网络命令成为孤儿继续运行。
            if (process is not null) KillProcessTree(process);
            return new CommandExecutionResult { ExitCode = -1, StandardError = "用户取消了操作" };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandExecutionResult { ExitCode = -1, StandardError = ex.Message };
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<CommandExecutionResult> RunDirectAsync(string commandLine, CancellationToken token)
    {
        var psi = CreateStartInfo("cmd.exe", $"/d /s /c \"{commandLine.Replace("\"", "\\\"")}\"");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Process? process = null;

        try
        {
            process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdout) stdout.AppendLine(e.Data);
                OutputReceived?.Invoke(new CommandOutputLine { Text = e.Data });
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderr) stderr.AppendLine(e.Data);
                OutputReceived?.Invoke(new CommandOutputLine { Text = e.Data, IsError = true });
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(token);
            return new CommandExecutionResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            // 取消必须结束整个进程树：cmd.exe 及其派生的 sfc / defrag 子进程
            // 可能运行数十分钟，只停止等待会留下持续改动系统的孤儿进程。
            if (process is not null) KillProcessTree(process);
            return new CommandExecutionResult { ExitCode = -1, StandardError = "用户取消了操作" };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            OutputReceived?.Invoke(new CommandOutputLine { Text = ex.Message, IsError = true });
            return new CommandExecutionResult { ExitCode = -1, StandardError = ex.Message };
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>
    /// 提权执行：生成临时 .cmd，把输出重定向到临时日志文件，随后轮询增量内容。
    /// 临时文件在完成后立即清理。
    /// </summary>
    private async Task<CommandExecutionResult> RunElevatedAsync(string commandLine, CancellationToken token)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SystemAssistant_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var script = Path.Combine(tempDir, "run.cmd");
        var log = Path.Combine(tempDir, "output.log");
        var code = Path.Combine(tempDir, "exit.code");

        try
        {
            // 临时目录可能位于中文用户目录下（Path.GetTempPath() 含非 ASCII）。
            // 直接用 Encoding.ASCII 写脚本会把这些路径变成 '?'，导致重定向输出丢失。
            // 这里用 %~dp0（脚本自身所在目录，运行时展开）引用日志 / 退出码文件，
            // 脚本内容保持纯 ASCII；同时用 UTF-8 写文件，确保任何 Unicode 字符都不被破坏。
            // chcp 65001 让后续输出统一按 UTF-8 编码，DrainLog 才能正确解码。
            var scriptContent =
                "@echo off\r\n" +
                "chcp 65001 >nul\r\n" +
                $"({commandLine}) > \"%~dp0output.log\" 2>&1\r\n" +
                "echo %ERRORLEVEL% > \"%~dp0exit.code\"\r\n";
            File.WriteAllText(script, scriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var psi = new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new CommandExecutionResult { ExitCode = -1, StandardError = "用户取消了权限提升" };
            }

            long lastPosition = 0;
            byte[]? pendingBytes = null;
            var output = new StringBuilder();
            while (!process.HasExited)
            {
                if (token.IsCancellationRequested)
                {
                    KillProcessTree(process);
                    return new CommandExecutionResult { ExitCode = -1, StandardError = "用户取消了操作" };
                }
                lastPosition = DrainLog(log, lastPosition, output, ref pendingBytes);
                try { await Task.Delay(250, token); }
                catch (OperationCanceledException)
                {
                    KillProcessTree(process);
                    return new CommandExecutionResult { ExitCode = -1, StandardError = "用户取消了操作" };
                }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            lastPosition = DrainLog(log, lastPosition, output, ref pendingBytes);
            // 进程已退出，残留的不完整序列以替换字符形式冲出（避免丢失尾部字节）
            if (pendingBytes is { Length: > 0 })
            {
                var tail = new string(Encoding.UTF8.GetChars(pendingBytes));
                output.Append(tail);
                foreach (var line in tail.Replace("\r\n", "\n").Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line))
                        OutputReceived?.Invoke(new CommandOutputLine { Text = line });
            }

            var exitCode = -1;
            if (File.Exists(code) && int.TryParse(File.ReadAllText(code).Trim(), out var parsed)) exitCode = parsed;

            return new CommandExecutionResult
            {
                ExitCode = exitCode,
                StandardOutput = output.ToString(),
                StandardError = exitCode == 0 ? string.Empty : $"命令返回退出码 {exitCode}"
            };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new CommandExecutionResult { ExitCode = -1, StandardError = "用户取消了权限提升" };
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>取消时结束整个进程树，避免遗留仍在运行的提权子进程。</summary>
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                      or NotSupportedException or AggregateException) { }
    }

    /// <summary>
    /// 增量读取日志。按字节位置 Seek 可能在多字节 UTF-8 字符中间截断，
    /// 用 Decoder 的跨调用状态（pending 字节）保证汉字不被解码成乱码。
    /// </summary>
    private long DrainLog(string logPath, long position, StringBuilder output, ref byte[]? pending)
    {
        try
        {
            if (!File.Exists(logPath)) return position;
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= position) return position;
            stream.Seek(position, SeekOrigin.Begin);
            var count = (int)Math.Min(stream.Length - position, int.MaxValue);
            var bytes = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(bytes, read, count - read);
                if (n == 0) break;
                read += n;
            }
            var newPosition = stream.Position;

            // 把上次残留的半个字符字节拼到本次前面一起解码
            var total = (pending?.Length ?? 0) + read;
            var combined = new byte[total];
            if (pending is not null) Buffer.BlockCopy(pending, 0, combined, 0, pending.Length);
            Buffer.BlockCopy(bytes, 0, combined, pending?.Length ?? 0, read);

            // Convert 明确返回本次实际消费的字节数：结尾处不完整的多字节序列
            // 会留在输入中不被消费，作为 pending 留给下一次轮询。
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[combined.Length + 1];
            decoder.Convert(combined, 0, combined.Length, chars, 0, chars.Length,
                flush: false, out var usedBytes, out var charCount, out _);
            var text = new string(chars, 0, charCount);
            pending = usedBytes < combined.Length ? combined[usedBytes..] : null;

            if (text.Length > 0)
            {
                output.Append(text);
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        OutputReceived?.Invoke(new CommandOutputLine { Text = line });
                }
            }
            return newPosition;
        }
        catch (IOException)
        {
            return position;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string arguments) => new(fileName, arguments)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        StandardOutputEncoding = ConsoleEncoding,
        StandardErrorEncoding = ConsoleEncoding
    };
}
