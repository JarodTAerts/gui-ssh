using System.Globalization;
using GuiSsh.Client.Models;

namespace GuiSsh.Client.Services;

/// <summary>
/// Parses shell command output (ls, stat, etc.) into structured data.
/// </summary>
public class ShellOutputParser
{
    /// <summary>
    /// Parses output of: ls -la --color=never --time-style=long-iso
    /// </summary>
    public List<FileEntry> ParseLsOutput(string output, string currentPath)
    {
        var entries = new List<FileEntry>();
        if (string.IsNullOrWhiteSpace(output))
            return entries;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Skip the "total" line
            if (line.StartsWith("total "))
                continue;

            var entry = ParseLsLine(line, currentPath);
            if (entry != null && entry.Name != "." && entry.Name != "..")
                entries.Add(entry);
        }

        return entries;
    }

    private FileEntry? ParseLsLine(string line, string currentPath)
    {
        // Expected format (long-iso):
        // drwxr-xr-x  4 user group 4096 2026-02-24 10:30 dirname
        // -rw-r--r--  1 user group  220 2026-02-20 14:00 .bashrc
        // lrwxrwxrwx  1 user group   11 2026-02-24 10:30 link -> target

        if (line.Length < 10)
            return null;

        try
        {
            var parts = SplitLsLine(line);
            if (parts.Length < 8)
                return null;

            var permissions = parts[0];
            var owner = parts[2];
            var group = parts[3];
            var sizeStr = parts[4];
            var dateStr = parts[5];
            var timeStr = parts[6];

            // The name is everything after the date/time, which starts at index 7
            var name = string.Join(' ', parts[7..]);
            var symlinkTarget = string.Empty;

            // Handle symlinks: name -> target
            var arrowIdx = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                symlinkTarget = name[(arrowIdx + 4)..];
                name = name[..arrowIdx];
            }

            var type = permissions[0] switch
            {
                'd' => FileEntryType.Directory,
                'l' => FileEntryType.Symlink,
                '-' => FileEntryType.File,
                _ => FileEntryType.Other
            };

            long.TryParse(sizeStr, out var size);
            DateTime.TryParse($"{dateStr} {timeStr}", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var modified);

            var fullPath = currentPath.TrimEnd('/') + "/" + name;

            return new FileEntry
            {
                Name = name,
                FullPath = fullPath,
                Type = type,
                Permissions = permissions,
                Owner = owner,
                Group = group,
                Size = size,
                Modified = modified,
                SymlinkTarget = symlinkTarget
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Splits ls -la output into parts, collapsing whitespace.
    /// </summary>
    private static string[] SplitLsLine(string line)
    {
        return line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
