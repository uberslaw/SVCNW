using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class JiggleSpeedTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1.5, 1.5)]
    [InlineData(4.5, 4.5)]
    [InlineData(10, 10)]
    [InlineData(1.2, 1)]
    [InlineData(1.3, 1.5)]
    [InlineData(1.25, 1.5)]
    [InlineData(2.25, 2.5)]
    [InlineData(2.75, 3)]
    [InlineData(7.2, 7)]
    [InlineData(7.25, 7.5)]
    [InlineData(0, 1)]
    [InlineData(0.4, 1)]
    [InlineData(-3, 1)]
    [InlineData(10.2, 10)]
    [InlineData(10.25, 10)]
    [InlineData(99, 10)]
    public void SpeedSnapsToHalfStepsBetweenOneAndTen(double input, double expected)
    {
        Assert.Equal(expected, JiggleMotion.Snap(input));
    }

    [Fact]
    public void EveryLegalHalfStepIsUnchanged()
    {
        for (var step = 0; step <= 18; step++)
        {
            var speed = 1 + step * 0.5;
            Assert.InRange(speed, 1, 10);
            Assert.Equal(speed, JiggleMotion.Snap(speed));
            Assert.Equal(TimeSpan.FromSeconds(1d / speed), JiggleMotion.MoveInterval(speed));
        }
    }

    [Fact]
    public void NonNumbersFallBackToTheDefaultRate()
    {
        Assert.Equal(JiggleMotion.DefaultMovesPerSecond, JiggleMotion.Snap(double.NaN));
        Assert.Equal(JiggleMotion.DefaultMovesPerSecond, JiggleMotion.Snap(double.PositiveInfinity));
        Assert.Equal(JiggleMotion.DefaultMovesPerSecond, JiggleMotion.Snap(double.NegativeInfinity));
    }

    [Fact]
    public void DefaultMatchesTheExistingSixCycleWiggle()
    {
        Assert.Equal(3d, JiggleMotion.DefaultMovesPerSecond);
        Assert.Equal(3d, new DeskSettings().JiggleSpeed);

        var preferences = NotificationPreferences.From(new DeskSettings());
        Assert.Equal(3d, preferences.JiggleSpeed);

        var settings = new NotificationSettingsViewModel();
        Assert.Equal(3d, settings.JiggleSpeed);
        Assert.Equal(3d, settings.ActiveJiggleSpeed);
        Assert.Equal(3d, settings.Committed.JiggleSpeed);

        var duration = TimeSpan.FromSeconds(NotificationPreferences.DefaultDurationSeconds);
        var plan = JiggleMotion.Plan(JiggleMotion.DefaultMovesPerSecond, duration);
        Assert.Equal(3d, plan.MovesPerSecond);
        Assert.Equal(6d, plan.SineCycles);
        Assert.Equal(48, JiggleMotion.KeyframeCount(plan.SineCycles));
        Assert.Equal(TimeSpan.FromSeconds(1d / 3d), plan.MoveInterval);
        Assert.Equal(0, JiggleMotion.Offset(3, duration, 0), precision: 6);
        Assert.Equal(6, JiggleMotion.Offset(3, duration, 1d / 24d), precision: 5);
    }

    [Fact]
    public void MoveIntervalIsOneOverSpeed()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), JiggleMotion.MoveInterval(2));
        Assert.Equal(TimeSpan.FromSeconds(1), JiggleMotion.MoveInterval(1));
        Assert.Equal(TimeSpan.FromMilliseconds(100), JiggleMotion.MoveInterval(10));
        Assert.Equal(TimeSpan.FromSeconds(1d / 4.5), JiggleMotion.MoveInterval(4.5));

        var snapped = JiggleMotion.Plan(2.2, TimeSpan.FromSeconds(2));
        Assert.Equal(2d, snapped.MovesPerSecond);
        Assert.Equal(TimeSpan.FromMilliseconds(500), snapped.MoveInterval);
        Assert.Equal(4d, snapped.SineCycles);

        var half = JiggleMotion.Plan(4.5, TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(1d / 4.5), half.MoveInterval);
        Assert.Equal(9d, half.SineCycles);
        Assert.Equal(0, JiggleMotion.Offset(2, TimeSpan.FromSeconds(2), 0.25), precision: 6);
    }

    [Fact]
    public void SpeedRoundTripsThroughTheSettingsFile()
    {
        var missing = DeskSettingsFile.Deserialize("{}", text => text ?? "");
        Assert.Equal(3d, missing.JiggleSpeed);
        Assert.Equal("00:01:00", missing.JiggleFrequency);

        var illegal = DeskSettingsFile.Deserialize("{\"JiggleSpeed\":7.2}", text => text ?? "");
        Assert.Equal(7d, illegal.JiggleSpeed);

        var settings = new DeskSettings
        {
            Password = "secret",
            JiggleFrequency = "00:00:10",
            JiggleDurationSeconds = 4,
            JiggleSpeed = 4.5
        };
        var json = DeskSettingsFile.Serialize(settings, text => text ?? "");
        Assert.Contains("\"JiggleSpeed\": 4.5", json, StringComparison.Ordinal);
        var loaded = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Equal(4.5, loaded.JiggleSpeed);
        Assert.Equal("secret", loaded.Password);
        Assert.Equal("00:00:10", loaded.JiggleFrequency);
        Assert.Equal(4, loaded.JiggleDurationSeconds);
        Assert.Equal(4.5, NotificationPreferences.From(loaded).JiggleSpeed);

        var view = new NotificationSettingsViewModel();
        view.Load(loaded);
        Assert.Equal(4.5, view.JiggleSpeed);
        Assert.Equal(4.5, view.ActiveJiggleSpeed);
        view.JiggleSpeed = 8.2;
        Assert.Equal(8d, view.JiggleSpeed);
        Assert.Equal(8d, view.ActiveJiggleSpeed);
        Assert.Equal(TimeSpan.FromMilliseconds(125), JiggleMotion.MoveInterval(view.ActiveJiggleSpeed));
        view.SaveSettingsCommand.Execute(null);

        var desk = new DeskSettings { Password = "secret" };
        view.Committed.ApplyTo(desk);
        var again = DeskSettingsFile.Deserialize(DeskSettingsFile.Serialize(desk, text => text ?? ""), text => text ?? "");
        Assert.Equal(8d, again.JiggleSpeed);
        Assert.Equal("secret", again.Password);
        Assert.Equal("00:00:10", again.JiggleFrequency);
    }

    [Fact]
    public void SliderWritesTheSettingsFileWithoutChangingWhenAJiggleStarts()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            Password = "secret",
            JiggleFrequency = "00:05:00",
            JiggleDurationSeconds = 4,
            JiggleWhen = JiggleWhen.NewUntilAcknowledged
        });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.NotificationSettings.Load(main.Connection.Notifications);
        Assert.Equal(3d, main.NotificationSettings.ActiveJiggleSpeed);

        main.NotificationSettings.JiggleSpeed = 2;
        Assert.Equal(2d, main.NotificationSettings.ActiveJiggleSpeed);
        Assert.Equal(2d, store.Current.JiggleSpeed);
        Assert.Equal("secret", store.Current.Password);
        Assert.Equal("00:05:00", store.Current.JiggleFrequency);
        Assert.Equal(4, store.Current.JiggleDurationSeconds);
        Assert.Equal(JiggleWhen.NewUntilAcknowledged, store.Current.JiggleWhen);
        Assert.Equal(TimeSpan.FromMilliseconds(500), JiggleMotion.MoveInterval(store.Current.JiggleSpeed));

        var schedule = new AlertJiggleSchedule();
        Assert.True(schedule.Arm(TimeSpan.FromMinutes(5)));
        Assert.False(schedule.Arm(TimeSpan.FromMinutes(5)));
        Assert.Equal(1, schedule.ArmCount);

        var loaded = DeskSettingsFile.Deserialize(
            DeskSettingsFile.Serialize(store.Current, text => text ?? ""),
            text => text ?? "");
        Assert.Equal(2d, loaded.JiggleSpeed);
        Assert.Equal("00:05:00", loaded.JiggleFrequency);
    }
}
