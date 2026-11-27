using System.Text;
using BatteryWidget.Providers;
using HidSharp;

namespace BatteryWidget.Services;

public static class HidDiagnosticService
{
    public static string GenerateHidReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("VID      | PID      | In  | Out | Feat | Ürün Adı");
        sb.AppendLine(new string('-', 76));

        var devices = DeviceList.Local.GetHidDevices()
            .OrderBy(d => d.VendorID)
            .ThenBy(d => d.ProductID)
            .ToList();

        foreach (var d in devices)
        {
            string product;
            try { product = d.GetProductName(); }
            catch { product = "(Bilinmiyor)"; }

            sb.AppendLine(
                $"0x{d.VendorID:X4}   | 0x{d.ProductID:X4}   | {d.GetMaxInputReportLength(),3} | {d.GetMaxOutputReportLength(),3} | {d.GetMaxFeatureReportLength(),4} | {product}");
        }

        if (devices.Count == 0)
            sb.AppendLine("Hiçbir HID cihazı bulunamadı.");

        return sb.ToString();
    }

    public static string ProbeKnownDevices()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== HyperX (0x03F0:0x05B7) & Darmoshark (0x0BDA:0xFFE0) Paket Testi ===");

        // HyperX 62-byte probe
        var hx = DeviceList.Local.GetHidDevices(0x03F0, 0x05B7)
            .FirstOrDefault(d => d.GetMaxOutputReportLength() == 62);
        if (hx is not null)
        {
            sb.AppendLine("[HyperX 0x03F0:0x05B7] 62-byte arayüz bulundu.");
            var resp = TryProbeOutput(hx, [0x66, 0x89], sb);
            if (resp is not null)
            {
                var parsed = HyperXCloud3Provider.ParseBatteryResponse(resp);
                if (parsed.HasValue)
                    sb.AppendLine($"  => Çözümlenen Pil: %{parsed.Value.Percent} (Şarj: {parsed.Value.IsCharging})");
            }
        }

        // Darmoshark 64-byte probe
        var dm = DeviceList.Local.GetHidDevices(0x0BDA, 0xFFE0)
            .FirstOrDefault(d => d.GetMaxOutputReportLength() == 64 && d.GetMaxInputReportLength() == 64);
        if (dm is not null)
        {
            sb.AppendLine("[Darmoshark 0x0BDA:0xFFE0] 64-byte kontrol arayüzü bulundu.");
            var resp = TryProbeOutput(dm, [0xB5, 0x05], sb);
            if (resp is not null)
            {
                var parsed = DarmosharkM3V2Provider.ParseBatteryResponse(resp);
                if (parsed.HasValue)
                    sb.AppendLine($"  => Çözümlenen Pil: %{parsed.Value.Percent} (Şarj: {parsed.Value.IsCharging})");
            }
        }
        else
        {
            sb.AppendLine("[Darmoshark 0x0BDA:0xFFE0] 64-byte kontrol arayüzü bulunamadı.");
        }

        return sb.ToString();
    }

    private static byte[]? TryProbeOutput(HidDevice dev, byte[] cmd, StringBuilder sb)
    {
        try
        {
            if (!dev.TryOpen(out var stream))
            {
                sb.AppendLine($"  Komut {BitConverter.ToString(cmd)}: Açılamadı.");
                return null;
            }
            using (stream)
            {
                stream.ReadTimeout = 400;
                stream.WriteTimeout = 400;
                var outBuf = new byte[dev.GetMaxOutputReportLength()];
                Array.Copy(cmd, outBuf, Math.Min(cmd.Length, outBuf.Length));
                stream.Write(outBuf, 0, outBuf.Length);

                var inBuf = new byte[dev.GetMaxInputReportLength()];
                int read = stream.Read(inBuf, 0, inBuf.Length);
                sb.AppendLine($"  Komut {BitConverter.ToString(cmd)} -> Yanıt ({read}B): {BitConverter.ToString(inBuf, 0, Math.Min(read, 24))}");
                return inBuf[..read];
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Komut {BitConverter.ToString(cmd)} -> Hata: {ex.Message}");
            return null;
        }
    }
}
