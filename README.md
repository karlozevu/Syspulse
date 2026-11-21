# Syspulse

Windows 10 / 11 上的本机硬件监控、安全清理与系统维护工具。使用 **C# .NET 9 + Avalonia UI 11** 开发，采用严格分层的四项目解决方案，UI 与业务逻辑完全解耦。

> 本项目**不联网上传任何硬件信息、扫描结果或文件列表**；不捆绑任何第三方软件、广告；不会静默修改系统设置。

> 复检报告：[REVIEW.md](REVIEW.md) · 全功能测试与修复：[FUNCTIONAL-TEST.md](FUNCTIONAL-TEST.md) · 变更记录：[CHANGELOG.md](CHANGELOG.md) · 安装包见第 8 节，回归探针见第 10 节。

---

## 1. 项目结构

```
Syspulse/
├── Syspulse.sln
├── Directory.Build.props           # 共享编译设置
├── NuGet.Config                    # NuGet 源（含国内镜像）
├── scripts/
│   ├── build.cmd                   # 一键还原 + 编译
│   ├── publish.ps1                 # 单文件绿色便携版发布脚本
│   └── publish.cmd                 # 发布脚本的双击入口
└── src/
    ├── Syspulse.Models/     # 纯数据实体（net9.0，零 UI / 零平台依赖）
    ├── Syspulse.Utils/      # 权限提升、文件操作、哈希、命令执行、
    │                               # 注册表备份、JSON 持久化、导出、Win32 P/Invoke
    ├── Syspulse.Core/       # 全部业务逻辑（完全不引用 Avalonia）
    └── Syspulse.App/        # Avalonia UI：页面、ViewModel、弹窗、主题、多语言
```

依赖方向严格单向：`App → Core → Utils → Models`。`Core` 中所有对外能力都通过 `Interfaces/ISyspulseServices.cs` 暴露，UI 只依赖接口；组合根是 `Core/Common/ServiceHub.cs`。**后续切换 WPF 时，只需重写 `App` 项目**，另外三层可原样复用（这正是备选方案要求保留的部分）。

后台采集（`SensorMonitor`、`ProcessMonitor`）使用 `PeriodicTimer` + 独立 `Task`，通过事件把快照推给 UI；UI 层统一用 `Dispatcher.UIThread.Post` 切回界面线程，避免阻塞。

---

## 2. 编译环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809+ / Windows 11（x64；也可发布 win-arm64） |
| SDK | **.NET 9 SDK**（开发与验证使用 9.0.306） |
| IDE（可选） | Visual Studio 2022 17.12+ / Rider / VS Code + C# Dev Kit |
| 运行时（调试） | 随 SDK 自带 |
| 额外驱动 | **不需要**。硬件监控使用系统 WMI + LibreHardwareMonitor 用户态/内核态访问，部分主板传感器需要管理员权限但不需要单独安装驱动 |

> 目标框架统一为 `net9.0-windows`（Models 为 `net9.0`）。程序清单 `app.manifest` 设为 `asInvoker`：**默认以普通权限启动**，需要高权限时由用户在界面上主动触发 UAC 提升。

---

## 3. 依赖安装与编译步骤

主要 NuGet 依赖（版本见各 `.csproj`）：

| 包 | 用途 | 所在层 |
| --- | --- | --- |
| `LibreHardwareMonitorLib` 0.9.4 | 温度 / 风扇 / 电压 / 负载 / 频率传感器 | Core |
| `System.Management` 9.0.x | WMI 查询系统、硬件、进程、服务 | Core |
| `System.ServiceProcess.ServiceController` | Windows 服务管理 | Core |
| `System.Text.Json` | 配置、白名单、日志持久化 | Utils |
| `Microsoft.Extensions.Logging*` | 日志管理（Debug + 本地文件提供器） | Utils / App |
| `Avalonia` 11.3.x（含 Desktop / Themes.Fluent / Fonts.Inter） | UI 框架 | App |

> **关于 OxyPlot.Avalonia**：`OxyPlot.Avalonia 2.1.0` 只支持 Avalonia 0.10，`2.2.0` 要求 .NET 10 + Avalonia 12，与本项目 .NET 9 + Avalonia 11 不兼容。因此实时曲线改为 `Controls/LineChartControl.cs` 用 Avalonia 原生 `DrawingContext` 自绘（零额外依赖，支持滚动历史、网格、阈值配色）。若日后升级到 .NET 10 / Avalonia 12，可只替换该控件。

编译：

```bash
dotnet restore Syspulse.sln
dotnet build   Syspulse.sln -c Release
```

或双击 `scripts/build.cmd`。

运行：

```bash
dotnet run --project src/Syspulse.App/Syspulse.App.csproj
```

或直接执行 `src/Syspulse.App/bin/Debug/net9.0-windows/Syspulse.exe`。

---

## 4. 发布：独立绿色单文件便携版

```powershell
# 框架依赖单文件（体积小，目标机器需预装 .NET 9 Desktop Runtime）
./scripts/publish.ps1

# 自包含单文件（免装运行时，体积较大）
./scripts/publish.ps1 -IncludeRuntime

# 其它参数
./scripts/publish.ps1 -IncludeRuntime -Runtime win-arm64 -Configuration Release
```

也可以双击 `scripts/publish.cmd`（默认框架依赖版；`publish.cmd -IncludeRuntime` 为自包含版）。

输出位于 `artifacts/<rid>-<mode>/`，包含 `Syspulse.exe`、`README-portable.txt`，并自动生成同名 `.zip`。脚本启用了 `PublishSingleFile`、`IncludeNativeLibrariesForSelfExtract`、`IncludeAllContentForSelfExtract`（Avalonia / Skia / 硬件监控原生库必须一并打包），并去掉 pdb。绿色版不写注册表、不需要安装，配置与日志落在 `%LOCALAPPDATA%\Syspulse`。

### 4.1 发布到 GitHub

`scripts/publish_github.py` 通过 GitHub REST API 创建仓库、提交完整源码树，并按项目版本创建 Release（默认读取 `Directory.Build.props`，也可用 `--version` 指定），附上两个便携版 zip 与安装程序。它**不依赖 `git push`**，因此可以在只放通 `api.github.com` 的代理环境下使用；仅用 Python 标准库。

```bash
# 1) 在 GitHub 创建 classic token（勾选 repo 即可）
#    https://github.com/settings/tokens
#    推荐：把 token 粘贴进仓库根目录的 .github_token 文件（已在 .gitignore 中），
#          脚本会自动读取，且发布成功后自动清空，不会出现在命令行历史或对话里。
#    也可以：set GITHUB_TOKEN=<你的 token>   （Windows CMD）
#            export GITHUB_TOKEN=<你的 token> （bash）

# 2) 先按上一节生成两个 zip，再执行：
python scripts/publish_github.py --repo System-Assistant --visibility public
```

> 说明：GitHub 的 MCP/App 集成授权通常不含「创建仓库」权限（`POST /user/repos` 返回 403
> `Resource not accessible by integration`），且往往只覆盖安装时选中的仓库，因此发布脚本
> 使用一次性个人令牌；也可用 `git remote add origin` + `git push` 代替本脚本。

可选参数：`--version`（Release 标签，默认取 `Directory.Build.props` 的 `<Version>`；指定新版本会创建独立 tag 与 Release，历史版本保留）、`--owner`（默认取 token 所属账号）、`--token-file`、`--no-release`（只推源码）、`--artifacts`（产物目录）、`--message`（提交信息）。脚本会上传两个 zip 与 `Syspulse-Setup-<版本>.exe`，重复运行会更新同版本 Release 的正文并替换同名附件。Token 只在内存中使用，不写入日志；发布完成后建议撤销。脚本读取 `git ls-files` 的索引内容作为文件清单，因此 `.gitignore` 中的 `bin/`、`obj/`、`artifacts/`、`.github_token` 不会被上传，且**远端文件树与本地索引完全一致**（tree SHA 相同）。

> 注意：如果仓库是先在 GitHub 网页手动创建并初始化了 README，远端会多出一个初始提交，与本地的提交历史不同（文件内容相同）。此时从本地直接 `git push` 会被判为非快进而拒绝；建议以远端为准重新 `clone`，或在确认无他人提交后使用 `git push --force-with-lease`。

---

## 5. 功能说明

### 模块一 · 硬件信息与实时监控
- **整机概览**：OS 版本与安装时间、主板 / BIOS、CPU、内存、显卡、全部物理磁盘、网卡、电池、显示器；一键导出 **TXT / Markdown / HTML** 到默认导出目录。
- **CPU**：型号、核心 / 线程、基础频率、L1/L2/L3 缓存、占用率、实时频率、温度、封装功耗。
- **内存**：总量 / 已用 / 可用 / 分页占用 / 内存条明细与插槽信息。
- **GPU**：型号、显存总量、已用显存、负载、核心温度。
- **磁盘**：物理盘 + 分区（型号、容量、已用、文件系统）、介质类型（SSD/HDD）、SMART 健康状态与警告 / 故障标记。
- **传感器面板**：温度 / 风扇 / 电压滚动折线图，刷新间隔可即时调整，超过阈值弹窗提醒（同一传感器 2 分钟冷却，避免骚扰）。
- **笔记本电池**：设计容量、满充容量、损耗百分比、循环次数、剩余电量。
- **显示器**：分辨率、刷新率、位深、HDR 状态（DisplayConfig API）。

### 模块二 · 安全垃圾清理
- 系统垃圾（临时目录、Windows 更新缓存、日志、错误转储、缩略图、预读）、Edge / Chrome / Firefox 缓存、应用缓存、保守的注册表残留扫描、自定义路径。
- 历史记录 / Cookie / 下载记录 / 注册表项 / 预读文件 / 自定义路径一律标记为高风险且**默认不勾选**。
- 清理白名单支持目录、文件名、后缀（含通配符），扫描与删除两阶段都校验。
- 流程强制为：**扫描 → 分组列表 → 用户勾选 → 明细确认 → 高危二次确认 → 删除（实时进度）→ 写操作日志**。
- 注册表清理前必须完成 `reg.exe export` 备份到用户选择目录，并生成 `RESTORE_README.txt` 还原说明；未备份时 Core 层直接拒绝删除。

### 模块三 · 系统优化
- 启动项（注册表 Run / RunOnce + 所有用户启动文件夹）：名称、路径、发布者、启用 / 禁用（Windows `StartupApproved` 机制，可恢复）/ 删除（回收站）。
- 服务管理器：全部服务、状态、启动类型、启动 / 停止 / 修改启动类型；内置核心服务名单，高风险服务显示文字警告。
- 系统映像修复：`sfc /scannow` 与 `DISM /Online /Cleanup-Image /RestoreHealth`，内嵌控制台窗口实时输出（提权命令通过临时脚本重定向日志轮询）。
- 磁盘维护：SSD 执行 `defrag /L`（TRIM），HDD 执行 `defrag /O`，支持 `/A` 仅分析。
- 网络工具：刷新 DNS、重置 Winsock、重置 TCP/IP、ping 延迟 / 丢包测试。
- 简易调优：系统动画、遥测、开始菜单建议；虚拟内存查看与系统属性入口；每项都有风险说明且**不会自动应用**。

### 模块四 · 进程与资源监视器
- PID、名称、CPU%、内存、磁盘读 / 写速率、启动用户；点击表头按任意列排序。
- 结束进程 / 进程树（权限不足时提示提升）、进程详情（加载模块 + 打开的文件句柄）。
- 独立悬浮仪表盘窗口：全局 CPU / 内存，可置顶、可一键隐藏 / 恢复主窗口。

### 模块五 · 独立工具箱
- 文件粉碎：1~7 次覆写（随机 / 全 0 / 全 1 交替）+ 随机重命名 + 删除，支持多选。
- 大文件查找：按体积降序，支持打开所在目录、移入回收站。
- 重复文件查找：按大小分组 → 快速指纹（头尾 8 KB）→ 完整 SHA256 确认；整组被勾选时自动阻止，保证每组至少保留一份。
- 系统快捷入口：任务管理器、注册表编辑器、CMD、PowerShell、服务管理器。

### 模块六 · 通用基础
- 浅色 / 深色 / 跟随系统主题（FluentTheme 动态资源）。
- 简体中文 / English，切换即时生效，无需重启（`Loc.Key` 附加属性 + 语言变更事件）。
- 设置持久化：刷新间隔、白名单、自定义路径、导出 / 备份路径、主题、语言、阈值、更新 URL。
- 操作日志：所有删除 / 修改 / 系统调用带时间、内容、成功与否；支持查看、导出 CSV、清空（清空本身也留痕）。
- 权限处理：检测管理员权限，高权限功能按钮禁用并提示，可主动 UAC 重启。
- 更新检测：启动时**仅检测**，`UpdateCheckUrl` 默认留空即完全不联网；配置后只发一次 GET 取 `{version, notes, url}`，不下载、不静默安装。

### 安全强制规范落实位置
| 规范 | 实现 |
| --- | --- |
| 删除 / 注册表 / 服务 / 调优二次确认 | `DialogService.ConfirmHighRiskAsync`（风险确认 + 最后确认两个对话框），清理页另有独立二次确认 |
| 保守默认勾选 | `ScanItemBuilder.DefaultChecked`：仅 `RiskLevel.Low` 默认勾选 |
| 不静默改系统设置 | Core 所有写操作都只在 UI 显式确认后调用；启动时不写入任何值 |
| 不联网上传数据 | 只有 `UpdateCheckService` 在用户配置 URL 后发一次无附带数据的 GET |
| 不捆绑软件 / 广告 | 无安装器、无广告、无第三方推广 |
| 额外护栏 | `PathGuard` 禁止删除系统 / 个人目录根、盘符根、关键系统文件；白名单在扫描和删除阶段双重校验 |

---

## 6. 已知限制与风险（务必阅读）

1. **权限**：默认 `asInvoker`。服务管理、sfc / DISM、磁盘维护、HKLM 启动项与注册表删除、遥测策略项需要管理员权限；非管理员时相关按钮禁用。传感器在部分主板上需要管理员权限才能读取风扇 / 电压，此时相应字段显示 N/A，但程序不会崩溃。
2. **SMART 为尽力而为**：Windows 没有统一公开的 SMART 接口。健康状态优先取 `MSFT_PhysicalDisk.HealthStatus`，属性明细取 `MSStorageDriver_ATAPISmartData` 原始块（按磁盘顺序匹配），部分 NVMe / RAID / USB 桥接盘可能只显示「未知」。请以厂商工具为准。
3. **WMI 不提供的字段**：CPU 最大睿频规格、内存通道数无法通过标准 WMI 获取，界面显示 N/A 或插槽占用说明；`Win32_VideoController.AdapterRAM` 为 32 位字段，大于 4 GB 显存可能显示偏小。
4. **HDR / 文件句柄**：HDR 依赖 `DisplayConfigGetDeviceInfo`，旧系统或非活动显示路径可能显示「未知」；文件句柄枚举基于 `NtQuerySystemInformation`，受保护进程 / 权限不足时返回空列表并给出提示。
5. **文件粉碎**：多次覆写能降低被恢复概率，但 SSD 磨损均衡 / TRIM、快照、云同步目录无法保证物理层不可恢复；粉碎不可撤销，务必确认。
6. **注册表清理**：只报告有明确失效证据的项（安装目录 / 图标 / 命令路径不存在、ProgID 缺失），不做启发式批量删除；执行前强制备份。即便如此，修改注册表仍有风险，建议创建系统还原点。
7. **浏览器缓存**：浏览器运行期间相关文件被占用，会删除失败并记入日志；建议先关闭浏览器。
8. **系统命令耗时**：`sfc /scannow` 与 `DISM /RestoreHealth` 可能持续数十分钟，请耐心等待，期间不要强制关闭。
9. **单文件发布**：Avalonia + 原生库在单文件模式下首次启动会解压到临时目录，启动略慢；若被安全软件拦截，请把 exe 加入白名单或改用普通目录发布。
10. **测试范围**：本仓库已在 .NET 9 SDK 9.0.306 下完成 **Debug + Release 编译验证**（0 错误 0 警告）、**两种单文件发布**（框架依赖 / 自包含）以及**应用启动冒烟测试**（普通构建与自包含 exe 均持续运行无崩溃、无运行时异常）。硬件读取、WMI、SMART、服务操作、注册表备份、清理删除等强依赖具体机型、账户权限与真实用户交互，需在目标机器上按第 7 节验收清单逐项实测。
11. **UI 采用代码构建**：页面由 `App/Views/PageViewLocator*.cs` 用 C# 构建（XAML 只用于 `App.axaml` 与 `Styles/AppStyles.axaml`）。这样主题 / 多语言切换、动态表格与曲线控件都由同一套绑定机制管理，也减少了 XAML 编译期的平台差异；ViewModel 与 View 仍然完全分离。

---

## 7. 验收自查清单

- [ ] Windows 10 / Windows 11 正常运行，无额外驱动
- [ ] 硬件信息读取稳定，切换页面不崩溃
- [ ] 清理流程：白名单生效、注册表备份生成 `RESTORE_README.txt`、高危项默认不勾选
- [ ] 所有高危操作出现两次确认，日志正确记录成功 / 失败
- [ ] 后台采集期间界面滚动 / 切换流畅
- [ ] 主题与中英文切换即时生效
- [ ] 发布脚本产出可直接双击运行的单文件 exe

---

## 8. Windows 安装程序（Inno Setup 6）

成品：`artifacts/Syspulse-Setup-1.2.2.exe`（约 42 MB）。这是用 **Inno Setup 6** 编译的真正安装向导，双击即可运行；内部用 LZMA2 压缩打包了 `artifacts/win-x64-self-contained/Syspulse.exe` 自包含单文件版，目标机器**无需安装 .NET 运行时**。

### 8.1 运行要求与安装位置

| 项目 | 说明 |
| --- | --- |
| 系统 | Windows 10 / 11（64 位，`x64compatible`） |
| 权限 | `PrivilegesRequired=lowest`，**不需要管理员权限、不会弹 UAC** |
| 默认目录 | `%LOCALAPPDATA%\Programs\Syspulse`（Inno Setup 常量 `{userpf}`） |
| 卸载注册表 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Syspulse` |
| 用户数据 | `%LOCALAPPDATA%\Syspulse`（配置 / 日志），**安装与卸载都不删除** |

### 8.2 向导流程（默认简体中文，可切换 English）

选择安装语言 → 欢迎 → 选择目标位置 → 选择附加任务（可选「创建桌面快捷方式」，默认不勾选）→ 准备安装 → 安装进度 → 完成（可选「运行 Syspulse」）。

安装后会创建开始菜单文件夹 `Syspulse`，内含主程序快捷方式与「卸载 Syspulse」快捷方式；桌面快捷方式仅在勾选附加任务时创建。

卸载信息由脚本的 `[Registry]` 写入 HKCU 的 `...\Uninstall\Syspulse`，包含 `DisplayName`、`DisplayVersion`、`Publisher`、`InstallLocation`、`DisplayIcon`、`UninstallString`（另有 `QuietUninstallString`、`URLInfoAbout`、`NoModify`、`NoRepair`）。卸载器 `unins000.exe` 随程序安装，可从开始菜单快捷方式或「应用和功能」启动，会删除安装文件、快捷方式与该注册表项；**不会触碰用户数据目录**，交互式卸载时还会弹窗说明数据将被保留。

> 脚本刻意设置 `CreateUninstallRegKey=no` 并自行写注册表，以便卸载项名称严格等于 `Syspulse`（而不是 Inno Setup 默认的 `...\Syspulse_is1`）；卸载器仍会正常生成，且该键带 `uninsdeletekey`，卸载时自动清理。脚本中没有任何 `[InstallDelete]` / `[UninstallDelete]` 段，用户数据目录从未列入删除范围。

### 8.3 静默安装 / 卸载

```bat
:: 完全静默安装（无界面）
Syspulse-Setup-1.2.2.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART ^
  /DIR="%LOCALAPPDATA%\Programs\Syspulse" /LOG="%TEMP%\sa-install.log"

:: 其他常用参数
:: /SILENT                    隐藏向导页但保留进度条
:: /LANG=english              使用英文界面
:: /TASKS="desktopicon"       额外创建桌面快捷方式
:: /NOCANCEL /CLOSEAPPLICATIONS

:: 静默卸载
"%LOCALAPPDATA%\Programs\Syspulse\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

静默安装不会自动启动程序（`[Run]` 带 `skipifsilent`），适合无人值守 / CI 部署。

### 8.4 从源码重建安装包

```bat
cd /d <仓库根目录>\Syspulse
scripts\build-installer.cmd
```

构建脚本会自动定位编译器，优先使用工作区内的便携副本 `.tools\inno\ISCC.exe`（Inno Setup 6.7.3，未安装到系统），找不到时回退到本机已安装的 Inno Setup 6。相关源码：

- `installer\Syspulse-Setup.iss` —— 安装 / 卸载 / 注册表 / 快捷方式主脚本
- `installer\ChineseSimplified.isl` —— 简体中文语言文件（Inno Setup 6.5.0+ 官方中文翻译，UTF-8 无 BOM）
- `scripts\build-installer.cmd` —— 一键构建与产物校验

重建前需先通过 `scripts\publish.ps1` 产出 `artifacts/win-x64-self-contained/Syspulse.exe`。

---

## 9. 应用图标

采用与界面强调色一致的品牌蓝圆角方块，配一条白色上升监控曲线、半透明面积填充、基线与端点光环；单一粗笔画保证 16×16 下仍能辨识。

| 文件 | 用途 |
| --- | --- |
| `scripts/make_icon.py` | 图标生成器，仅用 Python 标准库，可复现、无需设计软件 |
| `installer/app-icon.svg` | 可编辑的矢量设计稿 |
| `src/Syspulse.App/Assets/app.ico` | 9 种尺寸（16–256）的 ICO，用于 Win32 exe 与安装包 |
| `src/Syspulse.App/Assets/app-icon.png` | 256×256 PNG，用于 Avalonia 窗口标题栏 / 任务栏 |
| `installer/app.ico` | ICO 副本，供 Inno Setup 的 `SetupIconFile` 使用 |

重新生成只需执行 `python scripts/make_icon.py`。图标通过三处接入：`.csproj` 的 `ApplicationIcon`（exe）、`Services/AppIcon.cs` 与各窗口的 `Icon` 属性（Avalonia 窗口与对话框）、Inno Setup 的 `SetupIconFile`（安装包向导与 setup.exe）。改动图标后需依次重新 build、发布便携版、构建安装包。

---

## 10. 功能回归探针

`tests/FunctionalProbe/` 是一个**独立、只读**的控制台探针（不加入解决方案，不影响主程序构建），用于在不修改系统的前提下逐个验证业务层能力。

```bash
dotnet run --project tests/FunctionalProbe/FunctionalProbe.csproj -c Release
```

覆盖 34 项：配置与日志、整机概览与 TXT/MD/HTML 报告导出、CPU/内存/GPU/磁盘/电池/显示器、传感器与进程采集、硬件读数校验、垃圾清理扫描与注册表备份、启动项/服务/调优项、大文件/重复文件查找、网络延迟与更新检测、路径护栏与非法参数拒绝。结果写入 `artifacts/functional-test-report.txt`。

探针**不执行**任何删除、注册表写入、服务启停、进程结束或系统修复命令；写操作只验证「应被拒绝」的校验路径。注册表备份会调用 `reg.exe`，在禁止该程序的安全沙箱里会返回 `Success=false`，属于环境限制而非代码缺陷。

---

## 11. 硬件读数说明

### 11.1 显存容量的取值优先级

`Win32_VideoController.AdapterRAM` 是 **32 位字段**，8 GB / 12 GB 独显常回绕成 2 GB 或饱和到 4 GB，这是此前「显存显示不准」的根因。现在按以下顺序取值：

| 优先级 | 来源 | 标注 |
| --- | --- | --- |
| 1 | 显卡驱动注册表 `HardwareInformation.qwMemorySize`（REG_QWORD，64 位） | 专用显存，精确值 |
| 2 | 同名驱动的 `HardwareInformation.MemorySize`（REG_DWORD） | 专用显存，精确值 |
| 3 | WMI `AdapterRAM`（仅当明显低于 4 GB 上限时才可信） | 提示可能受 4 GB 上限限制 |
| 4 | 两者都不可信 | 显示 N/A，不编造容量 |

注册表路径为 `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-...}\000N`，普通用户权限即可读取；名称用精确 / 包含 / 双词重叠三级匹配，避免把虚拟显示器误配到实体显卡，同型号多卡按索引逐个配对。

### 11.2 系统名称

`Win32_OperatingSystem.Caption` 在 Windows 11 上仍返回 `Microsoft Windows 10 ...`，而注册表 `ProductName` 同样是遗留的 `Windows 10 Pro`。早期代码把两者拼在一起，导致「系统」一行同时出现两个互相矛盾的名字。现在只保留 Caption，并按内部版本号（`BuildNumber >= 22000`）把字面 `Windows 10` 规范化为 `Windows 11`，再补 `DisplayVersion`（如 24H2）。「内核版本」行显示 `10.0.xxxxx` 属于 Windows NT 内核版本，不代表产品名称。
