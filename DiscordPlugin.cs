using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord;

public sealed class DiscordPlugin : LoupixPlugin
{
    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "discord",
        Name = "Discord",
        Version = new Version(1, 0, 0),
        SdkVersion = new Version(1, 28, 0),
        Author = "",
        Description = ""
    };

    public override void Initialize(IPluginHost host)
    {
    }

    public override IEnumerable<IPluginCommand> GetCommands() => [];
}
