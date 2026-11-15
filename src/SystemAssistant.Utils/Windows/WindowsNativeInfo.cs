using System.Runtime.InteropServices;
using System.Text;
using SystemAssistant.Models.Hardware;
using SystemAssistant.Models.Processes;
using static SystemAssistant.Utils.Windows.NativeMethods;

namespace SystemAssistant.Utils.Windows;

/// <summary>
/// 对 Win32 P/Invoke 的安全封装：物理内存、进程 IO 计数、显示器 / HDR、进程文件句柄。
/// 所有方法都不会抛出异常，失败时返回空结果。
/// </summary>
public static class WindowsNativeInfo
{
    /// <summary>物理内存与提交内存（页面文件）状态。</summary>
    public static (long TotalPhys, long AvailablePhys, long TotalCommit, long AvailableCommit) GetMemoryStatus()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return (0, 0, 0, 0);
            return ((long)status.TotalPhys, (long)status.AvailablePhys,
                (long)status.TotalPageFile, (long)status.AvailablePageFile);
        }
        catch
        {
            return (0, 0, 0, 0);
        }
    }

    private static long _prevIdle;
    private static long _prevTotal;
    private static readonly object CpuSync = new();

    /// <summary>
    /// 通过 GetSystemTimes 的两次采样差值计算全局 CPU 占用率。
    /// 首次调用返回 null（尚无基准值），调用方应回落到 WMI LoadPercentage。
    /// </summary>
    public static double? GetCpuUsagePercent()
    {
        lock (CpuSync)
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
                var idleTicks = (long)idle.Value;
                var totalTicks = (long)(kernel.Value + user.Value);
                if (_prevTotal == 0)
                {
                    _prevIdle = idleTicks;
                    _prevTotal = totalTicks;
                    return null;
                }
                var deltaIdle = idleTicks - _prevIdle;
                var deltaTotal = totalTicks - _prevTotal;
                _prevIdle = idleTicks;
                _prevTotal = totalTicks;
                if (deltaTotal <= 0) return null;
                return Math.Clamp((1 - deltaIdle * 1.0 / deltaTotal) * 100, 0, 100);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>读取进程累计 IO 字节数，供进程监视器计算读写速率。</summary>
    public static (ulong ReadBytes, ulong WriteBytes) GetProcessIoBytes(IntPtr processHandle)
    {
        try
        {
            return GetProcessIoCounters(processHandle, out var counters)
                ? (counters.ReadTransferCount, counters.WriteTransferCount)
                : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>枚举所有已连接到桌面的显示器，并尽力读取 HDR 状态。</summary>
    public static IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var result = new List<DisplayInfo>();
        try
        {
            var hdrStates = GetHdrStates();
            for (uint index = 0; index < 16; index++)
            {
                var device = new DisplayDevice
                {
                    Size = Marshal.SizeOf<DisplayDevice>(),
                    DeviceName = new string('\0', 32),
                    DeviceString = new string('\0', 128),
                    DeviceId = new string('\0', 128),
                    DeviceKey = new string('\0', 128)
                };
                if (!EnumDisplayDevices(null, index, ref device, 0)) break;
                if ((device.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;

                var mode = new DevMode
                {
                    Size = (short)Marshal.SizeOf<DevMode>(),
                    DeviceName = new string('\0', 32),
                    FormName = new string('\0', 32)
                };
                if (!EnumDisplaySettings(device.DeviceName, EnumCurrentSettings, ref mode)) continue;

                bool? hdr = null;
                var order = result.Count;
                if (hdrStates.Count > 0) hdr = order < hdrStates.Count ? hdrStates[order] : hdrStates[0];

                result.Add(new DisplayInfo
                {
                    DeviceName = string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceName : device.DeviceString,
                    Width = mode.PelsWidth,
                    Height = mode.PelsHeight,
                    RefreshRateHz = mode.DisplayFrequency,
                    BitsPerPixel = mode.BitsPerPel,
                    IsPrimary = (device.StateFlags & DisplayDevicePrimaryDevice) != 0,
                    HdrEnabled = hdr
                });
            }
        }
        catch
        {
            // 显示器枚举失败不影响其它模块
        }
        return result;
    }

    /// <summary>通过 DisplayConfig API 读取每条活动显示路径的高级色彩（HDR）启用状态。</summary>
    private static IReadOnlyList<bool> GetHdrStates()
    {
        var states = new List<bool>();
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0 || pathCount == 0)
                return states;

            var paths = new DisplayConfigPathInfo[pathCount];
            // DISPLAYCONFIG_MODE_INFO 固定 64 字节；只作为输出缓冲使用，不需要解析
            var modes = new byte[Math.Max(1, (int)modeCount) * 64];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return states;

            foreach (var path in paths.Take((int)pathCount))
            {
                var request = new DisplayConfigGetAdvancedColorInfo
                {
                    Header = new DisplayConfigDeviceInfoHeader
                    {
                        Type = DisplayConfigDeviceInfoGetAdvancedColorInfo,
                        Size = Marshal.SizeOf<DisplayConfigGetAdvancedColorInfo>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };
                // value 的 bit0 = 支持高级色彩，bit1 = 已启用 HDR
                states.Add(DisplayConfigGetDeviceInfo(ref request) == 0 && (request.Value & 0x2) != 0);
            }
        }
        catch
        {
            // 旧版 Windows 或权限不足时无法读取，交由上层显示为「未知」
        }
        return states;
    }

    /// <summary>
    /// 尽力枚举指定进程持有的文件句柄。系统保护进程或权限不足时返回空列表。
    /// 采用 NtQuerySystemInformation(SystemExtendedHandleInformation) + DuplicateHandle。
    /// </summary>
    public static IReadOnlyList<ProcessHandleInfo> GetFileHandles(int processId, int maxResults = 200)
    {
        var result = new List<ProcessHandleInfo>();
        IntPtr buffer = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        try
        {
            var handles = QuerySystemHandles();
            var myHandles = handles.Where(h => h.ProcessId == processId).Take(maxResults * 4).ToList();
            if (myHandles.Count == 0) return result;

            processHandle = OpenProcess(ProcessDupHandle, false, processId);
            if (processHandle == IntPtr.Zero) return result;

            var current = GetCurrentProcess();
            var pathBuilder = new StringBuilder(1024);
            foreach (var entry in myHandles)
            {
                if (result.Count >= maxResults) break;
                if (!DuplicateHandle(processHandle, entry.Handle, current, out var duplicated, 0, false, DuplicateSameAccess))
                    continue;
                try
                {
                    if (GetFileType(duplicated) != FileTypeDisk) continue;
                    pathBuilder.Clear();
                    var length = GetFinalPathNameByHandle(duplicated, pathBuilder, (uint)pathBuilder.Capacity, 0);
                    if (length == 0 || length > pathBuilder.Capacity) continue;
                    var name = pathBuilder.ToString();
                    if (name.StartsWith(@"\\?\", StringComparison.Ordinal)) name = name[4..];
                    result.Add(new ProcessHandleInfo
                    {
                        HandleValue = entry.Handle.ToInt64(),
                        ObjectName = name,
                        ObjectType = "File"
                    });
                }
                finally
                {
                    CloseHandle(duplicated);
                }
            }
        }
        catch
        {
            // 句柄枚举属于「尽力而为」能力，任何失败都不应影响主界面
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        }
        return result;
    }

    private static IReadOnlyList<(int ProcessId, IntPtr Handle)> QuerySystemHandles()
    {
        var list = new List<(int, IntPtr)>();
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var size = 1 << 20; // 起始 1 MB，不足时按系统返回值扩容
            const int statusInfoLengthMismatch = unchecked((int)0xC0000004);
            int status;
            int required;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                buffer = Marshal.AllocHGlobal(size);
                status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out required);
                if (status == 0) break;
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                if (status != statusInfoLengthMismatch) return list;
                size = Math.Max(required + (1 << 16), size * 2);
            }
            if (buffer == IntPtr.Zero) return list;

            var count = (long)Marshal.ReadIntPtr(buffer);
            var headerSize = IntPtr.Size * 2; // NumberOfHandles + Reserved
            var entrySize = Marshal.SizeOf<SystemHandleTableEntryInfoEx>();
            var maxEntries = Math.Min(count, (size - headerSize) / entrySize);
            for (long i = 0; i < maxEntries; i++)
            {
                var ptr = IntPtr.Add(buffer, headerSize + (int)(i * entrySize));
                var entry = Marshal.PtrToStructure<SystemHandleTableEntryInfoEx>(ptr);
                list.Add(((int)entry.UniqueProcessId.ToInt64(), entry.HandleValue));
            }
        }
        catch
        {
            // 返回已解析的部分
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
        return list;
    }
}
