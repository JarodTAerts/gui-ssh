using Renci.SshNet;

namespace GuiSsh.Services;

/// <summary>
/// Creates SSH.NET client instances for connecting to remote servers.
/// </summary>
public class SshConnectionFactory
{
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

    public SshClient CreateClientWithKey(string host, int port, string username, string privateKeyPem, string? passphrase = null)
    {
        using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKeyPem));
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

    public ShellStream CreateShellStream(SshClient client, int columns = 120, int rows = 30)
    {
        return client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 1024);
    }
}
