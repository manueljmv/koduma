using System.Diagnostics;

namespace Koduma.Analyzers.External;

public class ExternalProcessRunner
{
    internal const int CmdCommandLineLimit = 8191;

    // A .cmd shim (npm, pnpm...) re-expands %* inside its own command line, which shares the same limit,
    // so batches leave room for the shim's own prefix (node.exe path, script path, etc.).
    internal const int CmdShimBatchLimit = CmdCommandLineLimit - 2048;

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

    // PATH only, never the current directory, matching NoDefaultCurrentDirectoryInExePath below.
    internal static bool IsOnPath(string executable) =>
        Path.IsPathRooted(executable)
            ? File.Exists(executable)
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(directory => Path.IsPathRooted(directory.Trim('"')) && File.Exists(Path.Combine(directory.Trim('"'), executable)));

    internal static bool RoutesThroughCmd(string executable) => OperatingSystem.IsWindows() && IsCmdShim(executable);

    /// <summary>Splits <paramref name="batchedArguments"/> into invocations whose cmd.exe command line stays within <see cref="CmdShimBatchLimit"/>.</summary>
    internal static IReadOnlyList<IReadOnlyList<string>> BatchForCmd(string executable, IReadOnlyList<string> fixedArguments, IReadOnlyList<string> batchedArguments)
    {
        var baseLength = string.Join(" ", RerouteThroughCmd(executable, fixedArguments).Arguments).Length;
        var batches = new List<IReadOnlyList<string>>();
        var current = new List<string>(fixedArguments);
        var length = baseLength;

        foreach (var argument in batchedArguments)
        {
            // Each extra argument adds a separating space and its surrounding quotes.
            var cost = argument.Length + 3;
            if (length + cost > CmdShimBatchLimit && current.Count > fixedArguments.Count)
            {
                batches.Add(current);
                current = new List<string>(fixedArguments);
                length = baseLength;
            }

            current.Add(argument);
            length += cost;
        }

        if (current.Count > fixedArguments.Count || batches.Count == 0)
            batches.Add(current);

        return batches;
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

        if (RoutesThroughCmd(executable))
        {
            // cmd.exe exits 1 for an unknown command, indistinguishable from a tool's own exit code 1.
            if (!IsOnPath(executable))
                return (-1, string.Empty, $"'{executable}' was not found on PATH.");

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
