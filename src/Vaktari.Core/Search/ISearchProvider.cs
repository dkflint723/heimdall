using Vaktari.Core.FileSystem;

namespace Vaktari.Core.Search;

public sealed record SearchQuery
{
    public required string Text { get; init; }

    /// <summary>Null searches every indexed location.</summary>
    public string? ScopePath { get; init; }

    public bool MatchContent { get; init; }
    public bool CaseSensitive { get; init; }
    public bool Regex { get; init; }
    public int MaxResults { get; init; } = 1000;
}

/// <summary>
/// Name and content search. Backed by an index someone else already maintains —
/// Everything on Windows, Baloo on Fedora KDE. Writing our own indexer is a
/// last resort, not a starting point.
/// </summary>
public interface ISearchProvider
{
    /// <summary>
    /// False when the backing index is absent or disabled (Everything not
    /// running, Baloo switched off). The UI degrades to a slow recursive walk
    /// with a visible warning rather than silently returning nothing.
    /// </summary>
    bool IsAvailable { get; }

    string BackendName { get; }

    bool SupportsContentSearch { get; }

    /// <summary>
    /// Streams results as the index answers, so the panel fills progressively
    /// instead of waiting on a complete result set.
    /// </summary>
    IAsyncEnumerable<FileEntry> SearchAsync(SearchQuery query, CancellationToken ct);
}
