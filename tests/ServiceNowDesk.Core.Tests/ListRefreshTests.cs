using System.Globalization;
using ServiceNowDesk.Client;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class ListRefreshTests
{
    [Fact]
    public async Task IncidentRefreshCommandReloadsAndBumpsAsOf()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        var first = new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero);
        var second = first.AddMinutes(12);
        workspace.ListClock = () => first;
        workspace.Attach(client);

        await workspace.RefreshCommand.ExecuteAsync(null);

        Assert.True(workspace.HasLoaded);
        Assert.Equal(first, workspace.ListRefreshedAt);
        Assert.Equal(
            "As of " + first.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            workspace.ListRefreshedText);
        var countAfterFirst = workspace.TotalCount;
        Assert.True(countAfterFirst > 0);

        workspace.ListClock = () => second;
        await workspace.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(second, workspace.ListRefreshedAt);
        Assert.Equal(
            "As of " + second.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            workspace.ListRefreshedText);
        Assert.Equal(countAfterFirst, workspace.TotalCount);
    }

    [Fact]
    public void ShowCachedRowsUsesCapturedAtForAsOf()
    {
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        var captured = new DateTimeOffset(2026, 10, 8, 15, 30, 0, TimeSpan.Zero);
        workspace.ListClock = () => captured.AddHours(3);

        workspace.ShowCachedRows([], 0, captured);

        Assert.Equal(captured, workspace.ListRefreshedAt);
        Assert.Equal(
            "As of " + captured.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            workspace.ListRefreshedText);
    }

    [Fact]
    public async Task MixRefreshCommandForcesLiveAndBumpsAsOf()
    {
        using var client = new SampleServiceNowClient();
        var mix = new MixWorkspaceViewModel();
        var stamped = new DateTimeOffset(2026, 10, 9, 9, 45, 0, TimeSpan.Zero);
        mix.ListClock = () => stamped;
        mix.Attach(client);
        mix.TryLoadFromHostCacheAsync = () => throw new InvalidOperationException("Refresh must not use host cache.");

        await mix.RefreshCommand.ExecuteAsync(null);

        Assert.True(mix.HasLoaded);
        Assert.Equal(stamped, mix.ListRefreshedAt);
        Assert.Equal(
            "As of " + stamped.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            mix.ListRefreshedText);
    }
}
