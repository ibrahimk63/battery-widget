namespace BatteryWidget.Models;

public enum DeviceCategory
{
    Headset,
    Mouse,
    Keyboard,
    Gamepad,
    Other
}

public enum ConnectionKind
{
    Wireless24Ghz,
    Bluetooth,
    UsbWired
}

public sealed record DeviceBatteryInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceCategory Category { get; init; } = DeviceCategory.Other;
    public ConnectionKind Connection { get; init; } = ConnectionKind.Wireless24Ghz;
    public int BatteryPercent { get; init; } = -1;
    public string? DisplayLevelText { get; init; }
    public int? VisualFillPercent { get; init; }
    public bool IsCharging { get; init; }
    public bool IsConnected { get; init; } = true;
    public DateTimeOffset LastUpdated { get; init; } = DateTimeOffset.Now;
    public string? StatusNote { get; init; }

    public string IconGlyph => Category switch
    {
        DeviceCategory.Headset => "🎧",
        DeviceCategory.Mouse => "🖱️",
        DeviceCategory.Keyboard => "⌨️",
        DeviceCategory.Gamepad => "🎮",
        _ => "🔋"
    };

    public string ConnectionLabel => Connection switch
    {
        ConnectionKind.Wireless24Ghz => "2.4 GHz",
        ConnectionKind.Bluetooth => "Bluetooth",
        ConnectionKind.UsbWired => "USB",
        _ => ""
    };

    public string PercentText =>
        !string.IsNullOrEmpty(DisplayLevelText) ? DisplayLevelText :
        BatteryPercent >= 0 ? $"{BatteryPercent}%" : "--";

    public int EffectiveRingPercent =>
        BatteryPercent >= 0 ? BatteryPercent : (VisualFillPercent ?? -1);
}
