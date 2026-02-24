using System.Net.Http.Json;
using GuiSsh.Client.Models;

namespace GuiSsh.Client.Services;

/// <summary>
/// WASM-side ISshService implementation that calls the server's HTTP API endpoints.
/// Used when components are running in WebAssembly render mode.
/// </summary>
public class ClientSshService : ISshService
{
    private readonly HttpClient _http;

    public ClientSshService(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> ConnectAsync(string host, int port, string username, string password)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/connect", new
        {
            Host = host,
            Port = port,
            Username = username,
            Password = password
        });

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ConnectResponse>();
        return result?.SessionId ?? throw new InvalidOperationException("No session ID returned.");
    }

    public async Task<string> ConnectWithKeyAsync(string host, int port, string username, string privateKey, string? passphrase = null)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/connect", new
        {
            Host = host,
            Port = port,
            Username = username,
            Password = string.Empty,
            PrivateKey = privateKey,
            Passphrase = passphrase
        });

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ConnectResponse>();
        return result?.SessionId ?? throw new InvalidOperationException("No session ID returned.");
    }

    public async Task DisconnectAsync(string sessionId)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/disconnect", new { SessionId = sessionId });
        response.EnsureSuccessStatusCode();
    }

    public async Task<CommandResult> ExecuteAsync(string sessionId, string command)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/exec", new
        {
            SessionId = sessionId,
            Command = command
        });

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<CommandResult>()
               ?? new CommandResult { ExitCode = -1, StdErr = "Failed to deserialize response." };
    }

    public async Task<bool> IsConnectedAsync(string sessionId)
    {
        try
        {
            var response = await _http.GetAsync($"/api/ssh/status/{sessionId}");
            if (!response.IsSuccessStatusCode)
                return false;

            var result = await response.Content.ReadFromJsonAsync<StatusResponse>();
            return result?.Connected ?? false;
        }
        catch
        {
            return false;
        }
    }

    private record ConnectResponse(string SessionId);
    private record StatusResponse(bool Connected);

    public async Task<(byte[] Data, string FileName)> DownloadFileAsync(string sessionId, string remotePath)
    {
        var response = await _http.PostAsJsonAsync("/api/ssh/download", new
        {
            SessionId = sessionId,
            RemotePath = remotePath
        });

        response.EnsureSuccessStatusCode();

        var data = await response.Content.ReadAsByteArrayAsync();
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? remotePath.Split('/').Last();

        return (data, fileName);
    }

    public async Task UploadFileAsync(string sessionId, string remotePath, Stream fileStream, string fileName)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(sessionId), "sessionId");
        content.Add(new StringContent(remotePath), "remotePath");
        content.Add(new StreamContent(fileStream), "file", fileName);

        var response = await _http.PostAsync("/api/ssh/upload", content);
        response.EnsureSuccessStatusCode();
    }
}
