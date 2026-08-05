using System.Diagnostics;
using System.Runtime.Versioning;

namespace Heimdall.Windows.Tests;

/// <summary>
/// A throwaway directory under the system temp, and the small vocabulary these
/// tests need to build a shape inside it.
///
/// **These tests use the real filesystem, deliberately.** Every bug this
/// project exists to pin down was a disagreement between what the code assumed
/// about NTFS and what NTFS does — a read-only attribute refusing a recursive
/// delete, Directory.Move rejecting a case-only rename, a junction being walked
/// as though it were a folder. A fake filesystem would have agreed with the
/// assumption in every one of those cases and reported all green.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TempTree : IDisposable
{
    public string Root { get; }

    public TempTree()
    {
        Root = Path.Combine(
            Path.GetTempPath(), "heimdall-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    /// <summary>An absolute path inside the tree. Nothing is created.</summary>
    public string At(params string[] parts)
        => Path.Combine([Root, .. parts]);

    public string Dir(params string[] parts)
    {
        var path = At(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public string Write(string relative, string content = "content")
    {
        var path = At(relative.Split('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteReadOnly(string relative, string content = "content")
    {
        var path = Write(relative, content);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        return path;
    }

    /// <summary>
    /// A junction, made by the platform's own tool rather than by the code under
    /// test — setup that shared an implementation with its subject would pass
    /// just as happily if both were wrong.
    ///
    /// `mklink /J` needs no elevation, unlike the symbolic link
    /// Directory.CreateSymbolicLink would make, which is the whole reason
    /// Heimdall reproduces junctions through the reparse-point ioctl.
    /// </summary>
    public string Junction(string relative, string target)
    {
        var path = At(relative.Split('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var mklink = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{path}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        mklink.WaitForExit();

        if (!Directory.Exists(path))
            throw new InvalidOperationException(
                $"Could not create a junction at '{path}': {mklink.StandardError.ReadToEnd().Trim()}");

        return path;
    }

    public bool Exists(params string[] parts)
        => File.Exists(At(parts)) || Directory.Exists(At(parts));

    public string Read(params string[] parts) => File.ReadAllText(At(parts));

    /// <summary>The names directly inside a folder, sorted, for whole-shape assertions.</summary>
    public string[] Names(params string[] parts)
        => [.. Directory.EnumerateFileSystemEntries(At(parts))
                 .Select(Path.GetFileName)
                 .OrderBy(n => n, StringComparer.Ordinal)!];

    public void Dispose()
    {
        // Read-only attributes are the point of some of these tests, and would
        // otherwise leave the temp directory behind for good.
        //
        // Not through a junction: a walk that follows one would be clearing
        // attributes in whatever tree it points at, which is the very mistake
        // several of these tests exist to catch.
        var walk = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", walk))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort; the delete below reports anything that matters.
        }

        try { Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temp directory left behind is not worth failing a green run over.
        }
    }
}
