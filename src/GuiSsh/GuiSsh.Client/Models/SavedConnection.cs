namespace GuiSsh.Client.Models;

public enum AuthMethod
{
    Password,
    PrivateKey,
    KeyAndPassphrase
}

public class SavedConnection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;
    public int SortOrder { get; set; }
    public DateTime LastConnected { get; set; }
    public string DefaultPath { get; set; } = "~";
    public bool HasStoredCredentials { get; set; }
}
