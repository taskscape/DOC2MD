internal static class CliExecutableLocator
{
    public static string Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("DOC2MD_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var executableName = OperatingSystem.IsWindows() ? "DOC2MD.Cli.exe" : "DOC2MD.Cli";
        var sibling = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(sibling))
        {
            return sibling;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var candidate = Path.Combine(
                    current.FullName,
                    "src",
                    "DOC2MD.Cli",
                    "bin",
                    configuration,
                    "net10.0",
                    executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            current = current.Parent;
        }

        return OperatingSystem.IsWindows() ? executableName : "doc2md";
    }
}
