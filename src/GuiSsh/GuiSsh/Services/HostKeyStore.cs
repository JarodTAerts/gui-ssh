using System.Text.Json;

namespace GuiSsh.Services;

/// <summary>
/// Server-side store for known SSH host key fingerprints (Trust-on-First-Use).
/// Persists to a JSON file so host keys survive container restarts when
/// mounted on a persistent volume.
/// </summary>
public class HostKeyStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<HostKeyStore> _logger;
    private Dictionary<string, KnownHost> _knownHosts = new();

    public HostKeyStore(IConfiguration config, ILogger<HostKeyStore> logger)
    {
        _logger = logger;
        _filePath = config.GetValue<string>("KnownHostsPath")
            ?? Path.Combine(AppContext.BaseDirectory, "known_hosts.json");

        Load();
    }

    /// <summary>
    /// Checks whether a host key fingerprint is known, unknown, or has changed.
    /// </summary>
    public HostKeyStatus Check(string host, int port, string fingerprint)
    {
        var key = $"{host}:{port}";
        if (!_knownHosts.TryGetValue(key, out var stored))
            return HostKeyStatus.Unknown;

        return stored.Fingerprint == fingerprint
            ? HostKeyStatus.Trusted
            : HostKeyStatus.Changed;
    }

    /// <summary>
    /// Records a host key fingerprint as trusted. Call after user approves an unknown host.
    /// </summary>
    public async Task TrustAsync(string host, int port, string fingerprint, string algorithm)
    {
        await _lock.WaitAsync();
        try
        {
            var key = $"{host}:{port}";
            _knownHosts[key] = new KnownHost
            {
                Fingerprint = fingerprint,
                Algorithm = algorithm,
                TrustedAt = DateTime.UtcNow
            };

            await SaveAsync();
            _logger.LogInformation("Trusted host key for {Host}:{Port} ({Algorithm}: {Fingerprint})",
                host, port, algorithm, fingerprint);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes a trusted host key entry (e.g. when user explicitly re-trusts after a key change).
    /// </summary>
    public async Task RemoveAsync(string host, int port)
    {
        await _lock.WaitAsync();
        try
        {
            var key = $"{host}:{port}";
            if (_knownHosts.Remove(key))
            {
                await SaveAsync();
                _logger.LogInformation("Removed host key for {Host}:{Port}", host, port);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                _knownHosts = JsonSerializer.Deserialize<Dictionary<string, KnownHost>>(json)
                    ?? new Dictionary<string, KnownHost>();
                _logger.LogInformation("Loaded {Count} known hosts from {Path}", _knownHosts.Count, _filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load known hosts from {Path}", _filePath);
            _knownHosts = new Dictionary<string, KnownHost>();
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_knownHosts, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            await File.WriteAllTextAsync(_filePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save known hosts to {Path}", _filePath);
        }
    }
}

public enum HostKeyStatus
{
    /// <summary>Host key matches a previously trusted fingerprint.</summary>
    Trusted,

    /// <summary>Host key has never been seen — user should approve (TOFU).</summary>
    Unknown,

    /// <summary>Host key differs from a previously trusted fingerprint — possible MITM.</summary>
    Changed
}

public class KnownHost
{
    public string Fingerprint { get; set; } = string.Empty;
    public string Algorithm { get; set; } = string.Empty;
    public DateTime TrustedAt { get; set; }
}
