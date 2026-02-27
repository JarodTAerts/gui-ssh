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
    private readonly HostKeyStore _hostKeyStore;
    private readonly ILogger<SshSessionManager> _logger;

    public SshSessionManager(SshConnectionFactory factory, HostKeyStore hostKeyStore, ILogger<SshSessionManager> logger)
    {
        _factory = factory;
        _hostKeyStore = hostKeyStore;
        _logger = logger;
    }

    /// <summary>
    /// Connects with password authentication and host key verification.
    /// Returns a ConnectResult that may require host key approval before use.
    /// </summary>
    public async Task<Client.Models.ConnectResult> ConnectAsync(string host, int port, string username, string password, string ownerId)
    {
        var verification = _factory.CreateClientWithHostKeyCheck(host, port, username, password, _hostKeyStore);
        return await ConnectWithVerificationAsync(verification, host, port, ownerId);
    }

    /// <summary>
    /// Connects with private key authentication and host key verification.
    /// </summary>
    public async Task<Client.Models.ConnectResult> ConnectWithKeyAsync(string host, int port, string username, string privateKey, string? passphrase, string ownerId)
    {
        var verification = _factory.CreateClientWithKeyAndHostKeyCheck(host, port, username, privateKey, passphrase, _hostKeyStore);
        return await ConnectWithVerificationAsync(verification, host, port, ownerId);
    }

    /// <summary>
    /// Second step: after user approves an unknown host key, trust it and reconnect.
    /// </summary>
    public async Task TrustAndReconnectAsync(string host, int port, string fingerprint, string algorithm)
    {
        await _hostKeyStore.TrustAsync(host, port, fingerprint, algorithm);
    }

    private async Task<Client.Models.ConnectResult> ConnectWithVerificationAsync(
        SshConnectionFactory.HostKeyVerificationResult verification,
        string host, int port, string ownerId)
    {
        var client = verification.Client;

        try
        {
            await Task.Run(() => client.Connect());
        }
        catch (Renci.SshNet.Common.SshConnectionException) when (verification.Status == HostKeyStatus.Changed)
        {
            // Host key mismatch — reject
            client.Dispose();
            _logger.LogWarning("Host key CHANGED for {Host}:{Port} — connection rejected (possible MITM)",
                host, port);
            return new Client.Models.ConnectResult
            {
                HostKeyChanged = true,
                HostKeyFingerprint = verification.Fingerprint,
                HostKeyAlgorithm = verification.Algorithm
            };
        }

        // After Connect(), the HostKeyReceived event has fired and populated verification.Status.
        if (verification.Status == HostKeyStatus.Unknown)
        {
            // Unknown host key — disconnect, return fingerprint for user approval
            try { client.Disconnect(); } catch { }
            client.Dispose();

            _logger.LogInformation("Unknown host key for {Host}:{Port} ({Algorithm}: {Fingerprint}) — awaiting user approval",
                host, port, verification.Algorithm, verification.Fingerprint);

            return new Client.Models.ConnectResult
            {
                NeedsHostKeyApproval = true,
                HostKeyFingerprint = verification.Fingerprint,
                HostKeyAlgorithm = verification.Algorithm
            };
        }

        if (verification.Status == HostKeyStatus.Changed)
        {
            // Host key changed — reject (shouldn't reach here since CanTrust=false triggers exception)
            try { client.Disconnect(); } catch { }
            client.Dispose();

            return new Client.Models.ConnectResult
            {
                HostKeyChanged = true,
                HostKeyFingerprint = verification.Fingerprint,
                HostKeyAlgorithm = verification.Algorithm
            };
        }

        // Trusted — proceed normally
        return await FinalizeSessionAsync(client, host, port, ownerId);
    }

    private async Task<Client.Models.ConnectResult> FinalizeSessionAsync(SshClient client, string host, int port, string ownerId)
    {
        var sessionId = Guid.NewGuid().ToString();
        var shellStream = _factory.CreateShellStream(client);

        var session = new ActiveSession
        {
            SessionId = sessionId,
            OwnerId = ownerId,
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

        _logger.LogInformation("SSH session {SessionId} connected to {Host}:{Port} for user {OwnerId}",
            sessionId, host, port, ownerId);

        return new Client.Models.ConnectResult { SessionId = sessionId };
    }

    /// <summary>
    /// Returns the session only if the caller is the owner. Returns null otherwise.
    /// </summary>
    public ActiveSession? GetSessionForOwner(string sessionId, string ownerId)
    {
        var session = GetSession(sessionId);
        if (session == null)
            return null;

        if (session.OwnerId != ownerId)
        {
            _logger.LogWarning("Session {SessionId} access denied for user {UserId} (owner: {OwnerId})",
                sessionId, ownerId, session.OwnerId);
            return null;
        }

        return session;
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

    /// <summary>
    /// Downloads a file from the remote server via SFTP and returns it as a byte array.
    /// For directories, creates a tar.gz archive first.
    /// </summary>
    public async Task<(byte[] Data, string FileName)> DownloadFileAsync(string sessionId, string remotePath)
    {
        var session = GetSession(sessionId);
        if (session == null || !session.IsConnected)
            throw new InvalidOperationException("Session not found or disconnected.");

        // Check if it's a directory
        var checkResult = await Task.Run(() =>
        {
            using var cmd = session.SshClient.RunCommand($"test -d {EscapePath(remotePath)} && echo DIR || echo FILE");
            return cmd.Result.Trim();
        });

        if (checkResult == "DIR")
        {
            return await DownloadDirectoryAsArchiveAsync(session, remotePath);
        }

        return await Task.Run(() =>
        {
            using var sftp = new SftpClient(session.SshClient.ConnectionInfo);
            sftp.Connect();
            try
            {
                using var ms = new MemoryStream();
                sftp.DownloadFile(remotePath, ms);
                var fileName = remotePath.Split('/').Last();
                return (ms.ToArray(), fileName);
            }
            finally
            {
                sftp.Disconnect();
            }
        });
    }

    private async Task<(byte[] Data, string FileName)> DownloadDirectoryAsArchiveAsync(ActiveSession session, string remotePath)
    {
        var dirName = remotePath.TrimEnd('/').Split('/').Last();
        var archiveName = $"/tmp/.guissh_{Guid.NewGuid():N}.tar.gz";

        try
        {
            // Create tar.gz on remote
            var tarResult = await Task.Run(() =>
            {
                var parentDir = remotePath[..remotePath.TrimEnd('/').LastIndexOf('/')];
                if (string.IsNullOrEmpty(parentDir)) parentDir = "/";
                using var cmd = session.SshClient.RunCommand($"cd {EscapePath(parentDir)} && tar czf {archiveName} {EscapePath(dirName)}");
                return cmd;
            });

            if (tarResult.ExitStatus != 0)
                throw new InvalidOperationException($"Failed to archive directory: {tarResult.Error}");

            // Download the archive via SFTP
            var data = await Task.Run(() =>
            {
                using var sftp = new SftpClient(session.SshClient.ConnectionInfo);
                sftp.Connect();
                try
                {
                    using var ms = new MemoryStream();
                    sftp.DownloadFile(archiveName, ms);
                    return ms.ToArray();
                }
                finally
                {
                    sftp.Disconnect();
                }
            });

            return (data, $"{dirName}.tar.gz");
        }
        finally
        {
            // Clean up remote archive
            _ = Task.Run(() =>
            {
                try { session.SshClient.RunCommand($"rm -f {archiveName}"); } catch { }
            });
        }
    }

    /// <summary>
    /// Uploads a file to the remote server via SFTP.
    /// </summary>
    public async Task UploadFileAsync(string sessionId, string remotePath, Stream fileStream)
    {
        var session = GetSession(sessionId);
        if (session == null || !session.IsConnected)
            throw new InvalidOperationException("Session not found or disconnected.");

        await Task.Run(() =>
        {
            using var sftp = new SftpClient(session.SshClient.ConnectionInfo);
            sftp.Connect();
            try
            {
                sftp.UploadFile(fileStream, remotePath, canOverride: true);
            }
            finally
            {
                sftp.Disconnect();
            }
        });

        session.LastActivity = DateTime.UtcNow;
        _logger.LogInformation("Uploaded file to {Path} on session {SessionId}", remotePath, sessionId);
    }

    /// <summary>
    /// Gets file info (size, whether it's a directory) for a remote path.
    /// </summary>
    public async Task<(long Size, bool IsDirectory, string FileName)> GetFileInfoAsync(string sessionId, string remotePath)
    {
        var session = GetSession(sessionId);
        if (session == null || !session.IsConnected)
            throw new InvalidOperationException("Session not found or disconnected.");

        return await Task.Run(() =>
        {
            using var sftp = new SftpClient(session.SshClient.ConnectionInfo);
            sftp.Connect();
            try
            {
                var attrs = sftp.GetAttributes(remotePath);
                var fileName = remotePath.TrimEnd('/').Split('/').Last();
                return (attrs.Size, attrs.IsDirectory, fileName);
            }
            finally
            {
                sftp.Disconnect();
            }
        });
    }

    /// <summary>
    /// Streams a file download directly to the output stream without buffering the entire file in memory.
    /// For directories, creates a tar.gz archive first.
    /// </summary>
    public async Task StreamDownloadAsync(string sessionId, string remotePath, Stream outputStream, Action<long>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId);
        if (session == null || !session.IsConnected)
            throw new InvalidOperationException("Session not found or disconnected.");

        // Check if it's a directory
        var checkResult = await Task.Run(() =>
        {
            using var cmd = session.SshClient.RunCommand($"test -d {EscapePath(remotePath)} && echo DIR || echo FILE");
            return cmd.Result.Trim();
        }, cancellationToken);

        if (checkResult == "DIR")
        {
            await StreamDirectoryDownloadAsync(session, remotePath, outputStream, onProgress, cancellationToken);
            return;
        }

        await Task.Run(() =>
        {
            using var sftp = new SftpClient(session.SshClient.ConnectionInfo);
            sftp.OperationTimeout = TimeSpan.FromHours(4); // Allow very long transfers
            sftp.BufferSize = 1024 * 64; // 64KB buffer for better throughput
            sftp.Connect();
            try
            {
                sftp.DownloadFile(remotePath, outputStream, bytesDownloaded =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    onProgress?.Invoke((long)bytesDownloaded);
                });
            }
            finally
            {
                sftp.Disconnect();
            }
        }, cancellationToken);

        session.LastActivity = DateTime.UtcNow;
    }

    private async Task StreamDirectoryDownloadAsync(ActiveSession session, string remotePath, Stream outputStream, Action<long>? onProgress, CancellationToken cancellationToken = default)
    {
        var dirName = remotePath.TrimEnd('/').Split('/').Last();
        var archiveName = $"/tmp/.guissh_{Guid.NewGuid():N}.tar.gz";

        try
        {
            // Create tar.gz on remote
            var tarResult = await Task.Run(() =>
            {
                var parentDir = remotePath[..remotePath.TrimEnd('/').LastIndexOf('/')];
                if (string.IsNullOrEmpty(parentDir)) parentDir = "/";
                using var cmd = session.SshClient.RunCommand($"cd {EscapePath(parentDir)} && tar czf {archiveName} {EscapePath(dirName)}");
                return cmd;
            }, cancellationToken);

            if (tarResult.ExitStatus != 0)
                throw new InvalidOperationException($"Failed to archive directory: {tarResult.Error}");

            // Stream the archive via SFTP
            await Task.Run(() =>
            {
                using var sftp = new SftpClient(session.SshClient.ConnectionInfo);
                sftp.OperationTimeout = TimeSpan.FromHours(4);
                sftp.BufferSize = 1024 * 64;
                sftp.Connect();
                try
                {
                    sftp.DownloadFile(archiveName, outputStream, bytesDownloaded =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        onProgress?.Invoke((long)bytesDownloaded);
                    });
                }
                finally
                {
                    sftp.Disconnect();
                }
            }, cancellationToken);
        }
        finally
        {
            // Clean up remote archive
            _ = Task.Run(() =>
            {
                try { session.SshClient.RunCommand($"rm -f {archiveName}"); } catch { }
            });
        }

        session.LastActivity = DateTime.UtcNow;
    }

    private static string EscapePath(string path) => $"'{path.Replace("'", "'\\''")}'";

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
    public required string OwnerId { get; set; }
    public required SshClient SshClient { get; set; }
    public required ShellStream ShellStream { get; set; }
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsConnected => SshClient?.IsConnected ?? false;
}
