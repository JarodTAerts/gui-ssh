using Microsoft.JSInterop;

namespace GuiSsh.Client.Services;

/// <summary>
/// Encrypts and decrypts credentials using the Web Crypto API via JS interop.
/// All cryptographic operations execute in the browser — nothing is encrypted or
/// decrypted on the server. Credentials are stored in browser localStorage and
/// only the plaintext password is sent to the server at SSH connection time.
/// Uses AES-GCM with PBKDF2 key derivation (100k iterations, SHA-256).
/// </summary>
public class CryptoService
{
    private readonly IJSRuntime _js;

    public CryptoService(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>
    /// Encrypts credentials (username + password) for a specific connection.
    /// The connectionId and browser user-agent are used as part of the key derivation,
    /// binding the encrypted data to both the connection and the browser.
    /// Returns a base64-encoded encrypted blob.
    /// </summary>
    public async Task<string> EncryptCredentialAsync(string connectionId, string username, string password)
    {
        return await _js.InvokeAsync<string>("cryptoInterop.encryptCredential", connectionId, username, password);
    }

    /// <summary>
    /// Decrypts credentials previously encrypted with EncryptCredentialAsync().
    /// Returns (username, password) or null if decryption fails (e.g. different browser/device).
    /// </summary>
    public async Task<(string Username, string Password)?> DecryptCredentialAsync(string connectionId, string encryptedData)
    {
        var result = await _js.InvokeAsync<CredentialResult?>("cryptoInterop.decryptCredential", connectionId, encryptedData);
        if (result is null)
            return null;
        return (result.Username, result.Password);
    }

    private class CredentialResult
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}
