using GuiSsh.Client.Models;
using GuiSsh.Client.Services;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace GuiSsh.Services;

/// <summary>
/// Server-side ISshService implementation that calls SshSessionManager directly.
/// Used when components are running in Server render mode.
/// Extracts the authenticated user identity to enforce session ownership.
/// </summary>
public class ServerSshService : ISshService
{
    private readonly SshSessionManager _manager;
    private readonly ConnectionPolicy _policy;
    private readonly AuthenticationStateProvider _authStateProvider;

    public ServerSshService(SshSessionManager manager, ConnectionPolicy policy, AuthenticationStateProvider authStateProvider)
    {
        _manager = manager;
        _policy = policy;
        _authStateProvider = authStateProvider;
    }

    private async Task<string> GetUserIdAsync()
    {
        var state = await _authStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException("Not authenticated.");
    }

    private async Task EnsureAllowedAsync(string host, int port)
    {
        var (allowed, reason) = await _policy.EvaluateAsync(host, port);
        if (!allowed)
            throw new InvalidOperationException(reason);
    }

    public async Task<ConnectResult> ConnectAsync(string host, int port, string username, string password)
    {
        await EnsureAllowedAsync(host, port);
        var userId = await GetUserIdAsync();
        return await _manager.ConnectAsync(host, port, username, password, userId);
    }

    public async Task<ConnectResult> ConnectWithKeyAsync(string host, int port, string username, string privateKey, string? passphrase = null)
    {
        await EnsureAllowedAsync(host, port);
        var userId = await GetUserIdAsync();
        return await _manager.ConnectWithKeyAsync(host, port, username, privateKey, passphrase, userId);
    }

    public async Task<ConnectResult> TrustHostKeyAndConnectAsync(
        string host, int port, string username, string password,
        string fingerprint, string algorithm,
        string? privateKey = null, string? passphrase = null)
    {
        await EnsureAllowedAsync(host, port);
        var userId = await GetUserIdAsync();
        await _manager.TrustAndReconnectAsync(host, port, fingerprint, algorithm);

        if (!string.IsNullOrEmpty(privateKey))
            return await _manager.ConnectWithKeyAsync(host, port, username, privateKey, passphrase, userId);

        return await _manager.ConnectAsync(host, port, username, password, userId);
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

    public async Task<(byte[] Data, string FileName)> DownloadFileAsync(string sessionId, string remotePath)
    {
        return await _manager.DownloadFileAsync(sessionId, remotePath);
    }

    public async Task UploadFileAsync(string sessionId, string remotePath, Stream fileStream, string fileName)
    {
        await _manager.UploadFileAsync(sessionId, remotePath, fileStream);
    }
}
