using System.Diagnostics;

namespace Koduma.Analyzers.External;

public class ExternalProcessRunner
{
    internal static bool IsCmdShim(string executable) =>
        executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    internal static string? FindUnsafeArgument(IReadOnlyList<string> arguments) =>
        arguments.FirstOrDefault(a => a.IndexOfAny(new[] { '&', '|', '^', '%', '<', '>', '(', ')', '"', '!' }) >= 0);

    internal static (string Executable, IReadOnlyList<string> Arguments) RerouteThroughCmd(string executable, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { executable };
        parts.AddRange(arguments);
        var commandLine = "\"" + string.Join(" ", parts.Select(p => "\"" + p + "\"")) + "\"";
        return ("cmd.exe", new[] { "/d", "/s", "/c", commandLine });
    }

    public virtual async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        if (OperatingSystem.IsWindows() && IsCmdShim(executable))
        {
            var unsafeArg = FindUnsafeArgument(arguments);
            if (unsafeArg is not null)
                return (-1, string.Empty, $"Argument '{unsafeArg}' contains a character unsafe to pass through cmd.exe and was rejected.");

            var (shell, shellArgs) = RerouteThroughCmd(executable, arguments);
            startInfo.FileName = shell;
            // Raw Arguments, not ArgumentList: ArgumentList escapes '"' as '\"', which cmd.exe does not understand.
            startInfo.Arguments = string.Join(" ", shellArgs);
        }
        else
        {
            foreach (var arg in arguments)
                startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{executable}'.");

            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (process.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
