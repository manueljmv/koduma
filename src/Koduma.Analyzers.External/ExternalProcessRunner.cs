using System.Diagnostics;

namespace Koduma.Analyzers.External;

public class ExternalProcessRunner
{
    internal const int CmdCommandLineLimit = 8191;

    internal static bool IsCmdShim(string executable) =>
        executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    internal static string? FindUnsafeArgument(IReadOnlyList<string> arguments) =>
        arguments.FirstOrDefault(a => a.IndexOfAny(new[] { '&', '|', '^', '%', '<', '>', '(', ')', '"', '!', '\r', '\n', '\0' }) >= 0);

    internal static string ResolveCmdExecutable()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrEmpty(comSpec)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
            : comSpec;
    }

    internal static (string Executable, IReadOnlyList<string> Arguments) RerouteThroughCmd(string executable, IReadOnlyList<string> arguments)
    {
        var parts = new List<string> { executable };
        parts.AddRange(arguments);
        var commandLine = "\"" + string.Join(" ", parts.Select(p => "\"" + p + "\"")) + "\"";
        return (ResolveCmdExecutable(), new[] { "/d", "/s", "/c", commandLine });
    }

    internal static string? CheckCommandLineLength(IReadOnlyList<string> arguments, string fullCommandLine)
    {
        if (fullCommandLine.Length <= CmdCommandLineLimit)
            return null;

        return $"Command line for cmd.exe would be {fullCommandLine.Length} characters across {arguments.Count} arguments, " +
               $"exceeding cmd.exe's {CmdCommandLineLimit}-character limit; the command was not run. " +
               "Reduce the number of files analyzed in a single invocation.";
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
            // Raw Arguments, not ArgumentList: ArgumentList escapes '"' as '\"', which cmd.exe does not understand.
            var fullArguments = string.Join(" ", shellArgs);

            var lengthError = CheckCommandLineLength(arguments, fullArguments);
            if (lengthError is not null)
                return (-1, string.Empty, lengthError);

            startInfo.FileName = shell;
            startInfo.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
            startInfo.Arguments = fullArguments;
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
