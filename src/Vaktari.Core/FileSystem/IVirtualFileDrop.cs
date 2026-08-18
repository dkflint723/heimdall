namespace Vaktari.Core.FileSystem;

/// <summary>
/// Takes files that a drop is offering but which are not on disk.
///
/// **This is what dragging out of an archive is.** 7-Zip and Explorer's own zip
/// view both hand over a list of names and a stream for each, rather than
/// paths — the files do not exist anywhere until somebody asks for their
/// contents. A drop handler that looks only for paths therefore sees nothing,
/// which is why the drag appeared to do nothing at all.
///
/// Null where the desktop has no such notion, which is every desktop but
/// Windows: the freedesktop world passes URIs, and a file that has no location
/// has no URI either.
/// </summary>
public interface IVirtualFileDrop
{
    /// <summary>
    /// Whether this drop is offering files with no location on disk.
    ///
    /// Asked while the pointer is still moving, so it must be cheap: it reads
    /// the list of formats and nothing else.
    /// </summary>
    bool Offers(object dataTransfer);

    /// <summary>
    /// Writes them somewhere real and returns the paths, or an empty list if
    /// nothing could be taken.
    ///
    /// **Called once, on the drop.** Extracting on every pointer move would
    /// unpack an archive to disk for a drag that never landed.
    ///
    /// The caller owns what comes back and is expected to MOVE it into place:
    /// there is no original to preserve, so a copy would leave a duplicate in
    /// the temporary folder for nobody.
    /// </summary>
    IReadOnlyList<string> Take(object dataTransfer, CancellationToken token = default);
}
