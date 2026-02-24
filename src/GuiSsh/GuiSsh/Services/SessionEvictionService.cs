namespace GuiSsh.Services;

/// <summary>
/// Background service that periodically evicts idle SSH sessions.
/// </summary>
public class SessionEvictionService : BackgroundService
{
    private readonly SshSessionManager _manager;
    private readonly ILogger<SessionEvictionService> _logger;
    private readonly TimeSpan _maxIdle = TimeSpan.FromMinutes(10);
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public SessionEvictionService(SshSessionManager manager, ILogger<SessionEvictionService> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session eviction service started (idle timeout: {Minutes} min)", _maxIdle.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_checkInterval, stoppingToken);
            _manager.EvictExpired(_maxIdle);
        }
    }
}
