namespace BatteryWidget.Models;

public sealed class HidDeviceProfile
{
    public string Name { get; set; } = string.Empty;
    public DeviceCategory Category { get; set; } = DeviceCategory.Other;
    public string VendorIdHex { get; set; } = "0x0000";
    public List<string> ProductIdsHex { get; set; } = [];
    public string? UsagePageHex { get; set; }
    public string? QueryPacketHex { get; set; }
    public bool UseFeatureReport { get; set; }
    public int BatteryByteIndex { get; set; } = 1;
    public int? ChargingByteIndex { get; set; }
    public int ChargingMask { get; set; } = 0x01;
    public int MaxBatteryValue { get; set; } = 100;

    public int ParseVendorId() => ParseHex(VendorIdHex);
    public HashSet<int> ParseProductIds() => ProductIdsHex.Select(ParseHex).ToHashSet();
    public int? ParseUsagePage() => string.IsNullOrWhiteSpace(UsagePageHex) ? null : ParseHex(UsagePageHex);

    public byte[]? ParseQueryPacket()
    {
        if (string.IsNullOrWhiteSpace(QueryPacketHex))
            return null;

        var parts = QueryPacketHex
            .Replace("-", " ")
            .Replace(",", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bytes[i] = Convert.ToByte(parts[i].Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
        }
        return bytes;
    }

    private static int ParseHex(string value)
    {
        var clean = value.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        return Convert.ToInt32(clean, 16);
    }
}
