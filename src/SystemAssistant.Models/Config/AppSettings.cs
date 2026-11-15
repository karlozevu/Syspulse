using SystemAssistant.Models.Hardware;

namespace SystemAssistant.Models.Config;

/// <summary>主题模式。</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>界面语言。</summary>
public enum LanguageMode
{
    /// <summary>简体中文</summary>
    ZhCn,

    /// <summary>English</summary>
    EnUs
}

/// <summary>用户设置。使用 System.Text.Json 持久化到用户目录，不包含任何硬件/扫描数据。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    /// <summary>传感器刷新间隔（毫秒），限制在 500~60000。</summary>
    public int RefreshIntervalMs { get; set; } = 2000;

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public LanguageMode Language { get; set; } = LanguageMode.ZhCn;
    public string DefaultExportDirectory { get; set; } = string.Empty;
    public string RegistryBackupDirectory { get; set; } = string.Empty;
    public IReadOnlyList<Cleaning.WhitelistRule> Whitelist { get; set; } = Array.Empty<Cleaning.WhitelistRule>();
    public IReadOnlyList<string> CustomScanPaths { get; set; } = Array.Empty<string>();
    // 默认给一个全局温度阈值，用户可在设置页修改或清空
    public IReadOnlyList<Hardware.SensorThreshold> Thresholds { get; set; } = new[]
    {
        new Hardware.SensorThreshold
        {
            SensorIdentifier = string.Empty,
            Kind = SensorKind.Temperature,
            MaxValue = 85,
            Enabled = true
        }
    };

    /// <summary>更新检测地址，留空表示禁用（默认不联网）。</summary>
    public string UpdateCheckUrl { get; set; } = string.Empty;

    public bool ConfirmHighRiskOperations { get; set; } = true;

    public int ClampRefreshInterval() => Math.Clamp(RefreshIntervalMs, 500, 60_000);
}
