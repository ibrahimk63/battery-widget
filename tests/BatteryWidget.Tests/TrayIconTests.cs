using System;
using System.Collections.Generic;
using System.Linq;
using BatteryWidget.Models;
using BatteryWidget.UI;
using Xunit;

namespace BatteryWidget.Tests;

public class TrayIconTests
{
    [Fact]
    public void FormatTooltipText_EmptyDevices_ReturnsDisconnectedText()
    {
        string tooltip = TrayIconManager.FormatTooltipText(Array.Empty<DeviceBatteryInfo>());
        Assert.Equal("Battery Dashboard — Bağlı cihaz yok", tooltip);
    }

    [Fact]
    public void FormatTooltipText_MultipleDevices_FormatsTopThreeAndFitsNotifyIconLimit()
    {
        var devices = new List<DeviceBatteryInfo>
        {
            new() { Id = "dev-1", Name = "VeryLongMouseNameExceedingLimit", BatteryPercent = 82 },
            new() { Id = "dev-2", Name = "Keyboard Pro", BatteryPercent = 45 },
            new() { Id = "dev-3", Name = "Headset X", BatteryPercent = 100 },
            new() { Id = "dev-4", Name = "Fourth Device", BatteryPercent = 12 }
        };

        string tooltip = TrayIconManager.FormatTooltipText(devices);

        Assert.True(tooltip.Length <= 127);
        Assert.Contains("VeryLongMouseN: 82%", tooltip);
        Assert.Contains("Keyboard Pro: 45%", tooltip);
        Assert.Contains("Headset X: 100%", tooltip);
        Assert.DoesNotContain("Fourth", tooltip);
    }

    [Fact]
    public void TruncateForNotifyIcon_BoundsTitleAndBalloonLengths()
    {
        Assert.Equal(string.Empty, TrayIconManager.TruncateForNotifyIcon(null, 63));
        Assert.Equal(string.Empty, TrayIconManager.TruncateForNotifyIcon(string.Empty, 63));

        string exact63 = new('a', 63);
        Assert.Equal(exact63, TrayIconManager.TruncateForNotifyIcon(exact63, 63));

        string over63 = new('b', 80);
        string truncated63 = TrayIconManager.TruncateForNotifyIcon(over63, 63);
        Assert.Equal(63, truncated63.Length);
        Assert.EndsWith("...", truncated63);

        string over255 = new('c', 300);
        string truncated255 = TrayIconManager.TruncateForNotifyIcon(over255, 255);
        Assert.Equal(255, truncated255.Length);
        Assert.EndsWith("...", truncated255);
    }

    [Fact]
    public void LoadEmbeddedAppIconBytes_AndExtractBestIcon_ReturnsValidPayload()
    {
        byte[]? icoBytes = TrayIconManager.LoadEmbeddedAppIconBytes();
        Assert.NotNull(icoBytes);
        Assert.True(icoBytes.Length > 22);

        byte[]? iconPayload = TrayIconManager.ExtractBestIconFromIcoBytes(icoBytes, 16, 16);
        Assert.NotNull(iconPayload);
        Assert.True(iconPayload.Length > 0);
    }

    [Fact]
    public void BatteryWidgetAssembly_DoesNotReferenceWinFormsOrSystemDrawing()
    {
        var referencedNames = typeof(TrayIconManager).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("System.Windows.Forms", referencedNames);
        Assert.DoesNotContain("System.Windows.Forms.Primitives", referencedNames);
        Assert.DoesNotContain("System.Drawing", referencedNames);
        Assert.DoesNotContain("System.Drawing.Common", referencedNames);
    }
}
