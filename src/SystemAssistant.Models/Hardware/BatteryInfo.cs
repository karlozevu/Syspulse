namespace SystemAssistant.Models.Hardware;

/// <summary>笔记本电池信息。台式机无电池时 <see cref="IsPresent"/> 为 false。</summary>
public sealed class BatteryInfo
{
    public bool IsPresent { get; init; }

    /// <summary>设计容量（mWh）。</summary>
    public int DesignCapacityMwh { get; init; }

    /// <summary>当前满充容量（mWh）。</summary>
    public int FullChargeCapacityMwh { get; init; }

    /// <summary>剩余电量（mWh）。</summary>
    public int RemainingCapacityMwh { get; init; }

    /// <summary>电池损耗百分比（0 = 无损耗，100 = 完全失效）。</summary>
    public double WearPercent => DesignCapacityMwh <= 0
        ? 0
        : Math.Clamp((1 - FullChargeCapacityMwh * 1.0 / DesignCapacityMwh) * 100, 0, 100);

    /// <summary>剩余电量百分比（0~100）。</summary>
    public double ChargePercent => FullChargeCapacityMwh <= 0
        ? 0
        : Math.Clamp(RemainingCapacityMwh * 100.0 / FullChargeCapacityMwh, 0, 100);

    /// <summary>充电循环次数，-1 表示设备未报告。</summary>
    public int CycleCount { get; init; } = -1;

    public bool IsCharging { get; init; }
    public string Chemistry { get; init; } = string.Empty;
}
