using System.Net.Http;
using Vaktari.Core.FileSystem;

namespace Vaktari.Ui.Settings;

/// <summary>
/// Fetches a theme and unpacks it, so that choosing one is a button rather than
/// a download, an extraction, and a folder picker.
///
/// **The extraction is the reason this exists.** Papirus is built out of some
/// forty thousand symbolic links, Windows creates none of them without
/// Developer Mode, and every ordinary unzipping tool therefore fails forty
/// thousand times and leaves a theme with holes where the file and folder icons
/// should be. Unpacking it here means the links are read rather than made — see
/// <see cref="IconThemeArchive"/>, which is also where the containment and size
/// rules live.
/// </summary>
public static class IconThemeInstaller
{
    /// <summary>
    /// Generous, because these are large files on whatever connection somebody
    /// has, and stingier than none, because a stalled download should not hang
    /// a settings window forever. Applies to the whole transfer, which is why
    /// it is minutes rather than seconds.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Downloads and installs, reporting progress from 0 to 1 where the server
    /// says how large the file is.
    ///
    /// **Streamed straight into the unpacking** rather than saved and then
    /// read: a hundred and ten megabytes is not worth writing twice, and a
    /// transfer that fails halfway leaves nothing behind because the unpacking
    /// stages its work and publishes only at the end.
    /// </summary>
    public static async Task<IconThemeArchive.Installed> InstallAsync(
        IconThemeSource source,
        IProgress<double>? progress = null,
        CancellationToken token = default)
    {
        using var http = new HttpClient { Timeout = Patience };

        // GitHub redirects archive downloads to a storage host and refuses
        // requests without one.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vaktari");

        using var response = await http
            .GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var expected = response.Content.Headers.ContentLength;

        await using var network = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var counted = new CountingStream(network, expected, progress);

        // Off the calling thread: unpacking fifty thousand files is real work,
        // and the caller is a settings window.
        return await Task
            .Run(() => IconThemeArchive.Install(counted, IconThemeCatalogue.FolderFor(source), token), token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Counts what passes through, so the window can show how far along a
    /// download is. Read-only and forward-only, which is all the archive reader
    /// asks for.
    /// </summary>
    private sealed class CountingStream(Stream inner, long? expected, IProgress<double>? progress) : Stream
    {
        private long _read;
        private int _lastReported = -1;

        public override int Read(byte[] buffer, int offset, int count)
            => Report(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Report(inner.Read(buffer));

        private int Report(int read)
        {
            if (read <= 0 || expected is not { } total || total <= 0) return read;

            _read += read;

            // Whole percents only. A progress bar told a hundred thousand times
            // is a hundred thousand dispatcher posts and a slower download.
            var percent = (int)(_read * 100 / total);

            if (percent != _lastReported)
            {
                _lastReported = percent;
                progress?.Report(Math.Clamp(percent / 100d, 0, 1));
            }

            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => expected ?? throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
