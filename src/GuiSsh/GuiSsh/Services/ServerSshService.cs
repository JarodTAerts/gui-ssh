using GuiSsh.Client.Models;
using GuiSsh.Client.Services;

namespace GuiSsh.Services;

/// <summary>
/// Server-side ISshService implementation that calls SshSessionManager directly.
/// Used when components are running in Server render mode.
/// </summary>
public class ServerSshService : ISshService
{
    private readonly SshSessionManager _manager;

    public ServerSshService(SshSessionManager manager)
    {
        _manager = manager;
    }

    public async Task<string> ConnectAsync(string host, int port, string username, string password)
    {
        return await _manager.ConnectAsync(host, port, username, password);
    }

    public async Task DisconnectAsync(string sessionId)
    {
        await _manager.DisconnectAsync(sessionId);
    }

    public async Task<CommandResult> ExecuteAsync(string sessionId, string command)
    {
        return await _manager.ExecuteAsync(sessionId, command);
    }

    public Task<bool> IsConnectedAsync(string sessionId)
    {
        return Task.FromResult(_manager.IsConnected(sessionId));
    }
}
