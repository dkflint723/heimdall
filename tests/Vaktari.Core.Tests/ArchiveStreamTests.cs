using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The stream that puts a sniffed header back in front of an archive.
///
/// **Two contracts a Stream is not obliged to honour, and a decompressor
/// assumes anyway.** Both were broken here, in ways that no amount of testing
/// through the archive reader would have found — the small fixtures pass
/// either way, and it took unpacking a real 5 MB theme for the second one to
/// appear at all.
///
/// Neither is defensive padding. They are what the xz decoder actually
/// requires, and the failure it gives when they are missing is "Block check
/// corrupt", which reads as a damaged download and sends you looking at the
/// file rather than at this.
/// </summary>
public sealed class ArchiveStreamTests
{
    /// <summary>Hands back a few bytes at a time, which every Stream is
    /// entitled to do and a network one does constantly.</summary>
    private sealed class Trickle(byte[] data) : Stream
    {
        private int _at;

        public override int Read(byte[] b, int o, int c) => Read(b.AsSpan(o, c));

        public override int Read(Span<byte> buffer)
        {
            var take = Math.Min(Math.Min(buffer.Length, 3), data.Length - _at);

            if (take <= 0) return 0;

            data.AsSpan(_at, take).CopyTo(buffer);
            _at += take;

            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static byte[] Bytes(int count) => [.. Enumerable.Range(0, count).Select(i => (byte)i)];

    /// <summary>
    /// **A read fills its buffer.** A stream may return less, and a correct
    /// reader loops — the xz decoder does not, and gives up partway through
    /// with "Reached end of stream unexpectedly". Reading an archive off a
    /// network is nothing but short reads.
    /// </summary>
    [Fact]
    public void A_read_returns_everything_that_was_asked_for()
    {
        var all = Bytes(64);

        using var stream = new IconThemeArchive.ConcatStream(all[..6], new Trickle(all[6..]));

        var buffer = new byte[64];

        Assert.Equal(64, stream.Read(buffer, 0, 64));
        Assert.Equal(all, buffer);

        // And nothing beyond the end, rather than blocking or repeating.
        Assert.Equal(0, stream.Read(buffer, 0, 64));
    }

    /// <summary>
    /// **The position is how much has been handed out**, not how far through
    /// the sniffed header we are — which is what it used to report, leaving it
    /// stuck at six forever.
    ///
    /// An xz file is blocks each padded to a four-byte boundary, and the
    /// decoder finds that padding from where the stream says it is. A frozen
    /// position made it look for every checksum in the wrong place.
    /// </summary>
    [Fact]
    public void The_position_counts_every_byte_handed_out()
    {
        var all = Bytes(64);

        using var stream = new IconThemeArchive.ConcatStream(all[..6], new Trickle(all[6..]));

        Assert.Equal(0, stream.Position);

        Assert.Equal(4, stream.Read(new byte[4], 0, 4));
        Assert.Equal(4, stream.Position);

        // Across the seam between the header and the stream behind it, which is
        // exactly where it used to stop counting.
        Assert.Equal(20, stream.Read(new byte[20], 0, 20));
        Assert.Equal(24, stream.Position);

        Assert.Equal(40, stream.Read(new byte[40], 0, 40));
        Assert.Equal(64, stream.Position);
    }
}
