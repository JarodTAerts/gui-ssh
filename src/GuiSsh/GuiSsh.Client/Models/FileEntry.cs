namespace GuiSsh.Client.Models;

public enum FileEntryType
{
    File,
    Directory,
    Symlink,
    Other
}

public class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public FileEntryType Type { get; set; }
    public string Permissions { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public string SymlinkTarget { get; set; } = string.Empty;

    public bool IsDirectory => Type == FileEntryType.Directory;
    public bool IsFile => Type == FileEntryType.File;
    public bool IsHidden => Name.StartsWith('.');

    public string Extension => IsFile && Name.Contains('.')
        ? Name[(Name.LastIndexOf('.') + 1)..].ToLowerInvariant()
        : string.Empty;
}
