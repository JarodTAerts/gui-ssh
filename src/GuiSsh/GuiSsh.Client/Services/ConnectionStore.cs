using System.Text.Json;
using GuiSsh.Client.Models;
using Microsoft.JSInterop;

namespace GuiSsh.Client.Services;

/// <summary>
/// Manages saved SSH connections in browser localStorage/IndexedDB via JS interop.
/// </summary>
public class ConnectionStore
{
    private readonly IJSRuntime _js;
    private const string StorageKey = "guissh_connections";

    public ConnectionStore(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<List<SavedConnection>> GetAllAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json))
                return new List<SavedConnection>();

            return JsonSerializer.Deserialize<List<SavedConnection>>(json) ?? new List<SavedConnection>();
        }
        catch
        {
            return new List<SavedConnection>();
        }
    }

    public async Task SaveAllAsync(List<SavedConnection> connections)
    {
        var json = JsonSerializer.Serialize(connections);
        await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
    }

    public async Task AddAsync(SavedConnection connection)
    {
        var all = await GetAllAsync();
        connection.SortOrder = all.Count;
        all.Add(connection);
        await SaveAllAsync(all);
    }

    public async Task UpdateAsync(SavedConnection connection)
    {
        var all = await GetAllAsync();
        var idx = all.FindIndex(c => c.Id == connection.Id);
        if (idx >= 0)
        {
            all[idx] = connection;
            await SaveAllAsync(all);
        }
    }

    public async Task DeleteAsync(string connectionId)
    {
        var all = await GetAllAsync();
        all.RemoveAll(c => c.Id == connectionId);
        await SaveAllAsync(all);
    }

    /// <summary>
    /// Stores encrypted credential data in localStorage (keyed by connection ID).
    /// </summary>
    public async Task SaveCredentialAsync(string connectionId, string encryptedCredential)
    {
        await _js.InvokeVoidAsync("localStorage.setItem", $"guissh_cred_{connectionId}", encryptedCredential);
    }

    /// <summary>
    /// Retrieves encrypted credential data for a connection.
    /// </summary>
    public async Task<string?> GetCredentialAsync(string connectionId)
    {
        return await _js.InvokeAsync<string?>("localStorage.getItem", $"guissh_cred_{connectionId}");
    }

    public async Task DeleteCredentialAsync(string connectionId)
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", $"guissh_cred_{connectionId}");
    }
}
