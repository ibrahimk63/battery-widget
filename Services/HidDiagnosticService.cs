using System.Text;
using BatteryWidget.Models;
using HidSharp;
using Microsoft.Win32;

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

    public static Task<string> GenerateUniversalDiagnosticsAsync(IReadOnlyList<DeviceBatteryInfo> activeDevices)
    {
        return Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 1. ALGILANAN AKTİF AYGITLAR ===");
            if (activeDevices.Count == 0)
            {
                sb.AppendLine("Şu anda algılanan pil bildiren aygıt yok.");
            }
            else
            {
                foreach (var dev in activeDevices)
                {
                    string state = dev.IsConnected ? (dev.IsCharging ? "Şarj Oluyor" : "Bağlı") : (dev.StatusNote ?? "Bağlı Değil");
                    sb.AppendLine($"- {dev.Name} [{dev.ConnectionLabel}] -> {dev.PercentText} ({state}) | ID: {dev.Id}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== 2. BU BİLGİSAYARDAKİ VENDOR USB / 2.4GHz HID AYGITLARI ===");
            var vendorGroups = DeviceList.Local.GetHidDevices()
                .Where(d => d.VendorID > 0 && (d.GetMaxOutputReportLength() >= 8 || d.GetMaxFeatureReportLength() >= 8))
                .GroupBy(d => (d.VendorID, d.ProductID))
                .OrderBy(g => g.Key.VendorID)
                .ThenBy(g => g.Key.ProductID)
                .ToList();

            if (vendorGroups.Count == 0)
            {
                sb.AppendLine("Özel kontrol kanalı olan USB HID aygıtı bulunamadı.");
            }
            else
            {
                foreach (var group in vendorGroups)
                {
                    string name = "(Bilinmiyor)";
                    foreach (var d in group)
                    {
                        try
                        {
                            var n = d.GetProductName();
                            if (!string.IsNullOrWhiteSpace(n)) { name = n.Trim(); break; }
                        }
                        catch { /* ignore */ }
                    }

                    sb.AppendLine($"[0x{group.Key.VendorID:X4}:0x{group.Key.ProductID:X4}] {name}");
                    foreach (var d in group)
                    {
                        sb.AppendLine($"  Arayüz -> In:{d.GetMaxInputReportLength()} Out:{d.GetMaxOutputReportLength()} Feat:{d.GetMaxFeatureReportLength()}");
                        try
                        {
                            var desc = d.GetReportDescriptor();
                            var inIds = desc.InputReports.Select(r => $"0x{r.ReportID:X2}({r.Length}B)").ToList();
                            var outIds = desc.OutputReports.Select(r => $"0x{r.ReportID:X2}({r.Length}B)").ToList();
                            var featIds = desc.FeatureReports.Select(r => $"0x{r.ReportID:X2}({r.Length}B)").ToList();

                            if (inIds.Count > 0) sb.AppendLine($"    In IDs  : {string.Join(", ", inIds)}");
                            if (outIds.Count > 0) sb.AppendLine($"    Out IDs : {string.Join(", ", outIds)}");
                            if (featIds.Count > 0) sb.AppendLine($"    Feat IDs: {string.Join(", ", featIds)}");
                        }
                        catch
                        {
                            sb.AppendLine("    Descriptor okunamadı.");
                        }
                    }
                }
            }

            sb.AppendLine();
            sb.AppendLine("=== 3. USB SERİ / SANAL COM PORTLARI ===");
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
                if (key is null || key.ValueCount == 0)
                {
                    sb.AppendLine("Aktif sanal COM portu bulunamadı.");
                }
                else
                {
                    foreach (var valName in key.GetValueNames())
                    {
                        var com = key.GetValue(valName)?.ToString();
                        sb.AppendLine($"- {com} ({valName})");
                    }
                }
            }
            catch
            {
                sb.AppendLine("COM port listesi okunamadı.");
            }

            return sb.ToString();
        });
    }
}
