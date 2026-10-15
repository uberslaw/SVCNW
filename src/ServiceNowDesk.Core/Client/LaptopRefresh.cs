using System.Globalization;
using System.Text;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

/// <summary>
/// Local note for one old laptop: whether they still have it, and the Melbourne tracking columns.
/// </summary>
public sealed class LaptopCheckNote
{
    public string SerialNumber { get; set; } = "";
    public bool StillHasLaptop { get; set; }
    public bool OnHold { get; set; }
    public bool VacationStudent { get; set; }
    public string Status { get; set; } = "";
    public string Comment { get; set; } = "";
}

/// <summary>A serial last seen in the stock room, waiting to be assigned.</summary>
public sealed class BoxedSerial
{
    public string SerialNumber { get; set; } = "";
    public string Model { get; set; } = "";
    public string Location { get; set; } = "";
    public string Stockroom { get; set; } = "";
    public string StateLabel { get; set; } = "";
    public string SubstateLabel { get; set; } = "";
}

/// <summary>
/// A serial that was in the box and is now assigned. <see cref="Reason"/> is typed locally,
/// the same job as the Comments column on the Melbourne upgrade sheet.
/// </summary>
public sealed class ReceivedLaptop
{
    public string SerialNumber { get; set; } = "";
    public string Model { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public string PreviousState { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTimeOffset SeenAt { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>Due list, check notes, stock-room watch, and laptops that have since been assigned.</summary>
public sealed class LaptopRefreshSnapshot
{
    public DateTimeOffset ImportedAt { get; set; }
    public string SourceName { get; set; } = "";
    public List<DueLaptop> Due { get; set; } = [];
    public List<DueLaptop> Available { get; set; } = [];
    public string AvailableSource { get; set; } = "";
    public List<LaptopCheckNote> Notes { get; set; } = [];
    public List<BoxedSerial> Watching { get; set; } = [];
    public List<ReceivedLaptop> Received { get; set; } = [];
}

public sealed record InBoxLaptop(string SerialNumber, string Model, string Location, string Stockroom, string State);

public sealed record NextLaptop(
    string SerialNumber,
    string Hostname,
    string Model,
    string AssignedTo,
    string Location,
    string AgeText,
    string DeliveredText,
    string Department,
    string Warranty,
    string Priority,
    string Detail,
    string UserName,
    string HandledBy,
    string ListedModel,
    bool IsNext,
    bool StillHasLaptop,
    bool OnHold,
    string Status,
    string Comment,
    string QueueText);

public sealed record ReceivedLaptopView(
    string SerialNumber,
    string Model,
    string AssignedTo,
    string PreviousState,
    string Location,
    DateTimeOffset SeenAt,
    string OldLaptop,
    string Reason);

public sealed class LaptopRefreshPlan
{
    public IReadOnlyList<InBoxLaptop> InBox { get; init; } = [];
    public IReadOnlyList<NextLaptop> Next { get; init; } = [];
    public IReadOnlyList<ReceivedLaptopView> Received { get; init; } = [];
    public int DueCount { get; init; }
    public int CompletedCount { get; init; }
    public int NextSlotCount { get; init; }
}

public readonly record struct TrackingApplyResult(int Updated, IReadOnlyList<string> Unmatched);

/// <summary>
/// Who should get the next laptop in the box, and who a boxed serial was actually assigned to.
/// Age comes from the imported delivery date. The stock room is ServiceNow state In stock or In box
/// with nobody assigned. Serials that are already on the due list are old machines, not new boxes.
/// </summary>
public static class LaptopRefreshLogic
{
    public static void ReplaceDueList(LaptopRefreshSnapshot snapshot, LaptopAgeImport import, string sourceName, DateTimeOffset importedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(import);
        snapshot.Due = import.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.SerialNumber))
            .Select(CopyDevice)
            .ToList();
        snapshot.SourceName = sourceName ?? "";
        snapshot.ImportedAt = importedAt;
        if (import.HasTracking)
            ApplyNotes(snapshot, import);
    }

    public static void ReplaceAvailable(LaptopRefreshSnapshot snapshot, LaptopAgeImport import, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(import);
        snapshot.Available = import.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.SerialNumber))
            .Select(CopyDevice)
            .ToList();
        snapshot.AvailableSource = sourceName ?? "";
    }

    public static TrackingApplyResult ApplyNotes(LaptopRefreshSnapshot snapshot, LaptopAgeImport import)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(import);
        if (!import.HasTracking)
            return new TrackingApplyResult(0, []);

        var unmatched = new List<string>();
        var updated = 0;
        foreach (var row in import.Devices)
        {
            var matches = MatchDue(snapshot.Due, row);
            if (matches.Count == 0)
            {
                var label = FirstName(row.Hostname, row.SerialNumber, row.AssignedTo);
                if (label.Length > 0)
                    unmatched.Add(label);
                continue;
            }

            foreach (var device in matches)
            {
                var note = NoteFor(snapshot, NoteKey(device));
                if (import.HasOnHold)
                    note.OnHold = row.OnHold;
                if (import.HasStatus)
                    note.Status = row.Status ?? "";
                if (import.HasComment && !string.IsNullOrWhiteSpace(row.Comment))
                    note.Comment = row.Comment.Trim();
                if (row.OnHold)
                    note.OnHold = true;
                if (!string.IsNullOrWhiteSpace(row.Department))
                    device.Department = row.Department;
                if (!string.IsNullOrWhiteSpace(row.Warranty))
                    device.Warranty = row.Warranty;
                if (!string.IsNullOrWhiteSpace(row.Priority))
                    device.Priority = row.Priority;
                updated++;
            }
        }

        return new TrackingApplyResult(updated, unmatched);
    }

    /// <summary>
    /// Remembers serials currently in the box. A serial that was in the box and now has someone
    /// assigned is recorded under <see cref="LaptopRefreshSnapshot.Received"/>.
    /// Returns true when the saved watch list or the received list changed.
    /// </summary>
    public static bool Observe(
        LaptopRefreshSnapshot snapshot,
        IReadOnlyList<HardwareAsset> catalog,
        bool catalogIsComplete,
        IEnumerable<Choice>? statuses,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        catalog ??= [];
        var dueSerials = DueSerials(snapshot);
        var received = snapshot.Received.ToDictionary(item => item.SerialNumber, StringComparer.OrdinalIgnoreCase);
        var current = IndexBySerial(catalog);
        var changed = false;

        foreach (var watched in snapshot.Watching)
        {
            if (string.IsNullOrWhiteSpace(watched.SerialNumber) || dueSerials.Contains(watched.SerialNumber))
                continue;
            if (!current.TryGetValue(watched.SerialNumber, out var asset))
                continue;
            if (!HasPerson(asset))
                continue;
            if (!received.TryGetValue(watched.SerialNumber, out var existing))
            {
                existing = new ReceivedLaptop
                {
                    SerialNumber = asset.SerialNumber.Trim(),
                    SeenAt = now
                };
                snapshot.Received.Add(existing);
                received[existing.SerialNumber] = existing;
                changed = true;
            }

            var assigned = asset.AssignedTo.Display?.Trim() ?? "";
            var previous = FormatState(watched.StateLabel, watched.SubstateLabel);
            var location = asset.Location.Display?.Trim() ?? watched.Location;
            if (!string.Equals(existing.AssignedTo, assigned, StringComparison.Ordinal)
                || !string.Equals(existing.Model, asset.Model ?? "", StringComparison.Ordinal)
                || !string.Equals(existing.PreviousState, previous, StringComparison.Ordinal)
                || !string.Equals(existing.Location, location, StringComparison.Ordinal))
            {
                existing.AssignedTo = assigned;
                existing.Model = asset.Model ?? "";
                existing.PreviousState = previous;
                existing.Location = location;
                if (existing.SeenAt == default)
                    existing.SeenAt = now;
                changed = true;
            }
        }

        var nextWatch = new List<BoxedSerial>();
        if (!catalogIsComplete)
        {
            foreach (var watched in snapshot.Watching)
            {
                if (string.IsNullOrWhiteSpace(watched.SerialNumber))
                    continue;
                if (dueSerials.Contains(watched.SerialNumber) || received.ContainsKey(watched.SerialNumber))
                    continue;
                if (current.ContainsKey(watched.SerialNumber))
                    continue;
                nextWatch.Add(watched);
            }
        }

        foreach (var asset in catalog)
        {
            if (!IsInTheBox(asset, statuses))
                continue;
            if (string.IsNullOrWhiteSpace(asset.SerialNumber))
                continue;
            if (dueSerials.Contains(asset.SerialNumber) || received.ContainsKey(asset.SerialNumber))
                continue;
            if (nextWatch.Any(item => item.SerialNumber.Equals(asset.SerialNumber, StringComparison.OrdinalIgnoreCase)))
                continue;
            nextWatch.Add(ToBoxed(asset));
        }

        nextWatch = nextWatch
            .OrderBy(item => item.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!SameWatch(snapshot.Watching, nextWatch))
        {
            snapshot.Watching = nextWatch;
            changed = true;
        }

        return changed;
    }

    public static bool IsInTheBox(HardwareAsset asset, IEnumerable<Choice>? statuses)
    {
        if (asset is null || !HardwareCatalog.IsComputer(asset) || HasPerson(asset))
            return false;

        var state = FirstText(asset.InstallStatusLabel, asset.InstallStatus);
        var sub = FirstText(asset.SubstatusLabel, asset.Substatus);
        if (IsBoxWord(state) || IsBoxWord(sub))
            return true;

        if (!HardwareCatalog.IsInStock(asset.InstallStatusLabel, asset.InstallStatus, statuses ?? HardwareCatalog.InstallStatuses))
            return false;

        return string.IsNullOrWhiteSpace(sub)
            || HardwareCatalog.SameLabel(sub, HardwareCatalog.Available)
            || IsBoxWord(sub);
    }

    public static LaptopRefreshPlan Build(
        LaptopRefreshSnapshot snapshot,
        IReadOnlyList<HardwareAsset> catalog,
        IReadOnlyList<string>? offices,
        IEnumerable<Choice>? statuses,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        catalog ??= [];
        var officeList = offices ?? [];
        var bySerial = IndexBySerial(catalog);
        var notes = snapshot.Notes
            .Where(note => !string.IsNullOrWhiteSpace(note.SerialNumber))
            .GroupBy(note => note.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var dueSerials = DueSerials(snapshot);

        var inBox = new List<InBoxLaptop>();
        if (snapshot.Available.Count > 0)
        {
            foreach (var device in snapshot.Available)
            {
                if (string.IsNullOrWhiteSpace(device.SerialNumber) || dueSerials.Contains(device.SerialNumber))
                    continue;
                if (!InOffices(device.Location, officeList))
                    continue;
                inBox.Add(new InBoxLaptop(
                    device.SerialNumber.Trim(),
                    device.Model ?? "",
                    device.Location ?? "",
                    "",
                    device.State ?? ""));
            }
        }
        else
        {
            foreach (var asset in catalog)
            {
                if (!IsInTheBox(asset, statuses))
                    continue;
                if (string.IsNullOrWhiteSpace(asset.SerialNumber) || dueSerials.Contains(asset.SerialNumber))
                    continue;
                if (!InOffices(asset.Location.Display, officeList))
                    continue;
                inBox.Add(new InBoxLaptop(
                    asset.SerialNumber.Trim(),
                    asset.Model ?? "",
                    asset.Location.Display ?? "",
                    asset.Stockroom.Display ?? "",
                    FormatState(asset.InstallStatusLabel, asset.SubstatusLabel)));
            }
        }

        inBox.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.SerialNumber, right.SerialNumber));

        var completed = 0;
        var waiting = new List<PendingRow>();
        foreach (var device in snapshot.Due)
        {
            if (!InOffices(device.Location, officeList))
                continue;
            if (string.IsNullOrWhiteSpace(device.AssignedTo))
                continue;

            var key = NoteKey(device);
            notes.TryGetValue(key, out var note);
            note ??= new LaptopCheckNote();
            if (IsCompleted(note))
            {
                completed++;
                continue;
            }

            bySerial.TryGetValue(device.SerialNumber ?? "", out var asset);
            var queue = QueueKind(device, asset, note, snapshot.Received, statuses);
            waiting.Add(new PendingRow(device, note, asset, queue));
        }

        waiting.Sort(ComparePending);
        var slots = inBox.Count;
        var next = new List<NextLaptop>(waiting.Count);
        var offered = 0;
        foreach (var row in waiting)
        {
            var isNext = row.Queue == QueueKindValue.Eligible && offered < slots;
            if (isNext)
                offered++;
            next.Add(ToNext(row, isNext, today));
        }

        var receivedViews = new List<ReceivedLaptopView>();
        foreach (var item in snapshot.Received.OrderByDescending(entry => entry.SeenAt))
        {
            if (!ShowReceived(item, snapshot.Due, officeList))
                continue;
            receivedViews.Add(new ReceivedLaptopView(
                item.SerialNumber,
                item.Model,
                item.AssignedTo,
                item.PreviousState,
                item.Location,
                item.SeenAt,
                DescribeOldLaptop(snapshot.Due, item.AssignedTo, today),
                item.Reason ?? ""));
        }

        return new LaptopRefreshPlan
        {
            InBox = inBox,
            Next = next,
            Received = receivedViews,
            DueCount = snapshot.Due.Count(device => InOffices(device.Location, officeList)),
            CompletedCount = completed,
            NextSlotCount = offered
        };
    }

    public static string FormatAge(DateOnly delivery, DateOnly today)
    {
        if (delivery > today)
            return "";

        var years = today.Year - delivery.Year;
        var months = today.Month - delivery.Month;
        if (today.Day < delivery.Day)
            months--;
        if (months < 0)
        {
            years--;
            months += 12;
        }

        if (years < 0)
            return "";
        return years.ToString(CultureInfo.InvariantCulture) + "y " + months.ToString(CultureInfo.InvariantCulture) + "m";
    }

    public static bool SamePerson(string? left, string? right)
    {
        var a = PersonTokens(left);
        var b = PersonTokens(right);
        if (a.Count == 0 || b.Count == 0 || a.Count != b.Count)
            return false;
        return a.SetEquals(b);
    }

    public static string Format(LaptopRefreshPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new StringBuilder();
        builder.Append("Who should be next");
        builder.Append('\n');
        builder.Append("Queue,Computename,Model,Assigned to,User name,Done by,Model code,Age,Delivery date,Location,Still has the old laptop,On hold,Status,Comment,Detail");
        builder.Append('\n');
        foreach (var row in plan.Next)
        {
            Append(builder, [
                row.QueueText,
                row.Hostname,
                row.Model,
                row.AssignedTo,
                row.UserName,
                row.HandledBy,
                row.ListedModel,
                row.AgeText,
                row.DeliveredText,
                row.Location,
                row.StillHasLaptop ? "Yes" : "Pending",
                row.OnHold ? "Yes" : "",
                row.Status,
                row.Comment,
                row.Detail
            ]);
        }

        builder.Append('\n');
        builder.Append("In the box");
        builder.Append('\n');
        builder.Append("Serial,Model,Location,Stockroom,State");
        builder.Append('\n');
        foreach (var row in plan.InBox)
            Append(builder, [row.SerialNumber, row.Model, row.Location, row.Stockroom, row.State]);

        builder.Append('\n');
        builder.Append("Who actually received a laptop");
        builder.Append('\n');
        builder.Append("Serial,Model,Assigned to,Previous state,Location,Seen,Old laptop,Reason");
        builder.Append('\n');
        foreach (var row in plan.Received)
        {
            var seen = row.SeenAt == default
                ? ""
                : row.SeenAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            Append(builder, [row.SerialNumber, row.Model, row.AssignedTo, row.PreviousState, row.Location, seen, row.OldLaptop, row.Reason]);
        }

        return builder.ToString();
    }

    public static bool IsCompleted(LaptopCheckNote? note) =>
        note is not null && (Same(note.Status, "Completed") || Same(note.Status, "Complete"));

    public static bool IsOnHold(LaptopCheckNote? note) =>
        note is not null && (note.OnHold || Same(note.Status, "On hold"));

    private static NextLaptop ToNext(PendingRow row, bool isNext, DateOnly today)
    {
        var device = row.Device;
        var age = device.DeliveryDate is DateOnly delivered
            ? FormatAge(delivered, today)
            : device.AgeText ?? "";
        var deliveredText = device.DeliveryDate is DateOnly date
            ? date.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : "";
        var queue = row.Queue switch
        {
            QueueKindValue.OnHold => "On hold",
            QueueKindValue.Check => "Check ServiceNow",
            QueueKindValue.AlreadyReceived => "Already received",
            QueueKindValue.Eligible when isNext => "Next",
            _ => "Later"
        };
        return new NextLaptop(
            device.SerialNumber ?? "",
            device.Hostname ?? "",
            device.Model ?? "",
            device.AssignedTo ?? "",
            device.Location ?? "",
            age,
            deliveredText,
            device.Department ?? "",
            device.Warranty ?? "",
            device.Priority ?? "",
            Detail(device, row.Asset, row.Queue),
            device.UserName ?? "",
            device.HandledBy ?? "",
            device.ListedModel ?? "",
            isNext,
            row.Note.StillHasLaptop,
            IsOnHold(row.Note),
            row.Note.Status ?? "",
            row.Note.Comment ?? "",
            queue);
    }

    private static string Detail(DueLaptop device, HardwareAsset? asset, QueueKindValue queue)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(device.Department))
            parts.Add(device.Department.Trim());
        if (!string.IsNullOrWhiteSpace(device.Warranty))
            parts.Add("Warranty " + device.Warranty.Trim());
        if (!string.IsNullOrWhiteSpace(device.Priority))
            parts.Add(device.Priority.Trim());
        if (!string.IsNullOrWhiteSpace(device.UserName))
            parts.Add(device.UserName.Trim());
        if (!string.IsNullOrWhiteSpace(device.HandledBy))
            parts.Add("Done by " + device.HandledBy.Trim());
        if (!string.IsNullOrWhiteSpace(device.ListedModel))
            parts.Add(device.ListedModel.Trim());
        if (asset is not null)
        {
            var state = FormatState(asset.InstallStatusLabel, asset.SubstatusLabel);
            var person = asset.AssignedTo.Display?.Trim() ?? "";
            if (queue == QueueKindValue.Check)
            {
                if (!HardwareRegionExport.IsInUseComputer(asset))
                    parts.Add(string.IsNullOrWhiteSpace(state) ? "ServiceNow does not show this laptop in use" : "ServiceNow state is " + state);
                else if (!SamePerson(person, device.AssignedTo))
                    parts.Add(string.IsNullOrWhiteSpace(person)
                        ? "ServiceNow shows this laptop assigned, with no name"
                        : "ServiceNow shows this laptop with " + person);
            }
        }
        else if (!SheetSaysInUse(device.State))
        {
            parts.Add("Sheet state is " + device.State.Trim());
        }

        return string.Join(" · ", parts);
    }

    private static QueueKindValue QueueKind(
        DueLaptop device,
        HardwareAsset? asset,
        LaptopCheckNote note,
        IReadOnlyList<ReceivedLaptop> received,
        IEnumerable<Choice>? statuses)
    {
        if (IsOnHold(note))
            return QueueKindValue.OnHold;
        if (received.Any(item => SamePerson(item.AssignedTo, device.AssignedTo)))
            return QueueKindValue.AlreadyReceived;
        if (string.IsNullOrWhiteSpace(device.AssignedTo))
            return QueueKindValue.Check;
        if (asset is not null)
        {
            var inUse = HardwareRegionExport.IsInUseComputer(asset, statuses);
            if (!inUse || !SamePerson(asset.AssignedTo.Display, device.AssignedTo))
                return QueueKindValue.Check;
            return QueueKindValue.Eligible;
        }

        return SheetSaysInUse(device.State) ? QueueKindValue.Eligible : QueueKindValue.Check;
    }

    private static bool SheetSaysInUse(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
            return true;
        if (HardwareCatalog.SameLabel(state, HardwareCatalog.InUse))
            return true;
        if (IsBoxWord(state)
            || HardwareCatalog.SameLabel(state, HardwareCatalog.InStock)
            || HardwareCatalog.SameLabel(state, HardwareCatalog.Retired)
            || HardwareCatalog.SameLabel(state, HardwareCatalog.Missing)
            || HardwareCatalog.SameLabel(state, HardwareCatalog.InMaintenance)
            || HardwareCatalog.SameLabel(state, HardwareCatalog.InTransit))
            return false;
        return true;
    }

    private static int ComparePending(PendingRow left, PendingRow right)
    {
        var leftDate = left.Device.DeliveryDate ?? DateOnly.MaxValue;
        var rightDate = right.Device.DeliveryDate ?? DateOnly.MaxValue;
        var byDate = leftDate.CompareTo(rightDate);
        if (byDate != 0)
            return byDate;

        var byAge = AgeMonths(right.Device.AgeText).CompareTo(AgeMonths(left.Device.AgeText));
        if (byAge != 0)
            return byAge;

        return StringComparer.OrdinalIgnoreCase.Compare(left.Device.AssignedTo, right.Device.AssignedTo);
    }

    private static int AgeMonths(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return -1;
        var years = 0;
        var months = 0;
        var number = 0;
        var have = false;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                number = (number * 10) + (ch - '0');
                have = true;
                continue;
            }

            if (!have)
                continue;
            if (ch is 'y' or 'Y')
                years = number;
            else if (ch is 'm' or 'M')
                months = number;
            number = 0;
            have = false;
        }

        return (years * 12) + months;
    }

    private static string DescribeOldLaptop(IReadOnlyList<DueLaptop> due, string? person, DateOnly today)
    {
        DueLaptop? oldest = null;
        foreach (var device in due)
        {
            if (!SamePerson(device.AssignedTo, person))
                continue;
            if (oldest is null || CompareDelivery(device, oldest) < 0)
                oldest = device;
        }

        if (oldest is null)
            return "Not on the due list";

        var age = oldest.DeliveryDate is DateOnly delivered ? FormatAge(delivered, today) : oldest.AgeText;
        return string.Join(" · ", new[] { oldest.Hostname, oldest.Model, age }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static int CompareDelivery(DueLaptop left, DueLaptop right)
    {
        var leftDate = left.DeliveryDate ?? DateOnly.MaxValue;
        var rightDate = right.DeliveryDate ?? DateOnly.MaxValue;
        return leftDate.CompareTo(rightDate);
    }

    private static bool ShowReceived(ReceivedLaptop item, IReadOnlyList<DueLaptop> due, IReadOnlyList<string> offices)
    {
        if (offices.Count == 0)
            return true;
        if (InOffices(item.Location, offices))
            return true;
        return due.Any(device => InOffices(device.Location, offices) && SamePerson(device.AssignedTo, item.AssignedTo));
    }

    private static bool InOffices(string? location, IReadOnlyList<string> offices)
    {
        if (offices.Count == 0)
            return true;
        return offices.Any(office =>
            HardwareOfficeNames.SamePlace(location, office) || HardwareOfficeNames.LoosePlace(location, office));
    }

    private static List<DueLaptop> MatchDue(IReadOnlyList<DueLaptop> due, DueLaptop row)
    {
        if (!string.IsNullOrWhiteSpace(row.SerialNumber))
        {
            var bySerial = due.Where(device => device.SerialNumber.Equals(row.SerialNumber, StringComparison.OrdinalIgnoreCase)).ToList();
            if (bySerial.Count > 0)
                return bySerial;
        }

        if (!string.IsNullOrWhiteSpace(row.Hostname))
        {
            var byHost = due.Where(device => device.Hostname.Equals(row.Hostname, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byHost.Count > 0)
                return byHost;
        }

        if (!string.IsNullOrWhiteSpace(row.AssignedTo))
        {
            var byPerson = due.Where(device => SamePerson(device.AssignedTo, row.AssignedTo)).ToList();
            if (byPerson.Count > 0)
                return byPerson;
        }

        return [];
    }

    private static LaptopCheckNote NoteFor(LaptopRefreshSnapshot snapshot, string key)
    {
        var note = snapshot.Notes.FirstOrDefault(item => item.SerialNumber.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (note is not null)
            return note;
        note = new LaptopCheckNote { SerialNumber = key };
        snapshot.Notes.Add(note);
        return note;
    }

    private static string NoteKey(DueLaptop device) =>
        !string.IsNullOrWhiteSpace(device.SerialNumber) ? device.SerialNumber.Trim() : (device.Hostname ?? "").Trim();

    private static DueLaptop CopyDevice(DueLaptop device) => new()
    {
        Location = device.Location,
        SerialNumber = device.SerialNumber.Trim(),
        Hostname = device.Hostname,
        Model = device.Model,
        AssignedTo = device.AssignedTo,
        State = device.State,
        Substate = device.Substate,
        Stockroom = device.Stockroom,
        DeliveryDate = device.DeliveryDate,
        AgeText = device.AgeText,
        Department = device.Department,
        Warranty = device.Warranty,
        Priority = device.Priority,
        UserName = device.UserName,
        HandledBy = device.HandledBy,
        ListedModel = device.ListedModel,
        OnHold = device.OnHold,
        Status = device.Status,
        Comment = device.Comment
    };

    private static HashSet<string> DueSerials(LaptopRefreshSnapshot snapshot) =>
        snapshot.Due
            .Select(device => device.SerialNumber)
            .Where(serial => !string.IsNullOrWhiteSpace(serial))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, HardwareAsset> IndexBySerial(IReadOnlyList<HardwareAsset> catalog)
    {
        var index = new Dictionary<string, HardwareAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in catalog)
        {
            if (string.IsNullOrWhiteSpace(asset.SerialNumber) || index.ContainsKey(asset.SerialNumber))
                continue;
            index[asset.SerialNumber] = asset;
        }

        return index;
    }

    private static bool HasPerson(HardwareAsset asset) =>
        !string.IsNullOrWhiteSpace(asset.AssignedTo.Display) || !string.IsNullOrWhiteSpace(asset.AssignedTo.SysId);

    private static bool IsBoxWord(string? label)
    {
        var text = (label ?? "").Trim();
        return text.Equals("in box", StringComparison.OrdinalIgnoreCase)
            || text.Equals("in the box", StringComparison.OrdinalIgnoreCase)
            || text.Contains("in box", StringComparison.OrdinalIgnoreCase);
    }

    private static BoxedSerial ToBoxed(HardwareAsset asset) => new()
    {
        SerialNumber = asset.SerialNumber.Trim(),
        Model = asset.Model ?? "",
        Location = asset.Location.Display ?? "",
        Stockroom = asset.Stockroom.Display ?? "",
        StateLabel = asset.InstallStatusLabel ?? "",
        SubstateLabel = asset.SubstatusLabel ?? ""
    };

    private static bool SameWatch(IReadOnlyList<BoxedSerial> left, IReadOnlyList<BoxedSerial> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (!left[index].SerialNumber.Equals(right[index].SerialNumber, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(left[index].StateLabel, right[index].StateLabel, StringComparison.Ordinal)
                || !string.Equals(left[index].SubstateLabel, right[index].SubstateLabel, StringComparison.Ordinal)
                || !string.Equals(left[index].Model, right[index].Model, StringComparison.Ordinal)
                || !string.Equals(left[index].Location, right[index].Location, StringComparison.Ordinal)
                || !string.Equals(left[index].Stockroom, right[index].Stockroom, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string FormatState(string? state, string? substate)
    {
        var label = (state ?? "").Trim();
        var sub = (substate ?? "").Trim();
        if (label.Length == 0)
            return sub;
        if (sub.Length == 0)
            return label;
        return label + " · " + sub;
    }

    private static string FirstText(string? label, string? value) =>
        !string.IsNullOrWhiteSpace(label) ? label.Trim() : (value ?? "").Trim();

    private static HashSet<string> PersonTokens(string? name)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(name))
            return tokens;
        var token = new StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(ch);
                continue;
            }

            if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
            tokens.Add(token.ToString());
        return tokens;
    }

    private static string FirstName(params string?[] parts)
    {
        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
                return part.Trim();
        }

        return "";
    }

    private static bool Same(string? left, string right) =>
        HardwareCatalog.SameLabel(left, right);

    private static void Append(StringBuilder builder, IReadOnlyList<string> cells)
    {
        for (var index = 0; index < cells.Count; index++)
        {
            if (index > 0)
                builder.Append(',');
            builder.Append(Escape(cells[index] ?? ""));
        }

        builder.Append('\n');
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private enum QueueKindValue
    {
        Eligible,
        OnHold,
        Check,
        AlreadyReceived
    }

    private sealed record PendingRow(DueLaptop Device, LaptopCheckNote Note, HardwareAsset? Asset, QueueKindValue Queue);
}
