using System.Globalization;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

public sealed class NotificationPreferences
{
    public const string DefaultFrequency = "00:01:00";
    public const int DefaultDurationSeconds = 2;
    public const int DefaultPollSeconds = 60;
    public const int MinimumDurationSeconds = 1;
    public const int MinimumPollSeconds = 15;
    public const string DefaultGroupName = "Aus DT - Client Services";

    public static readonly string[] DefaultLocations =
    [
        "Brisbane",
        "Maroochydore",
        "Gold Coast",
        "Townsville",
        "Cairns"
    ];

    public string JiggleFrequency { get; set; } = DefaultFrequency;
    public int JiggleDurationSeconds { get; set; } = DefaultDurationSeconds;
    public bool MaximizeWhenJiggling { get; set; } = true;
    public bool PlaySoundWhenJiggling { get; set; }
    public bool PlaySoundOnAlertMetric { get; set; } = true;
    public string AlertSoundPath { get; set; } = "";
    public string WatchedGroupName { get; set; } = DefaultGroupName;
    public List<string> OfficeLocations { get; set; } = [.. DefaultLocations];
    public int PollSeconds { get; set; } = DefaultPollSeconds;

    public TimeSpan JiggleInterval =>
        TryParseFrequency(JiggleFrequency, out var frequency) ? frequency : TimeSpan.FromMinutes(1);

    public static bool TryParseFrequency(string? text, out TimeSpan frequency)
    {
        frequency = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (!TimeSpan.TryParseExact(trimmed, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out frequency))
            return false;

        return frequency > TimeSpan.Zero;
    }

    public static bool TryParseDuration(string? text, out int seconds) =>
        TryParseMinimum(text, MinimumDurationSeconds, out seconds);

    public static bool TryParsePollSeconds(string? text, out int seconds) =>
        TryParseMinimum(text, MinimumPollSeconds, out seconds);

    public static IReadOnlyList<string> ParseLocations(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static NotificationPreferences From(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var frequency = settings.JiggleFrequency;
        if (!TryParseFrequency(frequency, out var parsed))
            frequency = DefaultFrequency;
        else
            frequency = parsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        return new NotificationPreferences
        {
            JiggleFrequency = frequency,
            JiggleDurationSeconds = settings.JiggleDurationSeconds >= MinimumDurationSeconds
                ? settings.JiggleDurationSeconds
                : DefaultDurationSeconds,
            MaximizeWhenJiggling = settings.MaximizeWhenJiggling,
            PlaySoundWhenJiggling = settings.PlaySoundWhenJiggling,
            PlaySoundOnAlertMetric = settings.PlaySoundOnAlertMetric,
            AlertSoundPath = settings.AlertSoundPath ?? "",
            WatchedGroupName = settings.WatchedGroupName?.Trim() ?? DefaultGroupName,
            OfficeLocations = settings.OfficeLocations is null
                ? [.. DefaultLocations]
                : settings.OfficeLocations
                    .Select(city => city?.Trim() ?? "")
                    .Where(city => city.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            PollSeconds = settings.NotificationPollSeconds >= MinimumPollSeconds
                ? settings.NotificationPollSeconds
                : DefaultPollSeconds
        };
    }

    public void ApplyTo(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.JiggleFrequency = JiggleFrequency;
        settings.JiggleDurationSeconds = JiggleDurationSeconds;
        settings.MaximizeWhenJiggling = MaximizeWhenJiggling;
        settings.PlaySoundWhenJiggling = PlaySoundWhenJiggling;
        settings.PlaySoundOnAlertMetric = PlaySoundOnAlertMetric;
        settings.AlertSoundPath = AlertSoundPath ?? "";
        settings.WatchedGroupName = WatchedGroupName ?? "";
        settings.OfficeLocations = OfficeLocations is null ? [] : [.. OfficeLocations];
        settings.NotificationPollSeconds = PollSeconds;
    }

    public NotificationPreferences Copy() => new()
    {
        JiggleFrequency = JiggleFrequency,
        JiggleDurationSeconds = JiggleDurationSeconds,
        MaximizeWhenJiggling = MaximizeWhenJiggling,
        PlaySoundWhenJiggling = PlaySoundWhenJiggling,
        PlaySoundOnAlertMetric = PlaySoundOnAlertMetric,
        AlertSoundPath = AlertSoundPath,
        WatchedGroupName = WatchedGroupName,
        OfficeLocations = OfficeLocations is null ? [] : [.. OfficeLocations],
        PollSeconds = PollSeconds
    };

    public AlertSearch ToSearch(string userSysId) =>
        new(userSysId, WatchedGroupName, OfficeLocations ?? []);

    private static bool TryParseMinimum(string? text, int minimum, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            return false;
        return seconds >= minimum;
    }
}
