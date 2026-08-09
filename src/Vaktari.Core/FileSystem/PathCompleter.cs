namespace Vaktari.Core.FileSystem;

/// <summary>
/// Tab-completion for a typed path.
///
/// Follows the shell convention people already have in their fingers: the first
/// press extends as far as every candidate agrees, and further presses cycle
/// through them. Completing straight to the first match would be faster to
/// write and worse to use — it moves the text somewhere you did not ask for
/// while you are still typing.
///
/// Holds the cycle position, so it is per-input-box rather than static.
/// </summary>
public sealed class PathCompleter
{
    private string _lastResult = "";
    private List<string> _matches = [];
    private int _index = -1;

    // The directory and fragment the candidates were built FOR. Cycling must
    // stay anchored to these rather than re-reading the text, because each
    // completion appends a trailing "/" and re-splitting would then treat the
    // folder just offered as the one to search — so the next candidate landed
    // INSIDE it instead of replacing it, and Tab produced
    // /home/flint/ → /home/flint/Desktop/ → /home/flint/Desktop/Documents/.
    private string _directory = "";
    private string _partial = "";

    /// <summary>Forgets the cycle; call when the user types something new.</summary>
    public void Reset()
    {
        _lastResult = "";
        _matches = [];
        _index = -1;
        _directory = "";
        _partial = "";
    }

    /// <summary>
    /// The next completion for <paramref name="text"/>, or null when there is
    /// nothing to add.
    /// </summary>
    public string? Complete(string text)
    {
        // Rebuild when the user has typed something new, and also when the last
        // offer was UNAMBIGUOUS — one candidate means the trailing "/" has taken
        // us inside it, so the next Tab should complete in there. With several
        // candidates the text still ends in a folder we are choosing BETWEEN,
        // so the cycle continues instead.
        if (text != _lastResult || _matches.Count <= 1) Rebuild(text);

        if (_matches.Count == 0) return null;

        if (_matches.Count == 1)
        {
            _index = 0;
            return Remember(Join(_directory, _matches[0]));
        }

        // Extend to the shared prefix first, and only start cycling once there
        // is nothing left that every candidate agrees on.
        var shared = CommonPrefix(_matches);

        if (_index < 0 && shared.Length > _partial.Length)
            return Remember(Join(_directory, shared), cycling: false);

        _index = (_index + 1) % _matches.Count;
        return Remember(Join(_directory, _matches[_index]));
    }

    private string Remember(string result, bool cycling = true)
    {
        _lastResult = result;
        if (!cycling) _index = -1;
        return result;
    }

    private void Rebuild(string text)
    {
        _index = -1;
        _matches = [];

        var (directory, partial) = Split(text);

        _directory = directory;
        _partial = partial;

        if (directory.Length == 0) return;

        try
        {
            _matches = Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.StartsWith(partial, StringComparison.OrdinalIgnoreCase))

                // A leading dot has to be asked for explicitly, or completing in
                // a home directory is mostly configuration folders.
                .Where(name => partial.StartsWith('.') || !name.StartsWith('.'))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            // Unreadable or missing directory: nothing to offer.
        }
    }

    /// <summary>Splits into the folder to search and the fragment to match.</summary>
    private static (string Directory, string Partial) Split(string text)
    {
        var path = Expand(text);

        // A trailing separator means "inside this folder", not "a folder whose
        // name is empty" — so everything in it is a candidate.
        if (path.EndsWith('/')) return (path, "");

        var slash = path.LastIndexOf('/');
        if (slash < 0) return ("", path);

        var directory = slash == 0 ? "/" : path[..slash];
        return (directory, path[(slash + 1)..]);
    }

    private static string Join(string directory, string name)
    {
        var joined = directory.EndsWith('/') ? directory + name : $"{directory}/{name}";

        // Trailing separator, so the next Tab completes inside it rather than
        // re-matching the folder just chosen.
        return joined + "/";
    }

    private static string Expand(string text)
    {
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home + text[1..];
        }

        return text;
    }

    private static string CommonPrefix(List<string> values)
    {
        if (values.Count == 0) return "";

        var prefix = values[0];

        foreach (var value in values.Skip(1))
        {
            var length = 0;

            while (length < prefix.Length && length < value.Length
                   && char.ToUpperInvariant(prefix[length]) == char.ToUpperInvariant(value[length]))
                length++;

            prefix = prefix[..length];
            if (prefix.Length == 0) break;
        }

        return prefix;
    }
}
