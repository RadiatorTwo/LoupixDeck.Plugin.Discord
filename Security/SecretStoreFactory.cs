using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Security;

internal static class SecretStoreFactory
{
    public static ISecretStore Create(IPluginSettings settings, IPluginLogger logger)
    {
        if (OperatingSystem.IsWindows())
            return new DpapiSecretStore(settings, logger);

        // Outside the plugin folder (~/.config/LoupixDeck/…), so a plugin update that replaces the
        // folder does not throw the credentials away.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LoupixDeck", "plugin-secrets", "discord.json");
        return new LibSecretStore(new FileSecretStore(path), logger);
    }
}
