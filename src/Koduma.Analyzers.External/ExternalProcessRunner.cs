using System.Diagnostics;

namespace Koduma.Analyzers.External;

public class ExternalProcessRunner
{
    public virtual async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

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
