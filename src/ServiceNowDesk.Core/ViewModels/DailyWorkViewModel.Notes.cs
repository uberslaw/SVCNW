using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class DailyWorkViewModel
{
    private readonly IPersonalTaskStore _personalTasks;

    public ObservableCollection<PersonalTaskRow> Notes { get; } = [];

    public event EventHandler<PersonalTaskRow>? IncidentRequested;

    public event EventHandler<PersonalTaskRow>? RequestedItemRequested;

    [ObservableProperty] private string noteText = "";
    [ObservableProperty] private TrafficLight noteLight = TrafficLight.Amber;
    [ObservableProperty] private bool hasNotes;

    public bool TryAddNote()
    {
        var text = NoteText?.Trim() ?? "";
        if (text.Length == 0)
            return false;

        var task = PersonalTask.Create(text, NoteLight);
        Persist(Notes.Select(note => note.ToTask()).Append(task));
        Notes.Add(PersonalTaskRow.From(task));
        HasNotes = true;
        NoteText = "";
        return true;
    }

    public void ChangeNoteColor(Guid id, TrafficLight light)
    {
        var row = Notes.FirstOrDefault(note => note.Id == id);
        if (row is null)
            return;

        var updated = row.ToTask() with { Light = PersonalTask.Normalize(light) };
        Persist(Notes.Select(note => note.Id == id ? updated : note.ToTask()));
        row.Apply(updated);
    }

    public bool RemovePersonalNote(Guid id)
    {
        var row = Notes.FirstOrDefault(note => note.Id == id);
        if (row is null)
            return false;

        Persist(Notes.Where(note => note.Id != id).Select(note => note.ToTask()));
        Notes.Remove(row);
        HasNotes = Notes.Count > 0;
        return true;
    }

    [RelayCommand]
    private void AddNote() => TryAddNote();

    [RelayCommand]
    private void RemoveNote(PersonalTaskRow? row)
    {
        if (row is not null)
            RemovePersonalNote(row.Id);
    }

    [RelayCommand]
    private void CreateIncident(PersonalTaskRow? row)
    {
        if (row is null)
            return;
        IncidentRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void CreateRequestedItem(PersonalTaskRow? row)
    {
        if (row is null)
            return;
        RequestedItemRequested?.Invoke(this, row);
    }

    private void LoadNotes()
    {
        Notes.Clear();
        foreach (var task in _personalTasks.Load())
            Notes.Add(PersonalTaskRow.From(task));
        HasNotes = Notes.Count > 0;
    }

    private void Persist(IEnumerable<PersonalTask> tasks) => _personalTasks.Replace(tasks.ToArray());
}

public partial class PersonalTaskRow : ObservableObject
{
    public PersonalTaskRow(PersonalTask task)
    {
        Id = task.Id;
        Apply(task);
    }

    public Guid Id { get; }

    [ObservableProperty] private string text = "";
    [ObservableProperty] private TrafficLight light = TrafficLight.Amber;
    [ObservableProperty] private string lightHex = PersonalTask.Hex(TrafficLight.Amber);
    [ObservableProperty] private DateTime createdUtc;

    public void Apply(PersonalTask task)
    {
        Text = task.Text;
        Light = task.Light;
        LightHex = PersonalTask.Hex(task.Light);
        CreatedUtc = task.CreatedUtc;
    }

    public PersonalTask ToTask() => new(Id, Text, Light, CreatedUtc);

    public static PersonalTaskRow From(PersonalTask task) => new(task);
}
