using System.Security.Cryptography;

namespace Vaktari.Core.FileSystem;

/// <summary>The three digests, computed together.</summary>
public sealed record ChecksumSet
{
    public required string Md5 { get; init; }
    public required string Sha1 { get; init; }
    public required string Sha256 { get; init; }
}

/// <summary>
/// File digests for verifying a download against a published checksum.
///
/// **One pass over the file, feeding all three.** The obvious implementation
/// hashes the file once per algorithm, which is three times the I/O for no
/// benefit — and on a multi-gigabyte ISO, which is exactly what people check
/// checksums on, that is the difference between a wait and a long wait.
///
/// MD5 and SHA-1 are here despite being broken for adversarial purposes.
/// Verifying that a download matches what a project published is not an
/// adversarial purpose, and a great many projects still publish only MD5 —
/// omitting it would make the feature useless for the case it exists for.
///
/// In Core because hashing a file is the same everywhere; nothing here is
/// platform-specific.
/// </summary>
public static class Checksums
{
    /// <summary>Big enough that syscall overhead disappears, small enough to
    /// report progress often and stay out of the large object heap.</summary>
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// <paramref name="progress"/> receives a fraction from 0 to 1, or is
    /// never called when the file's length is unknown or zero.
    /// </summary>
    public static async Task<ChecksumSet> ComputeAsync(
        string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var total = stream.Length;
        var read = 0L;
        var buffer = new byte[BufferSize];

        // Throttled: a progress report per 64 KB block would post hundreds of
        // thousands of dispatcher callbacks on a large file and cost more than
        // the hashing.
        var lastReported = -1;

        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (count == 0) break;

            var span = buffer.AsSpan(0, count);

            md5.AppendData(span);
            sha1.AppendData(span);
            sha256.AppendData(span);

            read += count;

            if (progress is null || total <= 0) continue;

            var percent = (int)(read * 100 / total);
            if (percent == lastReported) continue;

            lastReported = percent;
            progress.Report(percent / 100.0);
        }

        return new ChecksumSet
        {
            Md5 = Hex(md5.GetHashAndReset()),
            Sha1 = Hex(sha1.GetHashAndReset()),
            Sha256 = Hex(sha256.GetHashAndReset()),
        };
    }

    /// <summary>Lower case, which is what every project publishes.</summary>
    private static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}
