using Vaktari.Core.FileSystem;

namespace Vaktari.Windows;

/// <summary>
/// "New file from template" on Windows.
///
/// **The Templates folder, not the ShellNew registry keys.** WINDOWS.md names
/// ShellNew as the Windows equivalent, and it is what Explorer's own New submenu
/// is built from — but half its entries carry no template file at all, only a
/// NullFile marker meaning "create an empty one", and reading it needs the
/// registry this project does not yet reference. <c>%APPDATA%\Microsoft\Windows\Templates</c>
/// is a real folder of real files, which is exactly the shape
/// <see cref="ITemplateProvider"/> expects and what ~/Templates is on Linux.
/// </summary>
public sealed class WindowsTemplates : ITemplateProvider
{
    private static string Directory_ => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Templates");

    public IReadOnlyList<FileTemplate> Discover()
    {
        try
        {
            if (!Directory.Exists(Directory_)) return [];

            return Directory.EnumerateFiles(Directory_)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(path => new FileTemplate(
                    Path.GetFileNameWithoutExtension(path), path))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
