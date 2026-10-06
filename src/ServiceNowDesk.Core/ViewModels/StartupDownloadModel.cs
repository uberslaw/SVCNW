using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ServiceNowDesk.ViewModels;

public sealed partial class StartupDownloadModel : ObservableObject
{
    private int _total = 1;
    private int _finished;
    private int _current = -1;

    public ObservableCollection<StartupDownloadLine> Lines { get; } = [];

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string title = "Data ready";

    public void Begin(int total)
    {
        Lines.Clear();
        _total = Math.Max(1, total);
        _finished = 0;
        _current = -1;
        Title = Heading(null);
        IsRunning = true;
        IsOpen = true;
    }

    public void Start(string name)
    {
        var line = new StartupDownloadLine(name);
        line.ShowPercent(0);
        Lines.Add(line);
        _current = Lines.Count - 1;
        Title = Heading(name);
    }

    public void Report(int percent)
    {
        if (_current < 0 || _current >= Lines.Count)
            return;
        var line = Lines[_current];
        if (line.IsFinished)
            return;
        line.ShowPercent(Math.Clamp(percent, 0, 99));
    }

    public void Complete()
    {
        if (_current < 0 || _current >= Lines.Count)
            return;
        Lines[_current].ShowPercent(100);
        FinishCurrent();
    }

    public void CompleteCached()
    {
        if (_current < 0 || _current >= Lines.Count)
            return;
        var line = Lines[_current];
        line.ShowPercent(100);
        line.ShowNote("cached");
        FinishCurrent();
    }

    public void Fail(string error)
    {
        if (_current < 0 || _current >= Lines.Count)
            return;
        var text = string.IsNullOrWhiteSpace(error) ? "Could not download this section." : error.Trim();
        Lines[_current].ShowNote(text);
        FinishCurrent();
    }

    public void Toggle() => IsOpen = !IsOpen;

    public void Close() => IsOpen = false;

    private void FinishCurrent()
    {
        _finished++;
        _current = -1;
        if (_finished >= _total)
        {
            IsRunning = false;
            Title = "Data ready";
            return;
        }

        Title = Heading(null);
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
