namespace GuiSsh.Client.Models;

public class CommandResult
{
    public string StdOut { get; set; } = string.Empty;
    public string StdErr { get; set; } = string.Empty;
    public int ExitCode { get; set; }
    public bool Success => ExitCode == 0;
}
