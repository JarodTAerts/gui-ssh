using System.Text;
using System.Text.RegularExpressions;

namespace GuiSsh.Client.Services;

/// <summary>
/// Generates properly escaped shell commands for GUI file operations.
/// All file operations go through this class to prevent shell injection.
/// </summary>
public class ShellCommandBuilder
{
    public string ListDirectory(string path)
    {
        // Append trailing / to ensure symlink directories are listed by contents, not as the link itself
        var normalized = path.TrimEnd('/') + "/";
        var escaped = ShellEscape(normalized);
        return $"ls -la --color=never --time-style=long-iso {escaped}";
    }

    public string ReadFile(string path, int maxBytes = 1_048_576)
    {
        var escaped = ShellEscape(path);
        return $"head -c {maxBytes} {escaped}";
    }

    public string WriteFile(string path, string content)
    {
        var escaped = ShellEscape(path);
        // Use a heredoc with a unique delimiter
        var delimiter = "GUISSH_EOF";
        return $"cat > {escaped} << '{delimiter}'\n{content}\n{delimiter}";
    }

    public string CreateFile(string path)
        => $"touch {ShellEscape(path)}";

    public string CreateDirectory(string path)
        => $"mkdir -p {ShellEscape(path)}";

    public string Delete(string path, bool recursive = false)
        => recursive
            ? $"rm -rf {ShellEscape(path)}"
            : $"rm {ShellEscape(path)}";

    public string Rename(string oldPath, string newPath)
        => $"mv {ShellEscape(oldPath)} {ShellEscape(newPath)}";

    public string Copy(string source, string destination, bool recursive = false)
        => recursive
            ? $"cp -r {ShellEscape(source)} {ShellEscape(destination)}"
            : $"cp {ShellEscape(source)} {ShellEscape(destination)}";

    public string GetFileInfo(string path)
        => $"stat {ShellEscape(path)}";

    public string ChangePermissions(string path, string mode)
        => $"chmod {ShellEscape(mode)} {ShellEscape(path)}";

    public string DetectOs()
        => "uname -s";

    public string GetCurrentDirectory()
        => "pwd";

    public string GetHomeDirectory()
        => "echo $HOME";

    public string FileType(string path)
        => $"file --mime-type -b {ShellEscape(path)}";

    public string DownloadBase64(string path)
        => $"base64 {ShellEscape(path)}";

    /// <summary>
    /// Escapes a string for safe use in a POSIX shell by wrapping it in single quotes,
    /// with internal single quotes handled properly.
    /// </summary>
    public static string ShellEscape(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "''";

        // If the string contains no special characters, return as-is
        if (Regex.IsMatch(input, @"^[a-zA-Z0-9._/~-]+$"))
            return input;

        // Wrap in single quotes, escaping any internal single quotes
        return "'" + input.Replace("'", "'\\''") + "'";
    }
}
