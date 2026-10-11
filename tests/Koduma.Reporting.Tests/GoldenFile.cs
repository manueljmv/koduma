using System.Runtime.CompilerServices;
using Xunit;

internal static class GoldenFile
{
    public static void AssertMatches(string fileName, string actual, [CallerFilePath] string callerFilePath = "")
    {
        var goldenDir = Path.Combine(Path.GetDirectoryName(callerFilePath)!, "golden");
        var goldenPath = Path.Combine(goldenDir, fileName);
        var normalizedActual = actual.Replace("\r\n", "\n");

        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(goldenDir);
            File.WriteAllText(goldenPath, normalizedActual);
            Assert.Fail($"Golden file '{fileName}' did not exist; created it from the current output at {goldenPath}. Review its content, then re-run this test — it should now pass — and commit the new file.");
        }

        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        Assert.Equal(expected, normalizedActual);
    }
}
