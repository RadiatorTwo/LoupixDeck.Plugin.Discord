using System.Diagnostics;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Discord.Security;

/// <summary>
/// Linux: the desktop keyring through libsecret's <c>secret-tool</c> (GNOME Keyring, KWallet via
/// the Secret Service API). Falls back to <see cref="FileSecretStore"/> when <c>secret-tool</c> is
/// missing or the keyring refuses the request, and keeps using the fallback from then on.
/// </summary>
internal sealed class LibSecretStore(FileSecretStore fallback, IPluginLogger logger) : ISecretStore
{
    private const string Tool = "secret-tool";
    private const string Application = "loupixdeck-discord";
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(20);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string?> _cache = [];
    private bool _useFallback = !IsToolAvailable();

    public string Description => _useFallback ? fallback.Description : "Secret Service (libsecret)";

    public string? Get(string name)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(name, out string? cached))
                return cached;

            string? value = null;
            if (!_useFallback)
            {
                // Exit code 1 with no output simply means "not stored".
                (int exitCode, string output) = Run(["lookup", "application", Application, "name", name], null);
                if (exitCode == 0)
                    value = output.TrimEnd('\r', '\n');
                else if (exitCode < 0)
                    SwitchToFallback("lookup failed");
            }

            // A value stored while the keyring was unavailable lives in the fallback file.
            value ??= fallback.Get(name);
            _cache[name] = value;
            return value;
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            _cache[name] = string.IsNullOrEmpty(value) ? null : value;

            if (!_useFallback)
            {
                (int exitCode, _) = string.IsNullOrEmpty(value)
                    ? Run(["clear", "application", Application, "name", name], null)
                    : Run(["store", $"--label=LoupixDeck Discord ({name})", "application", Application, "name", name], value);

                // "clear" exits non-zero when nothing was stored; that is not an error.
                if (exitCode == 0 || (exitCode > 0 && string.IsNullOrEmpty(value)))
                {
                    fallback.Set(name, null);
                    return;
                }

                SwitchToFallback("store failed");
            }

            fallback.Set(name, value);
        }
    }

    private void SwitchToFallback(string reason)
    {
        _useFallback = true;
        logger.Warn($"Secret Service unavailable ({reason}); Discord credentials are stored in an owner-only file instead.");
    }

    /// <summary>Runs secret-tool; returns -1 when it could not run or timed out. Output is never logged.</summary>
    private static (int ExitCode, string Output) Run(IReadOnlyList<string> args, string? stdin)
    {
        try
        {
            ProcessStartInfo info = new(Tool)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string arg in args)
                info.ArgumentList.Add(arg);

            using Process process = Process.Start(info) ?? throw new InvalidOperationException();
            if (stdin != null)
                process.StandardInput.Write(stdin);
            process.StandardInput.Close();

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            // A locked keyring may show an unlock prompt; give the user time, but never hang forever.
            if (!process.WaitForExit(ToolTimeout))
            {
                try { process.Kill(); }
                catch { /* already gone */ }
                return (-1, string.Empty);
            }

            return (process.ExitCode, output.Result);
        }
        catch
        {
            return (-1, string.Empty);
        }
    }

    private static bool IsToolAvailable()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, Tool)));
    }
}
