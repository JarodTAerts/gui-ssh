using GuiSsh.Client.Models;

namespace GuiSsh.Client.Services;

public interface ISshService
{
    Task<string> ConnectAsync(string host, int port, string username, string password);
    Task DisconnectAsync(string sessionId);
    Task<CommandResult> ExecuteAsync(string sessionId, string command);
    Task<bool> IsConnectedAsync(string sessionId);
}
