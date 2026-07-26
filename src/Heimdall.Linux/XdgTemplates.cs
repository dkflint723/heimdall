using Rove.Core.FileSystem;

namespace Rove.Linux;

/// <summary>
/// Templates from the user's XDG templates directory — the same folder Dolphin
/// and Nautilus offer under "Create New". Read fresh each time rather than
/// cached: a template is a file the user drops in, and having to restart before
/// it appears would be baffling.
/// </summary>
public sealed class XdgTemplates : ITemplateProvider
{
    public IReadOnlyList<FileTemplate> Discover()
    {
        var directory = ReadTemplatesDir();
        if (directory is null || !Directory.Exists(directory)) return [];

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileTemplate(Path.GetFileNameWithoutExtension(f), f))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadTemplatesDir()
        => XdgUserDirs.Read("XDG_TEMPLATES_DIR")
           ?? Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Templates");
}
