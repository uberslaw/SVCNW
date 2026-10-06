using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Alerts;

namespace ServiceNowDesk.ViewModels;

public sealed class LegendSettingsViewModel
{
    private bool _ready;
    private bool _loading;

    public LegendSettingsViewModel()
    {
        Entries = HighlightCatalog.Entries.Select(entry => new LegendEntryModel(this, entry)).ToArray();
        _ready = true;
    }

    public IReadOnlyList<LegendEntryModel> Entries { get; }

    public event EventHandler<HighlightPreferences>? Changed;

    public HighlightPreferences Current() =>
        HighlightPreferences.FromKeys(Entries.Where(entry => entry.IsEnabled).Select(entry => entry.Key).ToArray());

    public void Load(HighlightPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _loading = true;
        foreach (var entry in Entries)
            entry.IsEnabled = preferences.IsEnabled(entry.Key);
        _loading = false;
    }

    internal void EntryChanged()
    {
        if (!_ready || _loading)
            return;
        Changed?.Invoke(this, Current());
    }
}

public partial class LegendEntryModel : ObservableObject
{
    private readonly LegendSettingsViewModel _owner;

    public LegendEntryModel(LegendSettingsViewModel owner, HighlightEntry entry)
    {
        _owner = owner;
        Key = entry.Key;
        Title = entry.Title;
        Explanation = entry.Explanation;
        SwatchHex = entry.SwatchHex;
        RowHex = entry.RowHex;
        IsEnabled = entry.EnabledByDefault;
    }

    public string Key { get; }

    public string Title { get; }

    public string Explanation { get; }

    public string SwatchHex { get; }

    public string RowHex { get; }

    [ObservableProperty] private bool isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        _ = value;
        _owner.EntryChanged();
    }
}
