namespace BatteryWidget.Models;

public sealed class HidDeviceProfile
{
    public string Name { get; set; } = string.Empty;
    public DeviceCategory Category { get; set; } = DeviceCategory.Other;
    public string VendorIdHex { get; set; } = "0x0000";
    public List<string> ProductIdsHex { get; set; } = [];
    public List<string> WiredProductIdsHex { get; set; } = [];
    public string? UsagePageHex { get; set; }
    public string? QueryPacketHex { get; set; }
    public string? WakeupPacketHex { get; set; }
    public bool UseFeatureReport { get; set; }
    public bool ReadPassiveInput { get; set; }
    public bool MergeProductIds { get; set; }
    public int ExpectedReportId { get; set; } = -1;
    public int? LinkStatusByteIndex { get; set; }
    public int LinkStatusOnlineValue { get; set; } = 1;
    public int BatteryByteIndex { get; set; } = 1;
    public int BatteryMask { get; set; } = 0xFF;
    public int? ChargingByteIndex { get; set; }
    public int ChargingMask { get; set; } = 0x01;
    public int? ChargingEqualsValue { get; set; }
    public int PassiveStaleSeconds { get; set; } = 25;
    public int MaxBatteryValue { get; set; } = 100;
    public string ChecksumMode { get; set; } = "None";
    public int ChecksumByteIndex { get; set; } = -1;
    public int? VoltageHighByteIndex { get; set; }
    public int? VoltageLowByteIndex { get; set; }

    public int ParseVendorId() => ParseHex(VendorIdHex);
    public HashSet<int> ParseProductIds() => ProductIdsHex.Select(ParseHex).ToHashSet();
    public HashSet<int> ParseWiredProductIds() => WiredProductIdsHex.Select(ParseHex).ToHashSet();
    public int? ParseUsagePage() => string.IsNullOrWhiteSpace(UsagePageHex) ? null : ParseHex(UsagePageHex);

    public byte[]? ParseQueryPacket() => ParseHexBytes(QueryPacketHex);
    public byte[]? ParseWakeupPacket() => ParseHexBytes(WakeupPacketHex);

    public void ApplyChecksum(byte[] buffer)
    {
        if (buffer.Length < 4 || string.IsNullOrWhiteSpace(ChecksumMode) ||
            string.Equals(ChecksumMode, "None", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(ChecksumMode, "SumMod256", StringComparison.OrdinalIgnoreCase))
        {
            int idx = ChecksumByteIndex >= 0 && ChecksumByteIndex < buffer.Length
                ? ChecksumByteIndex
                : buffer.Length - 1;
            int sum = 0;
            for (int i = 1; i < idx; i++)
                sum += buffer[i];
            buffer[idx] = (byte)(sum & 0xFF);
        }
        else if (string.Equals(ChecksumMode, "Compx55", StringComparison.OrdinalIgnoreCase))
        {
            int idx = ChecksumByteIndex >= 0 && ChecksumByteIndex < buffer.Length
                ? ChecksumByteIndex
                : Math.Min(16, buffer.Length - 1);
            int sum = 0;
            for (int i = 0; i < idx; i++)
                sum += buffer[i];
            buffer[idx] = (byte)((0x55 - sum) & 0xFF);
        }
        else if (string.Equals(ChecksumMode, "XorCrc", StringComparison.OrdinalIgnoreCase))
        {
            int idx = ChecksumByteIndex >= 0 && ChecksumByteIndex < buffer.Length
                ? ChecksumByteIndex
                : buffer.Length - 2;
            byte xor = 0;
            for (int i = 2; i < idx; i++)
                xor ^= buffer[i];
            buffer[idx] = xor;
        }
    }

    private static byte[]? ParseHexBytes(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        var parts = hex
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
