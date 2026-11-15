# Changelog

本项目遵循语义化版本约定。所有重要变更记录在此。

## [1.0.0] - 2026-11-15

首个可编译运行版本。

### 新增 · 架构
- 四层解决方案：`SystemAssistant.Models`（纯实体）/ `Utils`（平台工具）/ `Core`（业务，零 UI 依赖）/ `App`（Avalonia 11）。
- `Core/Common/ServiceHub.cs` 作为组合根，`Interfaces/ISystemAssistantServices.cs` 定义 UI 与业务的全部契约，便于后续把 UI 层整体替换为 WPF。
- 后台采集器（传感器、进程）使用 `PeriodicTimer` + 事件推送，UI 通过 `Dispatcher.UIThread` 更新，避免阻塞。

### 新增 · 模块一 硬件信息与实时监控
- 整机概览：OS / 主板 / BIOS / CPU / 内存 / 显卡 / 磁盘 / 网卡 / 电池 / 显示器，支持 TXT、Markdown、HTML 导出。
- CPU、内存、GPU、磁盘（含 SMART 健康与警告 / 故障标记）详情。
- LibreHardwareMonitorLib 驱动的温度 / 风扇 / 电压实时曲线，刷新间隔与阈值可配置，超阈值弹窗告警。
- 电池损耗 / 循环次数、多显示器分辨率 / 刷新率 / 位深 / HDR 状态。

### 新增 · 模块二 安全垃圾清理
- 系统垃圾、Edge / Chrome / Firefox 缓存、应用缓存、保守注册表残留、自定义路径扫描。
- 白名单（目录 / 文件名 / 后缀，支持通配符）在扫描与删除阶段双重校验。
- 「扫描 → 分组勾选 → 明细确认 → 高危二次确认 → 进度 → 日志」强制流程；`PathGuard` 路径护栏。
- 注册表删除前自动 `reg.exe export` 备份到用户选择目录并生成还原说明。

### 新增 · 模块三 系统优化
- 启动项、Windows 服务（高风险服务文字警告）、sfc / DISM 内嵌控制台、SSD TRIM / HDD 碎片整理、网络工具箱、简易系统调优与虚拟内存入口。

### 新增 · 模块四 进程与资源
- 实时进程列表（可排序列）、结束进程 / 进程树、模块与文件句柄详情、可置顶的独立悬浮仪表盘。

### 新增 · 模块五 独立工具箱
- 多遍覆写文件粉碎、大文件查找、SHA256 重复文件查找（每组至少保留一份）、系统快捷入口。

### 新增 · 模块六 通用基础
- 浅色 / 深色 / 跟随系统主题；简体中文 / English 即时切换；设置持久化；操作日志查看 / 导出 / 清空；管理员权限检测与按需 UAC 提升；仅检测不安装的更新检查（默认不联网）。

### 新增 · 交付
- `README.md`、`scripts/build.cmd`、`scripts/publish.ps1` / `publish.cmd`（框架依赖与自包含两种单文件绿色便携版）。

### 技术决策
- 采用 Avalonia 11.3.x + .NET 9；`OxyPlot.Avalonia` 无兼容版本（2.1 → Avalonia 0.10，2.2 → .NET 10 / Avalonia 12），实时曲线改为 `LineChartControl` 自绘实现。
- `LibreHardwareMonitorLib` 固定 0.9.4（含 `net8.0` 资产与 System.Management 9.x 依赖），避免预发布版对 .NET 10/11 的依赖。
- 所有删除 / 注册表 / 服务 / 系统调优操作均由 `DialogService` 强制两次确认，默认勾选策略保守，程序不静默修改系统设置，不上传任何用户数据。

### 已知限制
- WMI 无法提供 CPU 睿频规格与内存通道数；SMART / HDR / 文件句柄为尽力而为读取；文件粉碎无法保证 SSD + TRIM 场景的物理不可恢复。详见 `README.md` 第 6 节。
