namespace Heimdall.Core.FileSystem;

/// <summary>One labelled fact about a file. Platform extras arrive as these
/// rather than as typed fields, so Core does not grow a permissions model that
/// only means something on one operating system.</summary>
public sealed record PropertyRow(string Label, string Value);

public sealed record PropertyGroup(string Label, IReadOnlyList<PropertyRow> Rows);

/// <summary>
/// Everything a properties view shows. The universal fields are typed because
/// every platform has them; anything platform-specific — POSIX permissions,
/// NTFS ACLs, alternate data streams — lives in <see cref="Groups"/>.
/// </summary>
public sealed record FileDetails
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }

    public string Kind { get; init; } = "";
    public long Size { get; init; }

    public DateTimeOffset? Modified { get; init; }
    public DateTimeOffset? Accessed { get; init; }
    public DateTimeOffset? Created { get; init; }

    public string? SymlinkTarget { get; init; }

    public IReadOnlyList<PropertyGroup> Groups { get; init; } = [];
}

/// <summary>Progress while a directory is being measured.</summary>
public readonly record struct SizeProgress(long Bytes, int Files, int Folders);

public interface IPropertiesProvider
{
    ValueTask<FileDetails> GetAsync(string path, CancellationToken ct);

    /// <summary>
    /// Walks a directory to total its contents. Explicitly on demand: doing it
    /// automatically would make opening properties on a home directory hang.
    /// </summary>
    ValueTask<SizeProgress> MeasureAsync(
        string path, IProgress<SizeProgress> progress, CancellationToken ct);
}
