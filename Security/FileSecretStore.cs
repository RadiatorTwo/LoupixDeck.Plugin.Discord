using System.Text.Json;

namespace LoupixDeck.Plugin.Discord.Security;

/// <summary>
/// Last-resort store: a JSON file readable and writable only by the current user (mode 600).
/// Not encrypted — used on Linux when no Secret Service (libsecret) is available.
/// </summary>
internal sealed class FileSecretStore(string path) : ISecretStore
{
    private readonly Lock _gate = new();

    public string Description => "file with owner-only permissions";

    public string? Get(string name)
    {
        lock (_gate)
            return Load().GetValueOrDefault(name);
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            Dictionary<string, string> values = Load();
            if (string.IsNullOrEmpty(value))
                values.Remove(name);
            else
                values[name] = value;

            Write(values);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(Dictionary<string, string> values)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Create the file with owner-only permissions before the secrets go in.
        if (!File.Exists(path))
        {
            using (File.Create(path)) { }
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.WriteAllText(path, JsonSerializer.Serialize(values));
    }
}
