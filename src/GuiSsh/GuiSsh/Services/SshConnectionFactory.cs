using Renci.SshNet;

namespace GuiSsh.Services;

/// <summary>
/// Creates SSH.NET client instances for connecting to remote servers.
/// When a HostKeyStore is provided, host key verification is performed.
/// </summary>
public class SshConnectionFactory
{
    /// <summary>
    /// Result of creating a client with host key verification.
    /// Properties are populated after client.Connect() triggers the HostKeyReceived event.
    /// </summary>
    public class HostKeyVerificationResult
    {
        public required SshClient Client { get; init; }
        public HostKeyStatus Status { get; set; } = HostKeyStatus.Unknown;
        public string? Fingerprint { get; set; }
        public string? Algorithm { get; set; }
    }

    public SshClient CreateClient(string host, int port, string username, string password)
    {
        var connectionInfo = new Renci.SshNet.ConnectionInfo(host, port, username,
            new PasswordAuthenticationMethod(username, password))
        {
            Timeout = TimeSpan.FromSeconds(15),
            ChannelCloseTimeout = TimeSpan.FromSeconds(5)
        };

        return new SshClient(connectionInfo);
    }

    /// <summary>
    /// Creates a password-based client with host key verification via the HostKeyStore.
    /// The returned result includes the host key status and fingerprint for TOFU handling.
    /// </summary>
    public HostKeyVerificationResult CreateClientWithHostKeyCheck(
        string host, int port, string username, string password, HostKeyStore hostKeyStore)
    {
        var connectionInfo = new Renci.SshNet.ConnectionInfo(host, port, username,
            new PasswordAuthenticationMethod(username, password))
        {
            Timeout = TimeSpan.FromSeconds(15),
            ChannelCloseTimeout = TimeSpan.FromSeconds(5)
        };

        return CreateClientWithVerification(host, port, connectionInfo, hostKeyStore);
    }

    public SshClient CreateClientWithKey(string host, int port, string username, string privateKeyPem, string? passphrase = null)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(privateKeyPem);
        try
        {
            using var keyStream = new MemoryStream(keyBytes);
            var keyFile = passphrase != null
                ? new PrivateKeyFile(keyStream, passphrase)
                : new PrivateKeyFile(keyStream);

            var connectionInfo = new Renci.SshNet.ConnectionInfo(host, port, username,
                new PrivateKeyAuthenticationMethod(username, keyFile))
            {
                Timeout = TimeSpan.FromSeconds(15),
                ChannelCloseTimeout = TimeSpan.FromSeconds(5)
            };

            return new SshClient(connectionInfo);
        }
        finally
        {
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }
    }

    /// <summary>
    /// Creates a key-based client with host key verification via the HostKeyStore.
    /// </summary>
    public HostKeyVerificationResult CreateClientWithKeyAndHostKeyCheck(
        string host, int port, string username, string privateKeyPem, string? passphrase, HostKeyStore hostKeyStore)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(privateKeyPem);
        try
        {
            using var keyStream = new MemoryStream(keyBytes);
            var keyFile = passphrase != null
                ? new PrivateKeyFile(keyStream, passphrase)
                : new PrivateKeyFile(keyStream);

            var connectionInfo = new Renci.SshNet.ConnectionInfo(host, port, username,
                new PrivateKeyAuthenticationMethod(username, keyFile))
            {
                Timeout = TimeSpan.FromSeconds(15),
                ChannelCloseTimeout = TimeSpan.FromSeconds(5)
            };

            return CreateClientWithVerification(host, port, connectionInfo, hostKeyStore);
        }
        finally
        {
            Array.Clear(keyBytes, 0, keyBytes.Length);
        }
    }

    private HostKeyVerificationResult CreateClientWithVerification(
        string host, int port, Renci.SshNet.ConnectionInfo connectionInfo, HostKeyStore hostKeyStore)
    {
        var client = new SshClient(connectionInfo);
        var result = new HostKeyVerificationResult { Client = client };

        client.HostKeyReceived += (sender, e) =>
        {
            result.Fingerprint = $"SHA256:{e.FingerPrintSHA256}";
            result.Algorithm = e.HostKeyName;
            result.Status = hostKeyStore.Check(host, port, result.Fingerprint);

            switch (result.Status)
            {
                case HostKeyStatus.Trusted:
                    e.CanTrust = true;
                    break;
                case HostKeyStatus.Changed:
                    e.CanTrust = false; // Reject — possible MITM
                    break;
                case HostKeyStatus.Unknown:
                    // For TOFU: we accept the key to allow fingerprint capture,
                    // but the caller must check the status and get user approval
                    // before actually using the connection.
                    e.CanTrust = true;
                    break;
            }
        };

        return result;
    }

    public ShellStream CreateShellStream(SshClient client, int columns = 120, int rows = 30)
    {
        return client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 1024);
    }
}
