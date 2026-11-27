using BatteryWidget.Services;
using Xunit;

namespace BatteryWidget.Tests;

public class TaskbarDockTests
{
    private static readonly ScreenRect BottomTaskbar = new(0, 1032, 1920, 1080);
    private static readonly ScreenRect FullMonitor = new(0, 0, 1920, 1080);

    [Fact]
    public void ShouldDockOnDrop_WhenCursorInsideTaskbar_ReturnsTrue()
    {
        var window = new ScreenRect(1200, 900, 1516, 1075);

        bool shouldDock = TaskbarDockService.ShouldDockOnDrop(window, 1350, 1055, BottomTaskbar);

        Assert.True(shouldDock);
    }

    [Fact]
    public void ShouldDockOnDrop_WhenWindowOnDesktop_ReturnsFalse()
    {
        var window = new ScreenRect(1400, 750, 1716, 960);

        bool shouldDock = TaskbarDockService.ShouldDockOnDrop(window, 1550, 820, BottomTaskbar);

        Assert.False(shouldDock);
    }

    [Fact]
    public void ShouldDockOnDrop_WhenWindowOverlapsTaskbarCenterInBounds_ReturnsTrue()
    {
        var window = new ScreenRect(800, 860, 1116, 1056);

        bool shouldDock = TaskbarDockService.ShouldDockOnDrop(window, 950, 1015, BottomTaskbar);

        Assert.True(shouldDock);
    }

    [Fact]
    public void ShouldUndockOnDrop_WhenDraggedAboveThreshold_ReturnsTrue()
    {
        var window = new ScreenRect(900, 960, 1120, 990);

        bool shouldUndock = TaskbarDockService.ShouldUndockOnDrop(1000, 990, window, BottomTaskbar);

        Assert.True(shouldUndock);
    }

    [Fact]
    public void ShouldUndockOnDrop_WhenDraggedHorizontallyAlongTaskbar_ReturnsFalse()
    {
        var window = new ScreenRect(600, 1042, 820, 1070);

        bool shouldUndock = TaskbarDockService.ShouldUndockOnDrop(710, 1056, window, BottomTaskbar);

        Assert.False(shouldUndock);
    }

    [Fact]
    public void ComputeDockedPosition_PreservesDesiredXAndCentersVertically()
    {
        var (left, top) = TaskbarDockService.ComputeDockedPosition(850, 220, 28, BottomTaskbar);

        Assert.Equal(850, left);
        Assert.Equal(1042, top);
    }

    [Fact]
    public void ComputeDockedPosition_ClampsBeyondRightEdge()
    {
        var (left, top) = TaskbarDockService.ComputeDockedPosition(1880, 220, 28, BottomTaskbar, edgePaddingDip: 8);

        Assert.Equal(1920 - 220 - 8, left);
        Assert.Equal(1042, top);
    }

    [Fact]
    public void ComputeUndockedPosition_ClampsInsideWorkArea()
    {
        var workArea = new ScreenRect(0, 0, 1920, 1032);

        var (left, top) = TaskbarDockService.ComputeUndockedPosition(-50, 1010, 316, 210, workArea, marginDip: 12);

        Assert.Equal(12, left);
        Assert.Equal(1032 - 210 - 12, top);
    }

    [Fact]
    public void IsTaskbarAutoHidden_DetectsSlidOffScreenTaskbar()
    {
        var slidOff = new ScreenRect(0, 1078, 1920, 1126);

        Assert.True(TaskbarDockService.IsTaskbarAutoHidden(slidOff, FullMonitor));
        Assert.False(TaskbarDockService.IsTaskbarAutoHidden(BottomTaskbar, FullMonitor));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void IsNotificationStateFullscreen_DetectsFullscreenStates(int quns, bool expected)
    {
        Assert.Equal(expected, TaskbarDockService.IsNotificationStateFullscreen(quns));
    }

    [Theory]
    [InlineData("HyperX Cloud III Wireless", "HyperX Cloud…")]
    [InlineData("Darmoshark M3s", "Darmoshark M3s")]
    [InlineData("Logitech G PRO X SUPERLIGHT 2 Gaming Mouse", "Logitech G PR…")]
    [InlineData("", "Cihaz")]
    public void FormatShortDeviceName_FormatsCompactTaskbarLabels(string input, string expected)
    {
        string result = TaskbarDockService.FormatShortDeviceName(input, maxChars: 14);
        Assert.Equal(expected, result);
        Assert.True(result.Length <= 14);
    }
}
