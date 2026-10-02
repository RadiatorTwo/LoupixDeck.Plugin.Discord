using LoupixDeck.Plugin.Discord.Domain;
using LoupixDeck.Plugin.Discord.Features;
using LoupixDeck.Plugin.Discord.Rpc;
using LoupixDeck.Plugin.Discord.Security;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord;

public sealed class DiscordPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor, IPluginRequirements
{
    private const string ClientIdKey = "clientId";
    private const string ClientSecretKey = "clientSecret";
    private const string RedirectUriKey = "redirectUri";
    private const string DebugLogKey = "debugLog";

    /// <summary>Name of the client secret in the <see cref="ISecretStore"/> (never in settings.json).</summary>
    private const string ClientSecretName = "client_secret";

    private const string DefaultRedirectUri = "http://localhost";
    private const string DeveloperPortalUrl = "https://discord.com/developers/applications";

    private readonly ScopeRegistry _scopes = new();
    private readonly List<IDiscordFeature> _features = [];
    private readonly List<IPluginCommand> _commands = [];

    /// <summary>Features and the shared caches they use, disposed on shutdown.</summary>
    private readonly List<IDisposable> _disposables = [];

    private IPluginHost? _host;
    private ISecretStore? _secrets;
    private DiscordSession? _session;
    private string _lastClientId = string.Empty;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "discord",
        Name = "Discord",
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "",
        Description = "Controls the Discord desktop app through its local RPC interface"
    };

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _secrets = SecretStoreFactory.Create(host.Settings, host.Logger);
        MoveClientSecretToSecretStore();

        RpcDebugLog debugLog = new(host.Logger, () => host.Settings.Get<bool>(DebugLogKey));
        _session = new DiscordSession(ReadAppConfig, _secrets, _scopes, host.Logger, debugLog, host.Tr);

        foreach (IDiscordFeature feature in CreateFeatures(_session, host))
        {
            if (feature is IDisposable disposable)
                _disposables.Add(disposable);
            _features.Add(feature);
            _scopes.Add(feature.RequiredScopes);
            _commands.AddRange(feature.Commands);
        }

        _lastClientId = ReadAppConfig().ClientId;
        _session.Start();
    }

    /// <summary>Every feature of the plugin — a new one is one more line here.</summary>
    private IEnumerable<IDiscordFeature> CreateFeatures(DiscordSession session, IPluginHost host)
    {
        // Shared caches: one set of event subscriptions, however many features read them.
        VoiceStateTracker voice = new(session, host.Logger);
        GuildDirectory guilds = new(session, host.Logger);
        _disposables.Add(voice);
        _disposables.Add(guilds);

        return
        [
            new ConnectionFeature(session),
            new VoiceSettingsFeature(session, voice, host),
            new UserVoiceFeature(session, voice, host),
            new VoiceChannelFeature(session, voice, guilds, host),
            new TextChannelFeature(session, guilds),
            new SoundboardFeature(session, voice, host.Logger)
        ];
    }

    public override IEnumerable<IPluginCommand> GetCommands() => _commands;

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new()
        {
            Group = "Discord",
            Description = "Voice, channels and status of the Discord app",
            Icon = "\U000F066F"
        }
    ];

    public override void Shutdown()
    {
        foreach (IDisposable disposable in _disposables)
        {
            try { disposable.Dispose(); }
            catch { /* shutting down */ }
        }

        if (_session != null)
        {
            // Shutdown is synchronous; the session stops within a bounded time.
            try { _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); }
            catch { /* shutting down */ }
        }

        base.Shutdown();
    }

    private DiscordAppConfig ReadAppConfig()
    {
        IPluginSettings? settings = _host?.Settings;
        string clientId = settings?.Get<string>(ClientIdKey)?.Trim() ?? string.Empty;
        string redirectUri = settings?.Get<string>(RedirectUriKey)?.Trim() ?? string.Empty;

        return new DiscordAppConfig(
            clientId,
            _secrets?.Get(ClientSecretName) ?? string.Empty,
            redirectUri.Length > 0 ? redirectUri : DefaultRedirectUri);
    }

    /// <summary>
    /// The settings page writes the client secret into the plain settings.json like any field.
    /// Move it into the secret store right away and leave the field empty.
    /// </summary>
    private void MoveClientSecretToSecretStore()
    {
        if (_host == null || _secrets == null) return;

        string? secret = _host.Settings.Get<string>(ClientSecretKey)?.Trim();
        if (!_host.Settings.Contains(ClientSecretKey)) return;

        // An empty field means "keep the stored secret".
        if (!string.IsNullOrEmpty(secret))
            _secrets.Set(ClientSecretName, secret);

        _host.Settings.Remove(ClientSecretKey);
        _host.Settings.Save();
    }

    private string Tr(string english) => _host?.Tr(english) ?? english;

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema => BuildSchema();

    private List<PluginSettingDescriptor> BuildSchema()
    {
        string status = _session?.State.Describe(Tr) ?? Tr("Starting…");
        bool secretStored = !string.IsNullOrEmpty(_secrets?.Get(ClientSecretName));
        string secretBackend = _secrets?.Description ?? string.Empty;

        return
        [
            new()
            {
                Key = "__heading_status",
                Label = string.Format(Tr("Status: {0}"), status),
                Kind = PluginSettingKind.Heading
            },
            new()
            {
                Key = "__heading_app",
                Label = "Discord application",
                Kind = PluginSettingKind.Heading,
                Description = "Either the application you were added to as a tester, or your own one from the Discord Developer Portal. See the README for step-by-step instructions."
            },
            new()
            {
                Key = ClientIdKey,
                Label = "Client ID",
                Kind = PluginSettingKind.Text,
                Description = "Application ID from the OAuth2 page of the application"
            },
            new()
            {
                Key = ClientSecretKey,
                Label = "Client secret",
                Kind = PluginSettingKind.Password,
                Description = secretStored
                    ? string.Format(Tr("A client secret is stored ({0}). Leave empty to keep it."), secretBackend)
                    : string.Format(Tr("From the OAuth2 page of the application. Stored via {0}, not in settings.json."), secretBackend)
            },
            new()
            {
                Key = RedirectUriKey,
                Label = "Redirect URI",
                Kind = PluginSettingKind.Text,
                DefaultValue = DefaultRedirectUri,
                Description = "Must exactly match a redirect URI registered on the OAuth2 page of the application"
            },
            new()
            {
                Key = "__heading_debug",
                Label = "Diagnostics",
                Kind = PluginSettingKind.Heading
            },
            new()
            {
                Key = DebugLogKey,
                Label = "Log RPC traffic",
                Kind = PluginSettingKind.Toggle,
                DefaultValue = false,
                Description = "Writes every RPC message to the LoupixDeck log with tokens masked — for finding undocumented commands and events"
            }
        ];
    }

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        new()
        {
            Label = "Connect with Discord",
            Invoke = ConnectAsync
        },
        new()
        {
            Label = "Sign out",
            Invoke = SignOutAsync
        },
        new()
        {
            Label = "Open Developer Portal",
            Invoke = () =>
            {
                _host?.OpenBrowser(DeveloperPortalUrl);
                return Task.FromResult(string.Empty);
            }
        }
    ];

    private async Task<string> ConnectAsync()
    {
        if (_session == null) return Tr(RpcErrorMapper.NotConnected);

        try
        {
            // Values typed but not saved yet are not visible here; the secret may sit in the plain field.
            await Task.Run(MoveClientSecretToSecretStore).ConfigureAwait(false);
            if (!ReadAppConfig().IsComplete)
                return Tr(RpcErrorMapper.MissingConfig);

            return await _session.ConnectInteractiveAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _host?.Logger.Error("Connecting to Discord failed", ex);
            return RpcErrorMapper.Describe(ex, RpcErrorContext.Authorize, Tr);
        }
    }

    private async Task<string> SignOutAsync()
    {
        if (_session == null) return string.Empty;

        try
        {
            await _session.SignOutAsync().ConfigureAwait(false);
            return Tr("Signed out — stored tokens deleted");
        }
        catch (Exception ex)
        {
            _host?.Logger.Error("Signing out of Discord failed", ex);
            return Tr("Unexpected error — see the log");
        }
    }

    public void OnSettingsSaved()
    {
        // secret-tool may take a moment (keyring unlock); keep it off the caller's thread.
        _ = Task.Run(() =>
        {
            try
            {
                MoveClientSecretToSecretStore();

                string clientId = ReadAppConfig().ClientId;
                if (clientId == _lastClientId) return;

                _lastClientId = clientId;
                _session?.Restart();
            }
            catch (Exception ex)
            {
                _host?.Logger.Error("Applying Discord settings failed", ex);
            }
        });
    }

    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        List<MenuNode> children = [];
        foreach (IDiscordFeature feature in _features)
        {
            try { children.AddRange(feature.GetMenuNodes(target)); }
            catch (Exception ex) { _host?.Logger.Warn($"Discord menu entries failed: {ex.Message}"); }
        }

        IReadOnlyList<MenuNode> roots = children.Count == 0
            ? []
            : [new MenuNode { Name = "Discord", Children = children }];
        return Task.FromResult(roots);
    }

    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        DiscordConnectionState? state = _session?.State;
        if (state == null) return [];

        // Only states the user can act on; "Discord is not running" is normal, not a problem.
        bool needsAction = state.Status is DiscordConnectionStatus.NotAuthorized or DiscordConnectionStatus.Error;

        return
        [
            new PluginRequirement
            {
                Id = "discord-connection",
                Name = "Discord connection",
                IsMet = !needsAction,
                Message = needsAction ? state.Describe(Tr) : null,
                InstallHint = needsAction
                    ? Tr("Open the Discord plugin settings and press \"Connect with Discord\".")
                    : null
            }
        ];
    }
}
