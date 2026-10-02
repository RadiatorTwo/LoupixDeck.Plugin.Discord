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
    private readonly List<Func<IEnumerable<string>>> _optionalSources = [];

    /// <summary>Required scopes: the base set plus every source, sorted. A token must cover these.</summary>
    public IReadOnlyCollection<string> Scopes => Collect(_sources, BaseScopes);

    /// <summary>
    /// Scopes that are asked for but may be refused — undocumented ones Discord might not grant to
    /// every app. Without them only the features that declared them are unavailable.
    /// </summary>
    public IReadOnlyCollection<string> OptionalScopes => Collect(_optionalSources, []);

    public void AddSource(Func<IEnumerable<string>> source)
    {
        lock (_gate) _sources.Add(source);
    }

    public void AddOptionalSource(Func<IEnumerable<string>> source)
    {
        lock (_gate) _optionalSources.Add(source);
    }

    private SortedSet<string> Collect(List<Func<IEnumerable<string>>> list, IEnumerable<string> seed)
    {
        List<Func<IEnumerable<string>>> sources;
        lock (_gate) sources = [.. list];

        SortedSet<string> scopes = new(seed, StringComparer.Ordinal);
        foreach (Func<IEnumerable<string>> source in sources)
            scopes.UnionWith(source());
        return scopes;
    }

    /// <summary>True when <paramref name="granted"/> includes every required scope.</summary>
    public bool IsCoveredBy(IEnumerable<string> granted) => Scopes.ToHashSet().IsSubsetOf(granted);
}
