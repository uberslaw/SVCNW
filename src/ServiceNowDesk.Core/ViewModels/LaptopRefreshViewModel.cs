using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class LaptopRefreshViewModel : ObservableObject
{
    public const string ImportFilter = "Excel or CSV (*.xlsx;*.csv)|*.xlsx;*.csv|Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv";
    public const string ImportTitle = "Import laptops due this financial year";
    public const string ExportFileName = "laptop-refresh.csv";
    public static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly ILaptopRefreshStore _store;
    private readonly Func<string> _scope;
    private LaptopRefreshSnapshot _snapshot = new();
    private LaptopRefreshPlan _plan = new();
    private List<HardwareAsset> _catalog = [];
    private List<string> _offices = [];
    private List<Choice> _statuses = [];
    private string _loadedScope = "";
    private bool _loaded;
    private bool _complete;
    private bool _rebuilding;
    private IDesktopServices? _desktop;

    public LaptopRefreshViewModel(ILaptopRefreshStore store, Func<string> scope)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _scope = scope ?? (() => "");
    }

    public ObservableCollection<InBoxLaptop> InBox { get; } = [];
    public ObservableCollection<NextLaptopRow> Next { get; } = [];
    public ObservableCollection<ReceivedLaptopRow> Received { get; } = [];

    [ObservableProperty] private string summary = "Import the spreadsheet of laptops due this financial year. The oldest delivery date is who should be next.";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string inBoxHeading = "In the box";
    [ObservableProperty] private string nextHeading = "Who should be next";
    [ObservableProperty] private string receivedHeading = "Who actually received a laptop";
    [ObservableProperty] private bool hasDueList;
    [ObservableProperty] private bool hasInBox;
    [ObservableProperty] private bool hasNext;
    [ObservableProperty] private bool hasReceived;
    [ObservableProperty] private bool showEmptyInBox;
    [ObservableProperty] private bool showEmptyNext;
    [ObservableProperty] private bool showEmptyReceived;

    public void UseDesktop(IDesktopServices desktop) => _desktop = desktop;

    /// <summary>
    /// Compares the saved hardware list with the last stock-room watch and rebuilds the three lists.
    /// </summary>
    public void NoteCatalog(
        IReadOnlyList<HardwareAsset> catalog,
        bool catalogIsComplete,
        IReadOnlyList<string> offices,
        IEnumerable<Choice>? statuses)
    {
        EnsureLoaded();
        _catalog = catalog?.ToList() ?? [];
        _complete = catalogIsComplete;
        _offices = offices?.ToList() ?? [];
        _statuses = statuses?.ToList() ?? [];
        if (LaptopRefreshLogic.Observe(_snapshot, _catalog, _complete, _statuses, DateTimeOffset.Now))
            Save();
        Rebuild();
    }

    public void UseOffices(IReadOnlyList<string> offices, IEnumerable<Choice>? statuses)
    {
        EnsureLoaded();
        _offices = offices?.ToList() ?? [];
        if (statuses is not null)
            _statuses = statuses.ToList();
        Rebuild();
    }

    [RelayCommand]
    private void ImportDueList()
    {
        if (_desktop is null)
        {
            Status = "Import is not available in this window.";
            return;
        }

        var path = _desktop.PickOpenFile(ImportFilter, ImportTitle);
        if (string.IsNullOrWhiteSpace(path))
        {
            Status = "Import cancelled.";
            return;
        }

        ImportFile(path);
    }

    public void ImportFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnsureLoaded();
        LaptopAgeImport import;
        try
        {
            using var stream = File.OpenRead(path);
            import = LaptopAgeSheet.Read(stream, path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            Status = ex.Message;
            return;
        }

        if (import.Devices.Count == 0)
        {
            Status = "That file has no laptop rows.";
            return;
        }

        var source = Path.GetFileName(path);
        if (import.IsStockList)
        {
            LaptopRefreshLogic.ReplaceAvailable(_snapshot, import, source);
            var inStock = import.Devices.Count(device => device.State.Contains("stock", StringComparison.OrdinalIgnoreCase));
            var inTransit = import.Devices.Count(device => device.State.Contains("transit", StringComparison.OrdinalIgnoreCase));
            Status = "Imported "
                + _snapshot.Available.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " available laptops from "
                + source
                + ". "
                + inStock.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " in stock, "
                + inTransit.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " in transit.";
        }
        else if (import.HasSerial)
        {
            LaptopRefreshLogic.ReplaceDueList(_snapshot, import, source, DateTimeOffset.Now);
            var oldest = _snapshot.Due
                .Where(device => device.DeliveryDate is not null)
                .Select(device => device.DeliveryDate!.Value)
                .Order()
                .Cast<DateOnly?>()
                .FirstOrDefault();
            Status = "Imported "
                + _snapshot.Due.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " laptops from "
                + source
                + (oldest is null ? "." : ". Oldest delivery " + oldest.Value.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) + ".");
        }
        else
        {
            var applied = LaptopRefreshLogic.ApplyNotes(_snapshot, import);
            if (applied.Updated == 0)
            {
                Status = "None of those rows matched the due list. Import the devices spreadsheet first.";
                return;
            }

            Status = "Updated notes for "
                + applied.Updated.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " laptops from "
                + source
                + ".";
            if (applied.Unmatched.Count > 0)
            {
                Status += " Not on the due list: " + string.Join(", ", applied.Unmatched.Take(5));
                if (applied.Unmatched.Count > 5)
                    Status += "…";
                Status += ".";
            }
        }

        LaptopRefreshLogic.Observe(_snapshot, _catalog, _complete, _statuses, DateTimeOffset.Now);
        Save();
        Rebuild();
    }

    [RelayCommand]
    private void ExportSheet()
    {
        if (_desktop is null)
        {
            Status = "Export is not available in this window.";
            return;
        }

        EnsureLoaded();
        Rebuild();
        var path = _desktop.SaveTextFile(
            ExportFileName,
            "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            LaptopRefreshLogic.Format(_plan),
            Utf8Bom,
            "Export laptop refresh");
        Status = path is null ? "Export cancelled." : "Saved " + path;
    }

    private void SaveNext(NextLaptopRow row)
    {
        if (_rebuilding)
            return;

        var note = NoteFor(row.SerialNumber);
        var wasHold = LaptopRefreshLogic.IsOnHold(note);
        var wasCompleted = LaptopRefreshLogic.IsCompleted(note);
        note.StillHasLaptop = row.StillHasLaptop;
        note.OnHold = row.OnHold;
        note.Status = row.Status ?? "";
        note.Comment = row.Comment ?? "";
        Save();
        if (LaptopRefreshLogic.IsOnHold(note) != wasHold || LaptopRefreshLogic.IsCompleted(note) != wasCompleted)
            Rebuild();
    }

    private void SaveReason(ReceivedLaptopRow row)
    {
        if (_rebuilding)
            return;

        var item = _snapshot.Received.FirstOrDefault(entry =>
            entry.SerialNumber.Equals(row.SerialNumber, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.Reason == (row.Reason ?? ""))
            return;
        item.Reason = row.Reason ?? "";
        Save();
    }

    private void Rebuild()
    {
        if (_rebuilding)
            return;

        _rebuilding = true;
        try
        {
            _plan = LaptopRefreshLogic.Build(_snapshot, _catalog, _offices, _statuses, DateOnly.FromDateTime(DateTime.Today));
            foreach (var row in Next)
                row.Detach();
            foreach (var row in Received)
                row.Detach();
            InBox.Clear();
            Next.Clear();
            Received.Clear();
            foreach (var row in _plan.InBox)
                InBox.Add(row);
            foreach (var row in _plan.Next)
                Next.Add(new NextLaptopRow(row, SaveNext));
            foreach (var row in _plan.Received)
                Received.Add(new ReceivedLaptopRow(row, SaveReason));

            HasDueList = _snapshot.Due.Count > 0;
            HasInBox = InBox.Count > 0;
            HasNext = Next.Count > 0;
            HasReceived = Received.Count > 0;
            ShowEmptyInBox = !HasInBox && (HasDueList || _catalog.Count > 0);
            ShowEmptyNext = HasDueList && !HasNext;
            ShowEmptyReceived = HasDueList && !HasReceived;
            InBoxHeading = "In the box (" + InBox.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            NextHeading = "Who should be next (" + _plan.NextSlotCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            ReceivedHeading = "Who actually received a laptop (" + Received.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            Summary = Describe();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private string Describe()
    {
        if (_snapshot.Due.Count == 0)
        {
            return _catalog.Count == 0
                ? "Import the spreadsheet of laptops due this financial year. The oldest delivery date is who should be next."
                : "Import the spreadsheet of laptops due this financial year so old machines already in stock are not counted as new boxes.";
        }

        var text = _plan.DueCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " due · "
            + _plan.InBox.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " in the box · "
            + _plan.NextSlotCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " should be next · "
            + _plan.Received.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " received a boxed laptop";
        if (_plan.CompletedCount > 0)
        {
            text += " · "
                + _plan.CompletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " marked completed";
        }

        var pending = _plan.Next.Count(row => row.IsNext && !row.StillHasLaptop);
        if (pending > 0)
        {
            text += ". "
                + pending.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + (pending == 1 ? " next laptop is" : " next laptops are")
                + " still pending a check that the person has the old laptop.";
        }

        if (_catalog.Count == 0)
            text += " Refresh the hardware list to see which serials are in the stock room.";

        return text;
    }

    private LaptopCheckNote NoteFor(string serial)
    {
        var note = _snapshot.Notes.FirstOrDefault(item => item.SerialNumber.Equals(serial, StringComparison.OrdinalIgnoreCase));
        if (note is not null)
            return note;
        note = new LaptopCheckNote { SerialNumber = serial };
        _snapshot.Notes.Add(note);
        return note;
    }

    private void EnsureLoaded()
    {
        var scope = _scope();
        if (_loaded && string.Equals(scope, _loadedScope, StringComparison.OrdinalIgnoreCase))
            return;
        _loadedScope = scope;
        _snapshot = _store.Load(scope) ?? new LaptopRefreshSnapshot();
        _loaded = true;
    }

    private void Save() => _store.Save(_scope(), _snapshot);
}

public partial class NextLaptopRow : ObservableObject
{
    private readonly Action<NextLaptopRow> _save;
    private bool _ready;

    public NextLaptopRow(NextLaptop source, Action<NextLaptopRow> save)
    {
        _save = save;
        SerialNumber = source.SerialNumber;
        Hostname = source.Hostname;
        Model = source.Model;
        AssignedTo = source.AssignedTo;
        Location = source.Location;
        AgeText = source.AgeText;
        DeliveredText = source.DeliveredText;
        Detail = source.Detail;
        UserName = source.UserName;
        HandledBy = source.HandledBy;
        ListedModel = source.ListedModel;
        QueueText = source.QueueText;
        StillHasLaptop = source.StillHasLaptop;
        OnHold = source.OnHold;
        Status = source.Status;
        Comment = source.Comment;
        _ready = true;
    }

    public string SerialNumber { get; }
    public string Hostname { get; }
    public string Model { get; }
    public string AssignedTo { get; }
    public string Location { get; }
    public string AgeText { get; }
    public string DeliveredText { get; }
    public string Detail { get; }
    public string UserName { get; }
    public string HandledBy { get; }
    public string ListedModel { get; }
    public string QueueText { get; }
    public string CheckText => StillHasLaptop ? "Confirmed" : "Pending";

    [ObservableProperty] private bool stillHasLaptop;
    [ObservableProperty] private bool onHold;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string comment = "";

    public void Detach() => _ready = false;

    partial void OnStillHasLaptopChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckText));
        if (_ready)
            _save(this);
    }

    partial void OnOnHoldChanged(bool value)
    {
        if (_ready)
            _save(this);
    }

    partial void OnStatusChanged(string value)
    {
        if (_ready)
            _save(this);
    }

    partial void OnCommentChanged(string value)
    {
        if (_ready)
            _save(this);
    }
}

public partial class ReceivedLaptopRow : ObservableObject
{
    private readonly Action<ReceivedLaptopRow> _save;
    private bool _ready;

    public ReceivedLaptopRow(ReceivedLaptopView source, Action<ReceivedLaptopRow> save)
    {
        _save = save;
        SerialNumber = source.SerialNumber;
        Model = source.Model;
        AssignedTo = source.AssignedTo;
        PreviousState = source.PreviousState;
        OldLaptop = source.OldLaptop;
        SeenText = source.SeenAt == default
            ? ""
            : source.SeenAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        Reason = source.Reason;
        _ready = true;
    }

    public string SerialNumber { get; }
    public string Model { get; }
    public string AssignedTo { get; }
    public string PreviousState { get; }
    public string OldLaptop { get; }
    public string SeenText { get; }

    [ObservableProperty] private string reason = "";

    public void Detach() => _ready = false;

    partial void OnReasonChanged(string value)
    {
        if (_ready)
            _save(this);
    }
}
