using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ServiceNowDesk.ViewModels;

public sealed partial class StartupDownloadModel : ObservableObject
{
    private readonly object _gate = new();
    private int _total = 1;
    private int _finished;
    private int _current = -1;
    private bool _dismissed;

    public ObservableCollection<StartupDownloadLine> Lines { get; } = [];

    public event EventHandler? Dismissed;

    [ObservableProperty] private bool showScreen;
    [ObservableProperty] private bool showBar;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string countText = "";
    [ObservableProperty] private string percentText = "";
    [ObservableProperty] private int percent;

    public void Begin(int total)
    {
        lock (_gate)
        {
            Lines.Clear();
            _total = Math.Max(1, total);
            _finished = 0;
            _current = -1;
            _dismissed = false;
            Title = Heading(null);
            IsRunning = true;
            ShowScreen = true;
            ShowBar = false;
            UpdateSummary();
        }
    }

    public void Start(string name)
    {
        lock (_gate)
        {
            var line = new StartupDownloadLine(name);
            line.ShowPercent(0);
            Lines.Add(line);
            _current = Lines.Count - 1;
            Title = Heading(name);
        }
    }

    public void Report(int percent)
    {
        lock (_gate)
        {
            if (_current < 0 || _current >= Lines.Count)
                return;
            var line = Lines[_current];
            if (line.IsFinished)
                return;
            line.ShowPercent(Math.Clamp(percent, 0, 99));
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_current < 0 || _current >= Lines.Count)
                return;
            Lines[_current].ShowPercent(100);
            FinishCurrent();
        }
    }

    public void CompleteCached()
    {
        lock (_gate)
        {
            if (_current < 0 || _current >= Lines.Count)
                return;
            var line = Lines[_current];
            line.ShowPercent(100);
            line.ShowNote("cached");
            FinishCurrent();
        }
    }

    public void Fail(string error)
    {
        lock (_gate)
        {
            if (_current < 0 || _current >= Lines.Count)
                return;
            var text = string.IsNullOrWhiteSpace(error) ? "Could not download this section." : error.Trim();
            Lines[_current].ShowNote(text);
            FinishCurrent();
        }
    }

    public void Dismiss()
    {
        lock (_gate)
        {
            _dismissed = true;
            ShowScreen = false;
            ShowBar = IsRunning;
        }

        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    public void Close() => Dismiss();

    public void Reset()
    {
        lock (_gate)
        {
            Lines.Clear();
            _finished = 0;
            _current = -1;
            _dismissed = false;
            IsRunning = false;
            ShowScreen = false;
            ShowBar = false;
            Title = "";
            CountText = "";
            PercentText = "";
            Percent = 0;
        }
    }

    private void FinishCurrent()
    {
        _finished++;
        _current = -1;
        UpdateSummary();
        if (_finished >= _total)
        {
            IsRunning = false;
            ShowScreen = false;
            ShowBar = false;
            return;
        }

        Title = Heading(null);
        ShowScreen = !_dismissed;
        ShowBar = _dismissed;
    }

    private void UpdateSummary()
    {
        CountText = _finished.ToString(CultureInfo.InvariantCulture) + "/" + _total.ToString(CultureInfo.InvariantCulture);
        Percent = _total <= 0 ? 0 : _finished * 100 / _total;
        PercentText = Percent.ToString(CultureInfo.InvariantCulture) + "%";
    }

    private string Heading(string? current)
    {
        var count = _finished.ToString(CultureInfo.InvariantCulture) + "/" + _total.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(current)
            ? "Downloading data " + count
            : "Downloading data " + count + " — " + current.Trim();
    }
}

public sealed partial class StartupDownloadLine : ObservableObject
{
    public StartupDownloadLine(string name)
    {
        Name = name;
        Text = name;
    }

    public string Name { get; }

    [ObservableProperty] private int percent;
    [ObservableProperty] private string text;
    [ObservableProperty] private bool isFinished;

    public void ShowPercent(int value)
    {
        Percent = Math.Clamp(value, 0, 100);
        Text = Name + "    " + Percent.ToString(CultureInfo.InvariantCulture) + "%";
        if (Percent >= 100)
            IsFinished = true;
    }

    public void ShowNote(string note)
    {
        Text = Name + "    " + note;
        IsFinished = true;
    }
}
