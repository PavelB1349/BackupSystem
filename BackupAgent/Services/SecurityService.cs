using System.Security.Cryptography;
using System.Text;

namespace BackupAgent.Services;

public static class SecurityService
{
    public static string EncryptSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    public static string DecryptSecret(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return "";
        byte[] decrypted = ProtectedData.Unprotect(Convert.FromBase64String(encryptedBase64), null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(decrypted);
    }
}