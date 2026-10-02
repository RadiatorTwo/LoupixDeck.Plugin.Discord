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

    private readonly SortedSet<string> _scopes = new(BaseScopes, StringComparer.Ordinal);

    public IReadOnlyCollection<string> Scopes => _scopes;

    public void Add(IEnumerable<string> scopes)
    {
        foreach (string scope in scopes)
            _scopes.Add(scope);
    }

    /// <summary>True when <paramref name="granted"/> includes every required scope.</summary>
    public bool IsCoveredBy(IEnumerable<string> granted) => _scopes.IsSubsetOf(granted);
}
