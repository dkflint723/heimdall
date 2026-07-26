namespace Rove.Core.FileSystem;

public sealed record FileTemplate(string Name, string Path);

/// <summary>
/// The "new file from template" list. Platform-specific because the location is
/// a desktop convention — <c>XDG_TEMPLATES_DIR</c> here, the Windows
/// <c>ShellNew</c> registry keys there — even though using one is just a copy.
/// </summary>
public interface ITemplateProvider
{
    IReadOnlyList<FileTemplate> Discover();
}
