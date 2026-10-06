namespace ServiceNowDesk.Services;

public static class AttachmentStorage
{
    public static string Write(string fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var folder = Path.Combine(Path.GetTempPath(), "ServiceNowDesk", "downloads");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SafeName(fileName));
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string SafeName(string fileName)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim());
        foreach (var character in Path.GetInvalidFileNameChars())
            name = name.Replace(character, '_');
        return name.Length == 0 ? "attachment" : name;
    }
}
