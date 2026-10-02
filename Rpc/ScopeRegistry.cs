namespace LoupixDeck.Plugin.Discord.Rpc;

/// <summary>
/// The single place that decides which OAuth2 scopes AUTHORIZE asks for: a base set plus whatever
/// the registered features declare. When the set grows (a new feature), the stored token no longer
/// covers it and the user is asked to connect again.
/// </summary>
/// <remarks>
/// The RPC scopes (<c>rpc</c>, <c>rpc.voice.read</c>, <c>rpc.voice.write</c>, …) are only granted to
/// approved applications or to users on the application's tester list (OAuth2 docs, "OAuth2 Scopes").
/// </remarks>
internal sealed class ScopeRegistry
{
    /// <summary>Needed for any RPC access at all, plus the user's identity for the status line.</summary>
    private static readonly string[] BaseScopes = ["rpc", "identify"];

    private readonly Lock _gate = new();
    private readonly List<Func<IEnumerable<string>>> _sources = [];

    /// <summary>The current union of the base scopes and every source, sorted.</summary>
    public IReadOnlyCollection<string> Scopes
    {
        get
        {
            List<Func<IEnumerable<string>>> sources;
            lock (_gate) sources = [.. _sources];

            SortedSet<string> scopes = new(BaseScopes, StringComparer.Ordinal);
            foreach (Func<IEnumerable<string>> source in sources)
                scopes.UnionWith(source());
            return scopes;
        }
    }

    /// <summary>
    /// Adds a provider that is asked on every check, so a feature whose needs depend on a
    /// setting (opt-in scopes) is covered without restarting the plugin.
    /// </summary>
    public void AddSource(Func<IEnumerable<string>> source)
    {
        lock (_gate) _sources.Add(source);
    }

    /// <summary>True when <paramref name="granted"/> includes every required scope.</summary>
    public bool IsCoveredBy(IEnumerable<string> granted) => Scopes.ToHashSet().IsSubsetOf(granted);
}
