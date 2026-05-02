using System.Security.Cryptography;
using System.Text;

namespace UiApp.Services;

/// <summary>F-09: Encrypt/decrypt strings using Windows DPAPI (user-scope).</summary>
public static class DpapiHelper
{
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(cipherText); }
        catch { return cipherText; }

        // Основной путь — CurrentUser (после Stage 1 UI работает под user token).
        try
        {
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { /* fall through to LocalMachine */ }

        // Migration path: старые версии могли шифровать под LocalMachine
        // (когда UI запускался под SYSTEM-in-session-N token'ом). Decrypt
        // success здесь → на следующем Save() повторно зашифруется под
        // CurrentUser (EncryptSecrets видит plaintext, IsEncrypted=false).
        try
        {
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return cipherText;
        }
    }

    /// <summary>Check if a string looks like DPAPI-encrypted data (valid base64, not plaintext).</summary>
    public static bool IsEncrypted(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length > 16; // DPAPI output is always > 16 bytes
        }
        catch
        {
            return false;
        }
    }
}
