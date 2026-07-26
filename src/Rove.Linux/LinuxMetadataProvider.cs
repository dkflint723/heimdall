using System.Buffers.Binary;
using Rove.Core.FileSystem;

namespace Rove.Linux;

/// <summary>
/// Inline metadata. Image dimensions are read from the file header rather than
/// by decoding — a few dozen bytes instead of a forty megapixel bitmap, which
/// is the difference between this being free and it making scrolling stutter.
/// </summary>
public sealed class LinuxMetadataProvider : IFileMetadataProvider
{
    private static readonly string[] ImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
    ];

    public bool CanDescribe(string path, bool isDirectory)
        => isDirectory
           || ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async ValueTask<string?> DescribeAsync(string path, bool isDirectory, CancellationToken ct)
    {
        try
        {
            if (isDirectory) return await CountAsync(path, ct).ConfigureAwait(false);

            var size = await Task.Run(() => ReadImageSize(path), ct).ConfigureAwait(false);
            return size is var (width, height) && width > 0 ? $"{width} × {height}" : null;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask<string?> DescribeAccessAsync(
        string path, bool isDirectory, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                var mode = File.GetUnixFileMode(path);

                static char Bit(UnixFileMode mode, UnixFileMode flag, char set)
                    => mode.HasFlag(flag) ? set : '-';

                // Leading type character then three triplets, exactly as ls -l
                // prints it — the point is that it reads at a glance to anyone
                // who already knows the format.
                var type = isDirectory ? 'd'
                    : File.ResolveLinkTarget(path, returnFinalTarget: false) is not null ? 'l'
                    : '-';

                Span<char> text =
                [
                    type,
                    Bit(mode, UnixFileMode.UserRead, 'r'),
                    Bit(mode, UnixFileMode.UserWrite, 'w'),
                    Bit(mode, UnixFileMode.UserExecute, 'x'),
                    Bit(mode, UnixFileMode.GroupRead, 'r'),
                    Bit(mode, UnixFileMode.GroupWrite, 'w'),
                    Bit(mode, UnixFileMode.GroupExecute, 'x'),
                    Bit(mode, UnixFileMode.OtherRead, 'r'),
                    Bit(mode, UnixFileMode.OtherWrite, 'w'),
                    Bit(mode, UnixFileMode.OtherExecute, 'x'),
                ];

                return new string(text);
            }
            catch
            {
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Non-recursive: the immediate child count is what a listing wants,
    /// and it costs one readdir.</summary>
    private static async ValueTask<string?> CountAsync(string path, CancellationToken ct)
        => await Task.Run(() =>
        {
            try
            {
                var count = new DirectoryInfo(path)
                    .EnumerateFileSystemInfos()
                    .Take(10_001)
                    .Count();

                return count > 10_000 ? "10,000+ items" : $"{count:N0} items";
            }
            catch
            {
                return null;
            }
        }, ct).ConfigureAwait(false);

    private static (int Width, int Height)? ReadImageSize(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[32];

        if (stream.Read(header) < 32) return null;

        // PNG: signature, then the IHDR chunk carries the dimensions.
        if (header[..8].SequenceEqual("\x89PNG\r\n\x1a\n"u8))
        {
            return (BinaryPrimitives.ReadInt32BigEndian(header[16..20]),
                    BinaryPrimitives.ReadInt32BigEndian(header[20..24]));
        }

        if (header[..3].SequenceEqual("GIF"u8))
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]),
                    BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]));
        }

        if (header[..2].SequenceEqual("BM"u8))
        {
            return (BinaryPrimitives.ReadInt32LittleEndian(header[18..22]),
                    BinaryPrimitives.ReadInt32LittleEndian(header[22..26]));
        }

        if (header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return ReadWebPSize(stream, header);

        if (header[0] == 0xFF && header[1] == 0xD8)
            return ReadJpegSize(stream);

        return null;
    }

    private static (int, int)? ReadWebPSize(FileStream stream, ReadOnlySpan<byte> header)
    {
        // VP8X carries the canvas size as two 24-bit values, minus one.
        if (header[12..16].SequenceEqual("VP8X"u8))
        {
            var w = header[24] | (header[25] << 8) | (header[26] << 16);
            var h = header[27] | (header[28] << 8) | (header[29] << 16);
            return (w + 1, h + 1);
        }

        if (header[12..16].SequenceEqual("VP8 "u8))
        {
            stream.Position = 26;
            Span<byte> size = stackalloc byte[4];
            if (stream.Read(size) < 4) return null;

            return (BinaryPrimitives.ReadUInt16LittleEndian(size[..2]) & 0x3FFF,
                    BinaryPrimitives.ReadUInt16LittleEndian(size[2..]) & 0x3FFF);
        }

        return null;
    }

    /// <summary>
    /// Walks the segment chain to the start-of-frame marker. JPEG has no fixed
    /// header offset for its dimensions, so there is no shortcut.
    /// </summary>
    private static (int, int)? ReadJpegSize(FileStream stream)
    {
        stream.Position = 2;
        Span<byte> pair = stackalloc byte[2];

        while (stream.Position < stream.Length - 8)
        {
            if (stream.Read(pair) < 2 || pair[0] != 0xFF) return null;

            var marker = pair[1];

            // Standalone markers carry no length.
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) continue;

            if (stream.Read(pair) < 2) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(pair);

            var isFrame = marker is >= 0xC0 and <= 0xCF
                          && marker is not (0xC4 or 0xC8 or 0xCC);

            if (isFrame)
            {
                Span<byte> frame = stackalloc byte[5];
                if (stream.Read(frame) < 5) return null;

                return (BinaryPrimitives.ReadUInt16BigEndian(frame[3..5]),
                        BinaryPrimitives.ReadUInt16BigEndian(frame[1..3]));
            }

            stream.Position += length - 2;
        }

        return null;
    }
}
