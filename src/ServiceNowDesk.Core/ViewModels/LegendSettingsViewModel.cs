using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;

namespace ServiceNowDesk.ViewModels;

public sealed partial class LegendSettingsViewModel : ObservableObject
{
    private readonly Dictionary<string, int> _intensities = new(StringComparer.OrdinalIgnoreCase);
    private bool _ready;
    private bool _loading;
    private bool _suspendSlider;
    private int? _sharedIntensity;

    public LegendSettingsViewModel()
    {
        _suspendSlider = true;
        Entries = HighlightCatalog.Entries.Select(entry => new LegendEntryModel(this, entry)).ToArray();
        foreach (var entry in Entries)
        {
            var measured = LegendColorIntensity.Measure(entry.SwatchHex);
            _intensities[entry.Key] = measured;
            entry.Paint(measured);
        }

        _ready = true;
        SliderValue = Average();
        _suspendSlider = false;
        RefreshShown();
    }

    public IReadOnlyList<LegendEntryModel> Entries { get; }

    public ObservableCollection<LegendEntryModel> Shown { get; } = [];

    [ObservableProperty] private bool anyShown;

    [ObservableProperty] private string emptyNote = "";

    [ObservableProperty] private bool adjustIndividually;

    [ObservableProperty] private int sliderValue;

    [ObservableProperty] private string? selectedKey;

    [ObservableProperty] private string intensityCaption = "All colours";

    public event EventHandler<HighlightPreferences>? Changed;

    public HighlightPreferences Current()
    {
        var preferences = HighlightPreferences.FromKeys(
            Entries.Where(entry => entry.IsEnabled).Select(entry => entry.Key).ToArray());
        return preferences.WithIntensity(_sharedIntensity, Overrides());
    }

    private int Average() => LegendColorIntensity.Average(Entries.Select(entry => _intensities[entry.Key]));

    public void Load(HighlightPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _loading = true;
        _suspendSlider = true;
        foreach (var entry in Entries)
            entry.IsEnabled = preferences.IsEnabled(entry.Key);
        _sharedIntensity = preferences.SharedIntensity;
        foreach (var entry in Entries)
        {
            _intensities[entry.Key] = preferences.IntensityOf(entry.Key);
            entry.Paint(_intensities[entry.Key]);
        }

        AdjustIndividually = false;
        SelectedKey = null;
        SliderValue = Average();
        _suspendSlider = false;
        _loading = false;
        RefreshShown();
        RefreshSelection();
        RefreshCaption();
        SetIntensityCommand.NotifyCanExecuteChanged();
    }

    public void Select(string? key)
    {
        if (!AdjustIndividually || key is null || !_intensities.ContainsKey(key))
            return;

        _suspendSlider = true;
        SelectedKey = key;
        SliderValue = _intensities[key];
        _suspendSlider = false;
    }

    internal void EntryChanged()
    {
        if (!_ready || _loading)
            return;
        RefreshShown();
        Publish();
    }

    partial void OnAdjustIndividuallyChanged(bool value)
    {
        if (!_loading)
        {
            _suspendSlider = true;
            if (!value)
            {
                SelectedKey = null;
                SliderValue = Average();
            }

            _suspendSlider = false;
        }

        RefreshSelection();
        RefreshCaption();
        SetIntensityCommand.NotifyCanExecuteChanged();
    }

    partial void OnSliderValueChanged(int value)
    {
        if (_loading || _suspendSlider)
            return;

        var clamped = LegendColorIntensity.Clamp(value);
        if (clamped != value)
        {
            _suspendSlider = true;
            SliderValue = clamped;
            _suspendSlider = false;
            value = clamped;
        }

        if (AdjustIndividually)
            return;

        ApplyAll(value);
    }

    partial void OnSelectedKeyChanged(string? value)
    {
        _ = value;
        RefreshSelection();
        RefreshCaption();
        SetIntensityCommand.NotifyCanExecuteChanged();
    }

    private bool CanSetIntensity() =>
        AdjustIndividually && SelectedKey is not null && _intensities.ContainsKey(SelectedKey);

    [RelayCommand(CanExecute = nameof(CanSetIntensity))]
    private void SetIntensity()
    {
        if (SelectedKey is null || !_intensities.ContainsKey(SelectedKey))
            return;

        _intensities[SelectedKey] = LegendColorIntensity.Clamp(SliderValue);
        PaintAll();
        Publish();
    }

    private void ApplyAll(int value)
    {
        _sharedIntensity = LegendColorIntensity.Clamp(value);
        foreach (var entry in Entries)
            _intensities[entry.Key] = _sharedIntensity.Value;
        PaintAll();
        Publish();
    }

    private void PaintAll()
    {
        foreach (var entry in Entries)
            entry.Paint(_intensities[entry.Key]);
    }

    private Dictionary<string, int>? Overrides()
    {
        Dictionary<string, int>? overrides = null;
        foreach (var entry in Entries)
        {
            var value = _intensities[entry.Key];
            var original = HighlightCatalog.Find(entry.Key)!.SwatchHex;
            var baseline = _sharedIntensity ?? LegendColorIntensity.Measure(original);
            if (value == baseline)
                continue;
            overrides ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            overrides[entry.Key] = value;
        }

        return overrides;
    }

    private void Publish()
    {
        if (!_ready || _loading)
            return;
        Changed?.Invoke(this, Current());
    }

    private void RefreshShown()
    {
        Shown.Clear();
        foreach (var entry in Entries)
        {
            if (entry.IsEnabled)
                Shown.Add(entry);
        }

        AnyShown = Shown.Count > 0;
        EmptyNote = AnyShown
            ? ""
            : "Row colors are off. Open Legend in the left navigation to turn them on.";
    }

    private void RefreshSelection()
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = AdjustIndividually
                && string.Equals(entry.Key, SelectedKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void RefreshCaption()
    {
        if (!AdjustIndividually)
        {
            IntensityCaption = "All colours";
            return;
        }

        var selected = Entries.FirstOrDefault(entry =>
            string.Equals(entry.Key, SelectedKey, StringComparison.OrdinalIgnoreCase));
        IntensityCaption = selected is null ? "Click a colour swatch" : selected.Title;
    }
}

public partial class LegendEntryModel : ObservableObject
{
    private readonly LegendSettingsViewModel _owner;
    private readonly string _baseSwatch;
    private readonly string _baseRow;

    public LegendEntryModel(LegendSettingsViewModel owner, HighlightEntry entry)
    {
        _owner = owner;
        Key = entry.Key;
        Title = entry.Title;
        Explanation = entry.Explanation;
        _baseSwatch = entry.SwatchHex;
        _baseRow = entry.RowHex;
        SwatchHex = entry.SwatchHex;
        RowHex = entry.RowHex;
        IsEnabled = entry.EnabledByDefault;
    }

    public string Key { get; }

    public string Title { get; }

    public string Explanation { get; }

    [ObservableProperty] private string swatchHex;

    [ObservableProperty] private string rowHex;

    [ObservableProperty] private bool isSelected;

    [ObservableProperty] private bool isEnabled;

    internal void Paint(int intensity)
    {
        SwatchHex = LegendColorIntensity.Swatch(_baseSwatch, intensity);
        RowHex = LegendColorIntensity.Row(_baseSwatch, _baseRow, intensity);
    }

    [RelayCommand]
    private void Select() => _owner.Select(Key);

    partial void OnIsEnabledChanged(bool value)
    {
        _ = value;
        _owner.EntryChanged();
    }
}
