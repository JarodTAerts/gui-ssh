using System.Collections.Concurrent;
using Renci.SshNet;

namespace GuiSsh.Services;

/// <summary>
/// Server-side singleton that manages all active SSH sessions.
/// Sessions are keyed by a client-generated session ID.
/// </summary>
public class SshSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();
    private readonly SshConnectionFactory _factory;
    private readonly ILogger<SshSessionManager> _logger;

    public SshSessionManager(SshConnectionFactory factory, ILogger<SshSessionManager> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<string> ConnectAsync(string host, int port, string username, string password)
    {
        var sessionId = Guid.NewGuid().ToString();

        var client = _factory.CreateClient(host, port, username, password);

        await Task.Run(() => client.Connect());

        var shellStream = _factory.CreateShellStream(client);

        var session = new ActiveSession
        {
            SessionId = sessionId,
            SshClient = client,
            ShellStream = shellStream,
            LastActivity = DateTime.UtcNow
        };

        if (!_sessions.TryAdd(sessionId, session))
        {
            shellStream.Dispose();
            client.Disconnect();
            client.Dispose();
            throw new InvalidOperationException("Failed to register session.");
        }

        _logger.LogInformation("SSH session {SessionId} connected to {Host}:{Port}", sessionId, host, port);
        return sessionId;
    }

    public ActiveSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        if (session != null)
            session.LastActivity = DateTime.UtcNow;
        return session;
    }

    public ShellStream? GetShellStream(string sessionId)
    {
        return GetSession(sessionId)?.ShellStream;
    }

    public async Task<Client.Models.CommandResult> ExecuteAsync(string sessionId, string command)
    {
        var session = GetSession(sessionId);
        if (session == null || !session.IsConnected)
        {
            return new Client.Models.CommandResult
            {
                ExitCode = -1,
                StdErr = "Session not found or disconnected."
            };
        }

        try
        {
            var result = await Task.Run(() =>
            {
                using var cmd = session.SshClient.RunCommand(command);
                return new Client.Models.CommandResult
                {
                    StdOut = cmd.Result,
                    StdErr = cmd.Error,
                    ExitCode = cmd.ExitStatus ?? -1
                };
            });

            session.LastActivity = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute command on session {SessionId}", sessionId);
            return new Client.Models.CommandResult
            {
                ExitCode = -1,
                StdErr = ex.Message
            };
        }
    }

    public bool IsConnected(string sessionId)
    {
        var session = GetSession(sessionId);
        return session?.IsConnected ?? false;
    }

    public Task DisconnectAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            try
            {
                session.ShellStream?.Dispose();
                if (session.SshClient.IsConnected)
                    session.SshClient.Disconnect();
                session.SshClient.Dispose();
                _logger.LogInformation("SSH session {SessionId} disconnected", sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during disconnect of session {SessionId}", sessionId);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Evicts sessions idle longer than the specified duration. Called by background service.
    /// </summary>
    public void EvictExpired(TimeSpan maxIdle)
    {
        var cutoff = DateTime.UtcNow - maxIdle;
        var expired = _sessions.Where(kvp => kvp.Value.LastActivity < cutoff).ToList();

        foreach (var kvp in expired)
        {
            _logger.LogInformation("Evicting idle session {SessionId}", kvp.Key);
            _ = DisconnectAsync(kvp.Key);
        }
    }

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            try
            {
                kvp.Value.ShellStream?.Dispose();
                if (kvp.Value.SshClient.IsConnected)
                    kvp.Value.SshClient.Disconnect();
                kvp.Value.SshClient.Dispose();
            }
            catch { }
        }
        _sessions.Clear();
    }
}

public class ActiveSession
{
    public required string SessionId { get; set; }
    public required SshClient SshClient { get; set; }
    public required ShellStream ShellStream { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsConnected => SshClient?.IsConnected ?? false;
}
