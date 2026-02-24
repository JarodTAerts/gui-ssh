namespace GuiSsh.Client.Models;

public class ConnectionFormResult
{
    public SavedConnection Connection { get; set; } = new();
    public string Password { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public bool ConnectNow { get; set; }
    public bool SaveCredentials { get; set; } = true;
}
