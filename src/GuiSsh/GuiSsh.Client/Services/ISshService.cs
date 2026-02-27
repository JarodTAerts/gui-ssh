using GuiSsh.Client.Models;

namespace GuiSsh.Client.Services;

public interface ISshService
{
    Task<ConnectResult> ConnectAsync(string host, int port, string username, string password);
    Task<ConnectResult> ConnectWithKeyAsync(string host, int port, string username, string privateKey, string? passphrase = null);

    /// <summary>
    /// After user approves an unknown host key: trust it and reconnect.
    /// </summary>
    Task<ConnectResult> TrustHostKeyAndConnectAsync(
        string host, int port, string username, string password,
        string fingerprint, string algorithm,
        string? privateKey = null, string? passphrase = null);

    Task DisconnectAsync(string sessionId);
    Task<CommandResult> ExecuteAsync(string sessionId, string command);
    Task<bool> IsConnectedAsync(string sessionId);
    Task<(byte[] Data, string FileName)> DownloadFileAsync(string sessionId, string remotePath);
    Task UploadFileAsync(string sessionId, string remotePath, Stream fileStream, string fileName);
}
