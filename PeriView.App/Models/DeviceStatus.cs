namespace PeriView.App.Models;

public sealed class DeviceStatus
{
    public string DeviceKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool? IsConnected { get; set; }
    public int? BatteryPercent { get; set; }
    public bool? IsCharging { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.Now;
    public string? Error { get; set; }
    public string? DebugProperties { get; set; }
}
