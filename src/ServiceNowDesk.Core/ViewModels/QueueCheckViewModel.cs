using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.QueueDay;

namespace ServiceNowDesk.ViewModels;

public partial class QueueCheckViewModel : ObservableObject
{
    public ObservableCollection<QueueCheckPersonRow> People { get; } = [];

    public event EventHandler? CheckRequested;

    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string emptyMessage = "";
    [ObservableProperty] private string baselineText = "";
    [ObservableProperty] private string checkedText = "";

    public bool HasPeople => People.Count > 0;

    [RelayCommand]
    private void CheckAgain() => CheckRequested?.Invoke(this, EventArgs.Empty);

    public void MarkRunning()
    {
        IsRunning = true;
        Status = QueueDayText.Checking;
        EmptyMessage = "";
    }

    public void Show(QueueDayReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        IsRunning = false;
        Status = report.Status ?? "";
        EmptyMessage = report.EmptyMessage ?? "";
        BaselineText = report.BaselineAt is DateTime baseline ? QueueDayText.BaselineAt(baseline) : "";
        CheckedText = report.CheckedAt is DateTime checkedAt ? QueueDayText.CheckedAt(checkedAt) : "";
        Replace(report.People);
    }

    public void ShowDefineTeam() => ShowMessage(QueueDayText.DefineTeamMessage);

    public void ShowMessage(string message)
    {
        IsRunning = false;
        Status = "";
        EmptyMessage = message ?? "";
        BaselineText = "";
        CheckedText = "";
        Replace([]);
    }

    public void ShowError(string message)
    {
        IsRunning = false;
        Status = message ?? "";
    }

    public void Clear()
    {
        IsRunning = false;
        Status = "";
        EmptyMessage = "";
        BaselineText = "";
        CheckedText = "";
        Replace([]);
    }

    private void Replace(IReadOnlyList<QueueDayPersonResult> people)
    {
        People.Clear();
        foreach (var person in people ?? [])
            People.Add(QueueCheckPersonRow.From(person));
        OnPropertyChanged(nameof(HasPeople));
    }
}

public sealed class QueueCheckPersonRow
{
    private QueueCheckPersonRow(string name, IReadOnlyList<QueueCheckGroupRow> groups, string emptyNote)
    {
        Name = name;
        Groups = groups;
        EmptyNote = emptyNote;
    }

    public string Name { get; }

    public IReadOnlyList<QueueCheckGroupRow> Groups { get; }

    public string EmptyNote { get; }

    public static QueueCheckPersonRow From(QueueDayPersonResult person)
    {
        var lines = person.Lines ?? [];
        var groups = new[]
        {
            Group("Changed", lines, QueueDayKind.Changed),
            Group("New", lines, QueueDayKind.New),
            Group("Missing", lines, QueueDayKind.Missing),
            Group("Unchanged", lines, QueueDayKind.Unchanged)
        }.Where(group => group is not null).Cast<QueueCheckGroupRow>().ToArray();
        var empty = lines.Count == 0 ? QueueDayText.NoOpenTickets : "";
        var name = string.IsNullOrWhiteSpace(person.PersonName) ? person.PersonId : person.PersonName.Trim();
        return new QueueCheckPersonRow(name, groups, empty);
    }

    private static QueueCheckGroupRow? Group(string title, IReadOnlyList<QueueDayLine> lines, QueueDayKind kind)
    {
        var rows = lines
            .Where(line => line.Kind == kind)
            .Select(line => new QueueCheckLineRow(line.Number, line.ShortDescription, line.Breakdown))
            .ToArray();
        return rows.Length == 0 ? null : new QueueCheckGroupRow(title, rows);
    }
}

public sealed class QueueCheckGroupRow
{
    public QueueCheckGroupRow(string title, IReadOnlyList<QueueCheckLineRow> lines)
    {
        Title = title;
        Lines = lines;
    }

    public string Title { get; }

    public IReadOnlyList<QueueCheckLineRow> Lines { get; }
}

public sealed class QueueCheckLineRow
{
    public QueueCheckLineRow(string number, string shortDescription, string breakdown)
    {
        Number = number ?? "";
        ShortDescription = shortDescription ?? "";
        Breakdown = breakdown ?? "";
    }

    public string Number { get; }

    public string ShortDescription { get; }

    public string Breakdown { get; }
}
