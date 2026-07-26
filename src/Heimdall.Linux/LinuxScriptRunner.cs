using System.Diagnostics;
using System.Text;
using Heimdall.Core;

namespace Heimdall.Linux;

public sealed class LinuxScriptRunner : IScriptRunner
{
    public LinuxScriptRunner()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");

        ScriptsDirectory = Path.Combine(dataHome, "heimdall", "scripts");

        // Carried over from the old name, once, so scripts written before the
        // rename keep working without being moved by hand.
        Heimdall.Core.PreviousName.Adopt(
            Path.Combine(dataHome, "heimdall"), Path.Combine(dataHome, "rove"));
        EnsureDirectory();
    }

    public string ScriptsDirectory { get; }

    /// <summary>
    /// Created eagerly with a README, because a feature whose entire interface
    /// is "put a file in a folder" is invisible until that folder exists.
    /// </summary>
    private void EnsureDirectory()
    {
        try
        {
            if (Directory.Exists(ScriptsDirectory)) return;

            Directory.CreateDirectory(ScriptsDirectory);

            File.WriteAllText(Path.Combine(ScriptsDirectory, "README"),
                """
                Any executable file in this folder appears in Heimdall's context menu.

                It is run with the selected paths as arguments, and with the
                folder you are looking at as its working directory. Anything it
                prints is shown in the status bar.

                  HEIMDALL_CWD       the folder being listed
                  HEIMDALL_SELECTED  number of selected items

                Make a script executable with: chmod +x <file>
                The menu entry is the filename, underscores shown as spaces.
                """);
        }
        catch
        {
            // A read-only home is not a reason to fail startup.
        }
    }

    public IReadOnlyList<ScriptCommand> Discover()
    {
        try
        {
            if (!Directory.Exists(ScriptsDirectory)) return [];

            return Directory.EnumerateFiles(ScriptsDirectory)
                .Where(IsExecutable)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(path => new ScriptCommand(
                    Path.GetFileNameWithoutExtension(path).Replace('_', ' '),
                    path))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>The execute bit is the opt-in — a half-written script sitting in
    /// the folder should not appear in a menu until it is meant to run.</summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);

            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask<string> RunAsync(
        ScriptCommand script,
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var info = new ProcessStartInfo(script.Path)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var path in paths) info.ArgumentList.Add(path);

        info.Environment["HEIMDALL_CWD"] = workingDirectory;
        info.Environment["HEIMDALL_SELECTED"] = paths.Count.ToString();

        // The old names are still set. Scripts are the user's own code living
        // outside this repo, and a rename here should not silently break them.
        info.Environment["ROVE_CWD"] = workingDirectory;
        info.Environment["ROVE_SELECTED"] = paths.Count.ToString();

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {script.Name}.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        var output = (await stdout.ConfigureAwait(false)).Trim();
        var error = (await stderr.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            var message = new StringBuilder($"{script.Name} exited {process.ExitCode}");
            if (error.Length > 0) message.Append(": ").Append(FirstLine(error));
            return message.ToString();
        }

        return output.Length > 0 ? FirstLine(output) : $"{script.Name} finished";
    }

    /// <summary>The status bar is one line; the rest would be truncated anyway.</summary>
    private static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline].TrimEnd();
    }
}
