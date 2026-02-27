using System.Net;
using System.Net.Sockets;

namespace GuiSsh.Services;

/// <summary>
/// Evaluates whether an SSH connection target (host + port) is allowed
/// based on configurable policy rules. Prevents SSRF by blocking connections
/// to private/internal networks and restricting port ranges.
/// </summary>
public class ConnectionPolicy
{
    private readonly HashSet<string> _allowedHosts;
    private readonly bool _blockPrivateRanges;
    private readonly int _minPort;
    private readonly int _maxPort;
    private readonly ILogger<ConnectionPolicy> _logger;

    public ConnectionPolicy(IConfiguration config, ILogger<ConnectionPolicy> logger)
    {
        _logger = logger;

        var section = config.GetSection("ConnectionPolicy");
        _blockPrivateRanges = section.GetValue("BlockPrivateRanges", true);
        _minPort = section.GetValue("MinPort", 1);
        _maxPort = section.GetValue("MaxPort", 65535);

        var allowed = section.GetSection("AllowedHosts").Get<string[]>();
        _allowedHosts = allowed != null
            ? new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation(
            "ConnectionPolicy: BlockPrivateRanges={Block}, Ports={Min}-{Max}, AllowedHosts={Count}",
            _blockPrivateRanges, _minPort, _maxPort,
            _allowedHosts.Count == 0 ? "any" : _allowedHosts.Count);
    }

    /// <summary>
    /// Evaluates the connection target. Resolves DNS to check the actual IP
    /// against the policy (prevents DNS rebinding to private ranges).
    /// </summary>
    public async Task<(bool Allowed, string? Reason)> EvaluateAsync(string host, int port)
    {
        // Port range check
        if (port < _minPort || port > _maxPort)
            return (false, $"Port {port} is outside the allowed range ({_minPort}–{_maxPort}).");

        // Host allowlist (when configured, only listed hosts are permitted)
        if (_allowedHosts.Count > 0 && !_allowedHosts.Contains(host))
            return (false, $"Host '{host}' is not in the allowed hosts list.");

        // Resolve DNS and check resolved IPs against private range blocklist
        if (_blockPrivateRanges)
        {
            try
            {
                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var directIp))
                {
                    addresses = [directIp];
                }
                else
                {
                    addresses = await Dns.GetHostAddressesAsync(host);
                }

                if (addresses.Length == 0)
                    return (false, $"Could not resolve host '{host}'.");

                foreach (var ip in addresses)
                {
                    if (IsPrivateOrReserved(ip))
                    {
                        _logger.LogWarning(
                            "ConnectionPolicy blocked {Host}:{Port} — resolved to private/reserved IP {IP}",
                            host, port, ip);
                        return (false, "Connections to private or internal network addresses are not allowed.");
                    }
                }
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "DNS resolution failed for {Host}", host);
                return (false, $"Could not resolve host '{host}'.");
            }
        }

        return (true, null);
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        var bytes = ip.GetAddressBytes();

        if (bytes.Length == 4) // IPv4
        {
            return bytes[0] == 10                                           // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)    // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)                     // 192.168.0.0/16
                || bytes[0] == 127                                          // 127.0.0.0/8
                || (bytes[0] == 169 && bytes[1] == 254)                     // 169.254.0.0/16 (link-local)
                || bytes[0] == 0                                            // 0.0.0.0/8
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)   // 100.64.0.0/10 (CGN)
                || (bytes[0] == 198 && bytes[1] >= 18 && bytes[1] <= 19);   // 198.18.0.0/15 (benchmarking)
        }

        if (bytes.Length == 16) // IPv6
        {
            // fe80::/10 (link-local)
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
                return true;

            // fc00::/7 (unique local)
            if ((bytes[0] & 0xfe) == 0xfc)
                return true;

            // ::1 (loopback) — already caught by IPAddress.IsLoopback
            // :: (unspecified)
            if (ip.Equals(IPAddress.IPv6None))
                return true;

            // ::ffff:0:0/96 (IPv4-mapped) — check the embedded IPv4
            if (ip.IsIPv4MappedToIPv6)
            {
                var mapped = ip.MapToIPv4();
                return IsPrivateOrReserved(mapped);
            }
        }

        return false;
    }
}
