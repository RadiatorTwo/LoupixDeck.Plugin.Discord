namespace LoupixDeck.Plugin.Discord.Security;

/// <summary>
/// Storage for the client secret and the OAuth tokens. The SDK's <c>IPluginSettings</c> is a plain
/// JSON file, so secrets go through this instead — backed by whatever the platform offers.
/// </summary>
internal interface ISecretStore
{
    /// <summary>Short English description of the backend, shown in the settings ("Windows DPAPI", …).</summary>
    string Description { get; }

    string? Get(string name);

    /// <summary>Stores <paramref name="value"/>; <c>null</c> or empty deletes the entry.</summary>
    void Set(string name, string? value);
}
