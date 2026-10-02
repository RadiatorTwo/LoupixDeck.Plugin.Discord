# LoupixDeck.Plugin.Discord

Discord integration plugin for [LoupixDeck](https://github.com/RadiatorTwo/LoupixDeck),
built against [LoupixDeck.PluginSdk](https://github.com/RadiatorTwo/LoupixDeck.PluginSdk).

The plugin talks to the Discord desktop app on the same machine through Discord's local
[RPC interface](https://docs.discord.com/developers/topics/rpc) over IPC (a named pipe on Windows,
a Unix socket on Linux). It does not use or read your Discord login token — access is granted by
you through Discord's own authorization popup.

## Setup

Discord only lets an application use RPC if Discord approved it, or if your Discord account is on
the application's **tester list** (up to 50 people). Pick one of the two ways below.

### Way A — be added as a tester of an existing application

1. Send the owner of the application your Discord username and ask to be added as an
   **App Tester** (Developer Portal → the application → **App Testers**).
2. Accept the tester invitation if Discord sends you one.
3. Ask the owner for the **Client ID**, the **Client Secret** and the **Redirect URI** of the
   application.
4. Continue with [Connect](#connect).

### Way B — create your own application

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) (or press
   **Open Developer Portal** in the plugin settings) and click **New Application**. Any name works.
2. Open **OAuth2** in the left menu:
   - Copy the **Client ID**.
   - Click **Reset Secret** and copy the **Client Secret** (it is shown only once).
   - Under **Redirects**, click **Add Redirect**, enter `http://localhost` and save.
     Nothing has to run at that address — Discord only checks that the value matches.
3. As the owner you normally have RPC access to your own application. If the authorization
   still fails with *not on the tester list*, add your own account under **App Testers**.
4. Continue with [Connect](#connect).

### Connect

1. In LoupixDeck open **Plugins → Discord → Settings**.
2. Enter **Client ID**, **Client secret** and **Redirect URI** (the same value as in the
   Developer Portal, default `http://localhost`) and save.
3. Make sure the Discord desktop app is running, then press **Connect with Discord**.
4. Discord shows an authorization popup — confirm it.
5. The status line now reads *Connected as …*. The popup is not shown again: the plugin keeps the
   token and refreshes it on its own. It only asks again when you sign out, when the token was
   revoked, or when a plugin update needs additional permissions.

### Where the credentials are stored

The client secret and the OAuth tokens never go into the plain `settings.json`:

| Platform | Storage |
|---|---|
| Windows | encrypted with DPAPI for your Windows account |
| Linux | the desktop keyring via `secret-tool` (libsecret); without it a file readable only by you (`~/.config/LoupixDeck/plugin-secrets/discord.json`) |

**Sign out** in the settings revokes the token at Discord and deletes it locally.

### Troubleshooting

| Message | Cause |
|---|---|
| *Discord is not running* | No Discord desktop app found. The plugin reconnects on its own once Discord starts. |
| *Authorization failed — popup declined, or …not on the application's tester list* | You declined the popup, or (way A) your account is not a tester of the application yet. |
| *Client ID or client secret is wrong* | Re-copy both from the OAuth2 page. A reset secret invalidates the old one. |
| *Redirect URI does not match …* | The value in the plugin must be identical to one under **OAuth2 → Redirects**. |
| *Voice settings are locked by another app* | Only one app may change Discord's voice settings over RPC at a time (e.g. the Elgato Stream Deck plugin). Close or disconnect it; Discord releases the lock when that app disconnects. |
| *Not in a voice channel* | The action needs you to be in a voice channel. |

Unapproved applications are limited by Discord (tester list, 10 servers and 10 channels, see the
RPC docs, "Restrictions").

## Commands

| Command | Parameters | What it does |
|---|---|---|
| `Discord.ConnectionStatus` | – | Shows the connection state; press to reconnect. |
| `Discord.Mute` | `mode` (`Toggle`/`On`/`Off`) | Microphone mute. The button follows changes made in Discord. |
| `Discord.Deafen` | `mode` | Deafen. The button follows changes made in Discord. |
| `Discord.InputVolume` | `step` (default 5) | Dial: microphone volume 0–100, press to mute. |
| `Discord.OutputVolume` | `step` (default 5) | Dial: output volume 0–200, press to deafen. |
| `Discord.UserVolume` | `userId`, `step` (default 10) | Dial: how loud you hear a user (0–200 %), press to mute them for you. |
| `Discord.UserMute` | `userId` | Mute a user for you only. |
| `Discord.JoinVoiceChannel` | `channelId`, `force` | Join a voice channel. Without `force`, Discord refuses while you are in another channel. |
| `Discord.LeaveVoiceChannel` | – | Leave the voice channel. |
| `Discord.CurrentVoiceChannel` | – | Shows your voice channel and how many people are in it. |
| `Discord.Speaking` | – | Shows who is speaking in your voice channel. |
| `Discord.OpenTextChannel` | `channelId` | Switch the Discord app to a text channel. |
| `Discord.PlaySoundboardSound` | `soundId`, `guildId` | Play a soundboard sound in your voice channel (undocumented RPC, see below). |

Servers, channels and users are picked in the command menu under **Discord ▸** (*Join voice
channel*, *Open text channel*, *Users in voice channel*, *Soundboard*). The lists come from Discord and need an
active connection.

## Settings

| Setting | Meaning |
|---|---|
| Client ID / Client secret / Redirect URI | The Discord application, see [Setup](#setup). |
| Log RPC traffic | Writes every RPC message in both directions to the LoupixDeck log. Tokens, the OAuth code and the client secret are masked. Meant for finding undocumented commands and events. |
| RPC tester: command / arguments / event | Sends any RPC command with JSON arguments (and, for `SUBSCRIBE`, an event name) on the authenticated connection. Save the fields, then press **Send RPC command**; the reply is shown below the button and written to the log. `ERROR 4002` means the command does not exist. |

## Development

### Architecture

| Layer | Folder | Responsibility |
|---|---|---|
| Transport | `Transport/` | Finds `discord-ipc-0…9`, reads and writes frames (`opcode` + `length`, both uint32 little endian, + JSON). |
| RPC client | `Rpc/` | Handshake, READY, PING/PONG, reconnect with backoff, commands matched by nonce, timeouts, ERROR replies, SUBSCRIBE/UNSUBSCRIBE with automatic re-subscribe, OAuth (AUTHORIZE → token → AUTHENTICATE, refresh), scopes, debug log. |
| Secrets | `Security/` | DPAPI / libsecret / owner-only file. |
| Domain | `Domain/` | Shared caches the features read: voice state (`VoiceStateTracker`) and servers/channels (`GuildDirectory`). |
| Features | `Features/` | The commands. They use only `IDiscordRpc` (`CommandAsync`, `Subscribe`). |

### Adding a feature

1. Create a class implementing `IDiscordFeature` in `Features/`:
   - `RequiredScopes` — extra OAuth2 scopes (the base set is `rpc`, `identify`).
   - `Commands` — the `IPluginCommand`s. `CommandName`s are public API: never rename them.
   - optionally `GetMenuNodes` — pickers built from cached data.
2. Send commands with `rpc.CommandAsync("CMD", new JsonObject { … })`, subscribe to events with
   `rpc.Subscribe("EVENT", args, handler)` in the constructor — subscriptions survive reconnects.
3. Report failures with `CommandFeedback.ShowError(ctx, name, ex, context)`; `RpcErrorMapper`
   turns RPC codes into readable text.
4. Add one line to `DiscordPlugin.CreateFeatures`.
5. Add the new visible strings to `strings.de.json`.

If the feature needs a new scope, existing users see *New permissions needed* and connect once more.

### Undocumented features

Discord's RPC docs do not cover the soundboard. The plugin uses the commands the official Elgato
Stream Deck Discord plugin (2.4.0) uses over the same IPC interface:

| Command | Arguments | Used for |
|---|---|---|
| `GET_SOUNDBOARD_SOUNDS` | – | All sounds you can play (built-in and from your servers). Falls back to the documented `GET /soundboard-default-sounds` if it fails. |
| `PLAY_SOUNDBOARD_SOUND` | `sound_id`, `guild_id` (omitted for built-in sounds) | Playing a sound. |

Both were verified against the Discord client (October 2026). Being undocumented, they may change
without notice; the RPC tester in the settings helps re-check them.

Still open, marked `TODO(...)`:

- `TODO(voice-lock)` in `Rpc/RpcErrorMapper.cs` — the error Discord returns when another app
  holds the voice settings lock.
- `TODO(rpc-errors)` in `Rpc/RpcErrorMapper.cs` — the errors for a declined popup and for an
  account that is not on the tester list.

## Build & deploy

```bash
dotnet build -c Release
```

Copy the contents of `bin\Release\` (the plugin DLL, `System.Security.Cryptography.ProtectedData.dll`,
`strings.*.json`) together with `plugin.json` to `<LoupixDeck>\plugins\discord\`.
