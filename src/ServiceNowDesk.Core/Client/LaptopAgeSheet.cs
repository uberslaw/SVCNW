using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ServiceNowDesk.Client;

/// <summary>
/// One row from the due-laptop spreadsheet (ANZ devices) or a tracking sheet (Melbourne upgrades).
/// </summary>
public sealed class DueLaptop
{
    public string Location { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Model { get; set; } = "";
    public string AssignedTo { get; set; } = "";
    public string State { get; set; } = "";
    public string Substate { get; set; } = "";
    public string Stockroom { get; set; } = "";
    public DateOnly? DeliveryDate { get; set; }
    public string AgeText { get; set; } = "";
    public string Department { get; set; } = "";
    public string Warranty { get; set; } = "";
    public string Priority { get; set; } = "";
    public string UserName { get; set; } = "";
    public string HandledBy { get; set; } = "";
    public string ListedModel { get; set; } = "";
    public bool OnHold { get; set; }
    public string Status { get; set; } = "";
    public string Comment { get; set; } = "";
}

/// <summary>
/// Rows read from an .xlsx or .csv export. Tracking columns are the Melbourne sheet
/// (on hold, status, vacation student, comments) and are applied only when that header exists.
/// </summary>
public sealed class LaptopAgeImport
{
    public List<DueLaptop> Devices { get; init; } = [];
    public bool HasSerial { get; init; }
    public bool HasOnHold { get; init; }
    public bool HasStatus { get; init; }
    public bool HasComment { get; init; }

    /// <summary>
    /// A stock export: serials in stock or in transit, with no person column.
    /// The Sydney upgrade list is the other file, one row per person.
    /// </summary>
    public bool IsStockList { get; init; }

    public bool HasTracking => HasOnHold || HasStatus || HasComment;
}

/// <summary>
/// Reads the ANZ "devices 4 years and older" export and the Melbourne upgrade sheet.
/// Delivery dates may be real dates or Excel serial numbers (44840 is 6 Oct 2022).
/// </summary>
public static class LaptopAgeSheet
{
    public static LaptopAgeImport Read(Stream stream, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var name = fileName ?? "";
        if (name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return ReadXlsx(stream);

        if (name.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save the sheet as .xlsx or .csv, then import that file.");

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return ReadCsv(reader.ReadToEnd());
    }

    public static LaptopAgeImport ReadCsv(string text)
    {
        var table = ParseCsv(text ?? "");
        return FromTable(table);
    }

    public static LaptopAgeImport ReadXlsx(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var sheetPath = FirstSheetPath(zip);
            var entry = zip.GetEntry(sheetPath) ?? throw new InvalidDataException("This workbook has no sheet to read.");
            var shared = ReadSharedStrings(zip);
            using var sheetStream = entry.Open();
            var sheet = XDocument.Load(sheetStream);
            return FromTable(ReadSheetRows(sheet, shared));
        }
        catch (Exception ex) when (ex is InvalidDataException && ex.Message.StartsWith("This workbook", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or XmlException or ArgumentException)
        {
            throw new InvalidDataException("This spreadsheet could not be read. Save it as .xlsx or .csv and try again.");
        }
    }

    /// <summary>Excel serials use the 1899-12-30 epoch. Day-first text is used for CSV dates.</summary>
    public static DateOnly? ParseDelivery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
            && serial is >= 20000 and <= 80000)
        {
            var date = new DateTime(1899, 12, 30).AddDays(Math.Floor(serial));
            return DateOnly.FromDateTime(date);
        }

        string[] formats = ["yyyy-MM-dd", "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy"];
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return DateOnly.FromDateTime(exact);

        if (DateTime.TryParse(trimmed, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var gb))
            return DateOnly.FromDateTime(gb);

        return null;
    }

    public static bool Truthy(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("y", StringComparison.OrdinalIgnoreCase)
            || text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("1", StringComparison.Ordinal)
            || text.Equals("x", StringComparison.OrdinalIgnoreCase)
            || text.Equals("on hold", StringComparison.OrdinalIgnoreCase);
    }

    private static LaptopAgeImport FromTable(IReadOnlyList<IReadOnlyList<string>> table)
    {
        if (table.Count == 0)
            throw new InvalidDataException("This file has no rows.");

        var headerIndex = FindHeader(table);
        if (headerIndex < 0)
        {
            if (table.Any(LooksLikeUpgradeRow))
                return FromUpgradeRows(table);
            throw new InvalidDataException("This file needs a Serial, Computename, or Assigned to column.");
        }

        var columns = MapColumns(table[headerIndex]);
        if (!columns.ContainsKey("serial")
            && !columns.ContainsKey("hostname")
            && !columns.ContainsKey("assigned"))
            throw new InvalidDataException("This file needs a Serial, Computename, or Assigned to column.");

        var devices = new List<DueLaptop>();
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var rowIndex = headerIndex + 1; rowIndex < table.Count; rowIndex++)
        {
            var row = table[rowIndex];
            var device = new DueLaptop
            {
                Location = Cell(row, columns, "location"),
                SerialNumber = Cell(row, columns, "serial"),
                Hostname = Cell(row, columns, "hostname"),
                Model = Cell(row, columns, "model"),
                AssignedTo = Cell(row, columns, "assigned"),
                State = Cell(row, columns, "state"),
                Substate = Cell(row, columns, "substate"),
                Stockroom = Cell(row, columns, "stockroom"),
                AgeText = Cell(row, columns, "age"),
                Department = Cell(row, columns, "department"),
                Warranty = Cell(row, columns, "warranty"),
                Priority = Cell(row, columns, "priority"),
                Status = Cell(row, columns, "status"),
                Comment = VacationComment(Cell(row, columns, "comment"), Truthy(Cell(row, columns, "vacation"))),
                OnHold = Truthy(Cell(row, columns, "onhold"))
            };
            device.DeliveryDate = ParseDelivery(Cell(row, columns, "delivery"));

            if (device.Location.StartsWith("Applied filters", StringComparison.OrdinalIgnoreCase))
                continue;

            var blank = string.IsNullOrWhiteSpace(device.SerialNumber)
                && string.IsNullOrWhiteSpace(device.Hostname)
                && string.IsNullOrWhiteSpace(device.AssignedTo)
                && string.IsNullOrWhiteSpace(device.Model);
            if (blank)
                continue;

            if (!string.IsNullOrWhiteSpace(device.SerialNumber))
            {
                if (!seenSerials.Add(device.SerialNumber.Trim()))
                    continue;
            }

            if (device.Status.Equals("on hold", StringComparison.OrdinalIgnoreCase))
                device.OnHold = true;

            devices.Add(device);
        }

        return new LaptopAgeImport
        {
            Devices = devices,
            HasSerial = columns.ContainsKey("serial") && devices.Any(device => !string.IsNullOrWhiteSpace(device.SerialNumber)),
            HasOnHold = columns.ContainsKey("onhold"),
            HasStatus = columns.ContainsKey("status"),
            HasComment = columns.ContainsKey("comment") || columns.ContainsKey("vacation"),
            IsStockList = columns.ContainsKey("serial")
                && !columns.ContainsKey("assigned")
                && (columns.ContainsKey("state") || columns.ContainsKey("delivery"))
        };
    }

    /// <summary>
    /// Sydney upgrades sheet: no title row. Location, serial, hostname, model, state, age,
    /// person, user name, who did it, comment, Completed or Not completed, model code.
    /// </summary>
    private static LaptopAgeImport FromUpgradeRows(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var devices = new List<DueLaptop>();
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table)
        {
            if (!LooksLikeUpgradeRow(row))
                continue;

            var handled = At(row, 8);
            var comment = At(row, 9);
            if (!LooksLikePerson(handled))
            {
                if (handled.Length > 0)
                    comment = JoinComment(comment, handled);
                handled = "";
            }

            if (IsCompletedWord(comment))
                comment = "";

            var device = new DueLaptop
            {
                Location = At(row, 0),
                SerialNumber = At(row, 1),
                Hostname = At(row, 2),
                Model = At(row, 3),
                State = At(row, 4),
                AgeText = At(row, 5),
                AssignedTo = At(row, 6),
                UserName = At(row, 7),
                HandledBy = handled,
                Comment = comment,
                Status = At(row, 10),
                ListedModel = At(row, 11)
            };
            if (device.Status.Equals("on hold", StringComparison.OrdinalIgnoreCase))
                device.OnHold = true;
            if (!string.IsNullOrWhiteSpace(device.SerialNumber) && !seenSerials.Add(device.SerialNumber))
                continue;
            devices.Add(device);
        }

        if (devices.Count == 0)
            throw new InvalidDataException("This file has no laptop rows.");

        return new LaptopAgeImport
        {
            Devices = devices,
            HasSerial = true,
            HasStatus = true,
            HasComment = true
        };
    }

    private static bool LooksLikeUpgradeRow(IReadOnlyList<string> row)
    {
        if (row.Count < 7)
            return false;
        var serial = At(row, 1);
        var person = At(row, 6);
        if (serial.Length < 4 || person.Length < 2)
            return false;
        if (serial.Contains(' ', StringComparison.Ordinal))
            return false;
        return person.Any(char.IsLetter);
    }

    private static bool LooksLikePerson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 40 || trimmed.Any(char.IsDigit))
            return false;
        if (trimmed.Contains("user", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("finish", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("leave", StringComparison.OrdinalIgnoreCase))
            return false;
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 1 and <= 4 && words[0].Any(char.IsLetter);
    }

    private static bool IsCompletedWord(string text)
    {
        var letters = new string(text.Where(char.IsLetter).ToArray());
        return letters.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || letters.Equals("complete", StringComparison.OrdinalIgnoreCase)
            || letters.Equals("compledted", StringComparison.OrdinalIgnoreCase)
            || letters.Equals("complleted", StringComparison.OrdinalIgnoreCase);
    }

    private static string VacationComment(string comment, bool vacation)
    {
        if (!vacation)
            return comment ?? "";
        return JoinComment(comment, "Vacation student");
    }

    private static string JoinComment(string? comment, string extra)
    {
        var text = (comment ?? "").Trim();
        if (text.Length == 0)
            return extra;
        if (text.Contains(extra, StringComparison.OrdinalIgnoreCase))
            return text;
        return text + ". " + extra;
    }

    private static string At(IReadOnlyList<string> row, int index) =>
        index < row.Count ? (row[index] ?? "").Trim() : "";

    private static int FindHeader(IReadOnlyList<IReadOnlyList<string>> table)
    {
        var limit = Math.Min(table.Count, 10);
        for (var index = 0; index < limit; index++)
        {
            var mapped = MapColumns(table[index]);
            if (mapped.ContainsKey("serial") || mapped.ContainsKey("hostname") || mapped.ContainsKey("assigned"))
                return index;
        }

        return -1;
    }

    private static Dictionary<string, int> MapColumns(IReadOnlyList<string> headers)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headers.Count; index++)
        {
            var field = FieldFor(NormalizeHeader(headers[index]));
            if (field is null)
                continue;

            var priority = FieldPriority(field.Value.Name, NormalizeHeader(headers[index]));
            if (rank.TryGetValue(field.Value.Name, out var existing) && existing <= priority)
                continue;

            columns[field.Value.Name] = index;
            rank[field.Value.Name] = priority;
        }

        return columns;
    }

    private static (string Name, int Priority)? FieldFor(string header)
    {
        var name = header switch
        {
            "location" => "location",
            "serial" or "serial number" or "serial no" or "serial num" => "serial",
            "hostname" or "host name" or "computename" or "computer name" or "computer" or "configuration item" or "ci" => "hostname",
            "model" => "model",
            "assigned to" or "assigned" => "assigned",
            "state" or "install status" => "state",
            "substate" or "sub state" or "substatus" => "substate",
            "stockroom" or "stock room" => "stockroom",
            "delivery date" or "delivered" or "delivery" => "delivery",
            "age" => "age",
            "department" => "department",
            "on hold" => "onhold",
            "status" => "status",
            "vacation student" => "vacation",
            "comments" or "comment" or "reason" => "comment",
            _ when header.StartsWith("priority", StringComparison.Ordinal) => "priority",
            _ when header.StartsWith("warranty", StringComparison.Ordinal) => "warranty",
            _ => ""
        };
        return name.Length == 0 ? null : (name, FieldPriority(name, header));
    }

    /// <summary>Lower wins. Hostname beats Computename beats Configuration item.</summary>
    private static int FieldPriority(string field, string header) => field switch
    {
        "hostname" when header is "hostname" or "host name" => 0,
        "hostname" when header is "computename" or "computer name" or "computer" => 1,
        "hostname" => 2,
        _ => 0
    };

    private static string NormalizeHeader(string header)
    {
        var trimmed = (header ?? "").Trim().TrimStart('\uFEFF');
        var buffer = new StringBuilder(trimmed.Length);
        var space = false;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
                space = false;
                continue;
            }

            if (!space && buffer.Length > 0)
            {
                buffer.Append(' ');
                space = true;
            }
        }

        return buffer.ToString().Trim();
    }

    private static string Cell(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> columns, string field)
    {
        if (!columns.TryGetValue(field, out var index) || index < 0 || index >= row.Count)
            return "";
        return (row[index] ?? "").Trim();
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var delimiter = DetectDelimiter(text);
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            if (ch == '"')
            {
                quoted = true;
                continue;
            }

            if (ch == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
                continue;
            }

            if (ch == '\r')
                continue;

            if (ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                continue;
            }

            cell.Append(ch);
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static char DetectDelimiter(string text)
    {
        var line = text;
        var newline = text.IndexOf('\n');
        if (newline >= 0)
            line = text[..newline];
        var commas = line.Count(ch => ch == ',');
        var semis = line.Count(ch => ch == ';');
        return semis > commas ? ';' : ',';
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        var workbook = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("This workbook has no sheet to read.");
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException("This workbook has no sheet to read.");
        XDocument workbookDoc;
        XDocument relDoc;
        using (var stream = workbook.Open())
            workbookDoc = XDocument.Load(stream);
        using (var stream = rels.Open())
            relDoc = XDocument.Load(stream);

        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace officeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var sheet = workbookDoc.Descendants(main + "sheet").FirstOrDefault()
            ?? throw new InvalidDataException("This workbook has no sheet to read.");
        var id = sheet.Attribute(officeRel + "id")?.Value ?? "";
        var target = relDoc.Descendants(packageRel + "Relationship")
            .FirstOrDefault(rel => rel.Attribute("Id")?.Value == id)
            ?.Attribute("Target")?.Value ?? "worksheets/sheet1.xml";
        target = target.Replace('\\', '/').TrimStart('/');
        if (target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            return target;
        return "xl/" + target;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return [];

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var strings = new List<string>();
        foreach (var item in document.Descendants(main + "si"))
        {
            var text = string.Concat(item.Descendants(main + "t").Select(node => node.Value));
            strings.Add(text);
        }

        return strings;
    }

    private static List<List<string>> ReadSheetRows(XDocument sheet, IReadOnlyList<string> shared)
    {
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<List<string>>();
        foreach (var row in sheet.Descendants(main + "row"))
        {
            var cells = new Dictionary<int, string>();
            var max = 0;
            var position = 0;
            foreach (var cell in row.Elements(main + "c"))
            {
                position++;
                var reference = cell.Attribute("r")?.Value ?? "";
                var index = ColumnIndex(reference);
                if (index <= 0)
                    index = position;
                cells[index] = CellText(cell, main, shared);
                if (index > max)
                    max = index;
            }

            if (max == 0)
            {
                rows.Add([]);
                continue;
            }

            var values = new List<string>(max);
            for (var index = 1; index <= max; index++)
                values.Add(cells.TryGetValue(index, out var value) ? value : "");
            rows.Add(values);
        }

        return rows;
    }

    private static string CellText(XElement cell, XNamespace main, IReadOnlyList<string> shared)
    {
        var type = cell.Attribute("t")?.Value ?? "";
        if (type == "inlineStr")
            return string.Concat(cell.Descendants(main + "t").Select(node => node.Value));

        var raw = cell.Element(main + "v")?.Value ?? "";
        if (type == "s")
        {
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < shared.Count)
                return shared[index];
            return "";
        }

        return raw;
    }

    private static int ColumnIndex(string cellRef)
    {
        var index = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch))
                break;
            index = (index * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return index;
    }
}
