namespace GuiSsh.Client.Models;

/// <summary>
/// Result of an SSH connection attempt. May require host key approval
/// before the connection is finalized (Trust-on-First-Use).
/// </summary>
public class ConnectResult
{
    /// <summary>
    /// The session ID if the connection succeeded.
    /// Null if host key approval is required or the connection failed.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// True if the server's host key is unknown and needs user approval.
    /// </summary>
    public bool NeedsHostKeyApproval { get; set; }

    /// <summary>
    /// True if the server's host key has changed from a previously trusted value.
    /// The connection should be aborted — possible MITM attack.
    /// </summary>
    public bool HostKeyChanged { get; set; }

    /// <summary>
    /// The host key fingerprint (e.g. "SHA256:abc123...") when approval is needed.
    /// </summary>
    public string? HostKeyFingerprint { get; set; }

    /// <summary>
    /// The host key algorithm (e.g. "ssh-ed25519", "ssh-rsa") when approval is needed.
    /// </summary>
    public string? HostKeyAlgorithm { get; set; }

    /// <summary>
    /// True if the connection completed successfully.
    /// </summary>
    public bool IsConnected => !string.IsNullOrEmpty(SessionId);

    /// <summary>
    /// Error message if the connection failed (not related to host key).
    /// </summary>
    public string? Error { get; set; }
}
