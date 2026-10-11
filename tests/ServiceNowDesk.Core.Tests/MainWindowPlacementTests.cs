using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class MainWindowPlacementTests
{
    private static readonly MainWindowPlacement.Rect WorkArea = new(0, 0, 1920, 1080);

    [Fact]
    public void MissingSettingsUseDefaultSizeCentredOnWorkArea()
    {
        var placement = MainWindowPlacement.Resolve(null, WorkArea);

        Assert.Equal(MainWindowPlacement.DefaultWidth, placement.Width);
        Assert.Equal(MainWindowPlacement.DefaultHeight, placement.Height);
        Assert.Equal((WorkArea.Width - MainWindowPlacement.DefaultWidth) / 2, placement.Left);
        Assert.Equal((WorkArea.Height - MainWindowPlacement.DefaultHeight) / 2, placement.Top);
        Assert.False(placement.Maximized);
    }

    [Fact]
    public void SavedSizeAndPositionAreRestored()
    {
        var settings = new DeskSettings
        {
            WindowLeft = 120,
            WindowTop = 80,
            WindowWidth = 1400,
            WindowHeight = 900,
            WindowState = MainWindowPlacement.StateNormal
        };

        var placement = MainWindowPlacement.Resolve(settings, WorkArea);

        Assert.Equal(120, placement.Left);
        Assert.Equal(80, placement.Top);
        Assert.Equal(1400, placement.Width);
        Assert.Equal(900, placement.Height);
        Assert.False(placement.Maximized);
    }

    [Fact]
    public void MaximizedStateIsRestored()
    {
        var settings = new DeskSettings
        {
            WindowLeft = 40,
            WindowTop = 20,
            WindowWidth = 1100,
            WindowHeight = 700,
            WindowState = "maximized"
        };

        var placement = MainWindowPlacement.Resolve(settings, WorkArea);

        Assert.True(placement.Maximized);
        Assert.Equal(1100, placement.Width);
        Assert.Equal(700, placement.Height);
    }

    [Fact]
    public void SizeIsClampedToWorkAreaAndMinimum()
    {
        var huge = MainWindowPlacement.Resolve(
            new DeskSettings { WindowWidth = 5000, WindowHeight = 4000 },
            WorkArea);
        Assert.Equal(WorkArea.Width, huge.Width);
        Assert.Equal(WorkArea.Height, huge.Height);

        var tiny = MainWindowPlacement.Resolve(
            new DeskSettings { WindowWidth = 100, WindowHeight = 50 },
            WorkArea);
        Assert.Equal(MainWindowPlacement.MinWidth, tiny.Width);
        Assert.Equal(MainWindowPlacement.MinHeight, tiny.Height);
    }

    [Fact]
    public void OffScreenPositionIsNudgedOntoWorkArea()
    {
        var settings = new DeskSettings
        {
            WindowLeft = 5000,
            WindowTop = -200,
            WindowWidth = 1100,
            WindowHeight = 700
        };

        var placement = MainWindowPlacement.Resolve(settings, WorkArea);

        Assert.InRange(placement.Left, WorkArea.Left + MainWindowPlacement.VisibleEdge - placement.Width, WorkArea.Left + WorkArea.Width - MainWindowPlacement.VisibleEdge);
        Assert.InRange(placement.Top, WorkArea.Top, WorkArea.Top + WorkArea.Height - MainWindowPlacement.VisibleEdge);
    }

    [Fact]
    public void InvalidNumbersFallBackToDefaults()
    {
        var settings = new DeskSettings
        {
            WindowWidth = double.NaN,
            WindowHeight = double.PositiveInfinity,
            WindowLeft = double.NaN,
            WindowTop = double.NegativeInfinity
        };

        var placement = MainWindowPlacement.Resolve(settings, WorkArea);

        Assert.Equal(MainWindowPlacement.DefaultWidth, placement.Width);
        Assert.Equal(MainWindowPlacement.DefaultHeight, placement.Height);
        Assert.Equal((WorkArea.Width - MainWindowPlacement.DefaultWidth) / 2, placement.Left);
        Assert.Equal((WorkArea.Height - MainWindowPlacement.DefaultHeight) / 2, placement.Top);
    }

    [Fact]
    public void CaptureWritesNormalizedPlacementOntoSettings()
    {
        var settings = new DeskSettings();
        MainWindowPlacement.Capture(settings, 32, 48, 1300, 820, "Minimized");

        Assert.Equal(32, settings.WindowLeft);
        Assert.Equal(48, settings.WindowTop);
        Assert.Equal(1300, settings.WindowWidth);
        Assert.Equal(820, settings.WindowHeight);
        Assert.Equal(MainWindowPlacement.StateNormal, settings.WindowState);

        MainWindowPlacement.Capture(settings, 10, 10, 1200, 800, MainWindowPlacement.StateMaximized);
        Assert.Equal(MainWindowPlacement.StateMaximized, settings.WindowState);
    }

    [Fact]
    public void CaptureIgnoresInvalidSize()
    {
        var settings = new DeskSettings { WindowWidth = 1280, WindowHeight = 800 };
        MainWindowPlacement.Capture(settings, 0, 0, double.NaN, 800, MainWindowPlacement.StateNormal);

        Assert.Equal(1280, settings.WindowWidth);
        Assert.Equal(800, settings.WindowHeight);
    }

    [Fact]
    public void SettingsFileRoundTripsWindowPlacement()
    {
        var settings = new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            WindowLeft = 64,
            WindowTop = 96,
            WindowWidth = 1500,
            WindowHeight = 880,
            WindowState = MainWindowPlacement.StateMaximized
        };

        var json = DeskSettingsFile.Serialize(settings, text => text ?? "");
        var loaded = DeskSettingsFile.Deserialize(json, text => text ?? "");

        Assert.Equal(64, loaded.WindowLeft);
        Assert.Equal(96, loaded.WindowTop);
        Assert.Equal(1500, loaded.WindowWidth);
        Assert.Equal(880, loaded.WindowHeight);
        Assert.Equal(MainWindowPlacement.StateMaximized, loaded.WindowState);
    }

    [Fact]
    public void OlderSettingsFileLeavesWindowPlacementUnset()
    {
        var loaded = DeskSettingsFile.Deserialize(
            "{\"InstanceUrl\":\"https://example.service-now.com\"}",
            text => text ?? "");

        Assert.Null(loaded.WindowWidth);
        Assert.Null(loaded.WindowHeight);
        Assert.Null(loaded.WindowLeft);
        Assert.Null(loaded.WindowTop);
        Assert.Null(loaded.WindowState);
    }

    [Fact]
    public void ConnectionBuildSettingsPreservesWindowPlacement()
    {
        var connection = new ConnectionViewModel();
        connection.Load(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            WindowLeft = 15,
            WindowTop = 25,
            WindowWidth = 1200,
            WindowHeight = 760,
            WindowState = MainWindowPlacement.StateNormal
        });

        var built = connection.BuildSettings();

        Assert.Equal(15, built.WindowLeft);
        Assert.Equal(25, built.WindowTop);
        Assert.Equal(1200, built.WindowWidth);
        Assert.Equal(760, built.WindowHeight);
        Assert.Equal(MainWindowPlacement.StateNormal, built.WindowState);
    }

    [Fact]
    public void MainViewModelPersistsPlacementWithoutWipingOtherSettings()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            InstanceUrl = "https://keep.service-now.com",
            Username = "alex",
            DownloadCacheOnLaunch = false,
            WindowWidth = 1100,
            WindowHeight = 700
        });
        var main = new MainViewModel(store, new RecordingDesktopServices());

        main.RememberWindowPlacement(200, 100, 1400, 900, MainWindowPlacement.StateMaximized);

        var saved = store.Load();
        Assert.Equal("https://keep.service-now.com", saved.InstanceUrl);
        Assert.Equal("alex", saved.Username);
        Assert.False(saved.DownloadCacheOnLaunch);
        Assert.Equal(200, saved.WindowLeft);
        Assert.Equal(100, saved.WindowTop);
        Assert.Equal(1400, saved.WindowWidth);
        Assert.Equal(900, saved.WindowHeight);
        Assert.Equal(MainWindowPlacement.StateMaximized, saved.WindowState);

        var resolved = main.ResolveWindowPlacement(WorkArea);
        Assert.Equal(200, resolved.Left);
        Assert.Equal(100, resolved.Top);
        Assert.Equal(1400, resolved.Width);
        Assert.Equal(900, resolved.Height);
        Assert.True(resolved.Maximized);
    }
}
