using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Security;

/// <summary>
/// Windows: values encrypted with DPAPI for the current user and kept in the plugin settings.
/// The ciphertext in <c>settings.json</c> is useless to another Windows account or machine.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiSecretStore(IPluginSettings settings, IPluginLogger logger) : ISecretStore
{
    private const string KeyPrefix = "secret:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LoupixDeck.Plugin.Discord.v1");

    public string Description => "Windows DPAPI";

    public string? Get(string name)
    {
        string? blob = settings.Get<string>(KeyPrefix + name);
        if (string.IsNullOrEmpty(blob)) return null;

        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Copied from another account or machine — treat as absent, the user reconnects once.
            logger.Warn($"Stored Discord secret '{name}' could not be decrypted and is ignored.");
            return null;
        }
    }

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            settings.Remove(KeyPrefix + name);
        }
        else
        {
            byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            settings.Set(KeyPrefix + name, Convert.ToBase64String(cipher));
        }

        settings.Save();
    }
}
