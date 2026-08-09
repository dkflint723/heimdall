using System.Diagnostics;
using System.Text;
using Vaktari.Core;

namespace Vaktari.Windows;

/// <summary>
/// User scripts, invoked on the current selection.
///
/// **The extension is the opt-in, where Linux uses the execute bit.** Windows
/// has no execute permission, so a folder of ordinary files needs some other
/// signal for "this is meant to run" — and the extension is also what decides
/// how to run it, so the two questions have one answer.
/// </summary>
public sealed class WindowsScriptRunner : IScriptRunner
{
    /// <summary>
    /// Ordered by how the shell would treat them. <c>.ps1</c> needs an explicit
    /// interpreter because double-clicking one opens Notepad by default and
    /// PowerShell's execution policy blocks it besides — see <see cref="Build"/>.
    /// </summary>
    private static readonly string[] Runnable =
        [".bat", ".cmd", ".ps1", ".exe", ".com"];

    public WindowsScriptRunner(string stateDirectory)
    {
        ScriptsDirectory = Path.Combine(stateDirectory, "scripts");
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

            File.WriteAllText(Path.Combine(ScriptsDirectory, "README.txt"),
                """
                Any .bat, .cmd, .ps1 or .exe in this folder appears in Vaktari's
                context menu.

                It is run with the selected paths as arguments, and with the
                folder you are looking at as its working directory. Anything it
                prints is shown in the status bar.

                  VAKTARI_CWD       the folder being listed
                  VAKTARI_SELECTED  number of selected items

                The menu entry is the filename without its extension, with
                underscores shown as spaces.

                PowerShell scripts are run with -ExecutionPolicy Bypass, so a
                .ps1 here runs without changing the machine's policy. That also
                means a script in this folder is trusted: it is your own code,
                and nothing checks it.
                """);
        }
        catch
        {
            // A read-only profile is not a reason to fail startup.
        }
    }

    public IReadOnlyList<ScriptCommand> Discover()
    {
        try
        {
            if (!Directory.Exists(ScriptsDirectory)) return [];

            return Directory.EnumerateFiles(ScriptsDirectory)
                .Where(p => Runnable.Contains(
                    Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
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

    /// <summary>
    /// A .ps1 is not executable on its own — the shell opens it in an editor,
    /// and the default execution policy would refuse it anyway. Everything else
    /// runs directly.
    /// </summary>
    private static ProcessStartInfo Build(ScriptCommand script, IReadOnlyList<string> paths)
    {
        if (!Path.GetExtension(script.Path).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var direct = new ProcessStartInfo(script.Path);
            foreach (var path in paths) direct.ArgumentList.Add(path);
            return direct;
        }

        var shell = new ProcessStartInfo("powershell.exe");
        shell.ArgumentList.Add("-NoLogo");
        shell.ArgumentList.Add("-NonInteractive");
        shell.ArgumentList.Add("-ExecutionPolicy");
        shell.ArgumentList.Add("Bypass");
        shell.ArgumentList.Add("-File");
        shell.ArgumentList.Add(script.Path);
        foreach (var path in paths) shell.ArgumentList.Add(path);

        return shell;
    }

    public async ValueTask<string> RunAsync(
        ScriptCommand script,
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var info = Build(script, paths);

        info.WorkingDirectory = workingDirectory;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        // Otherwise every .bat flashes a console window over the file manager.
        info.CreateNoWindow = true;

        info.Environment["VAKTARI_CWD"] = workingDirectory;
        info.Environment["VAKTARI_SELECTED"] = paths.Count.ToString();

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start {script.Name}.");

        // Disposing a Process does not stop the child. Without this, cancelling
        // leaves the user's script running with nothing showing it and no way to
        // reach it from the application.
        using var cancellation = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { Quiet.Swallowed("scripts", ex); }
        });

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
