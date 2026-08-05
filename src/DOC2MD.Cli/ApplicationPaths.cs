internal static class ApplicationPaths
{
    public const string ResourceRootEnvironmentVariable = "DOC2MD_RESOURCE_ROOT";

    public static string ResourceRoot
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable(ResourceRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            var candidates = new List<string>();

            if (OperatingSystem.IsMacOS())
            {
                candidates.Add(Path.GetFullPath(Path.Combine(baseDirectory, "..", "Resources")));
            }

            candidates.Add(Path.Combine(baseDirectory, "Resources"));
            candidates.Add(baseDirectory);

            return candidates.FirstOrDefault(IsResourceRoot) ?? candidates[0];
        }
    }

    public static string MarkItDownPackageRoot => Path.Combine(ResourceRoot, "markitdown");

    public static string MarkItDownSourceRoot => Path.Combine(MarkItDownPackageRoot, "src");

    public static string BundledTessdataRoot => Path.Combine(ResourceRoot, "tessdata");

    public static string UserRuntimeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DOC2MD",
        "runtime");

    public static string UserMarkItDownVenvRoot => Path.Combine(UserRuntimeRoot, "markitdown-venv");

    public static string GetVirtualEnvironmentPython(string virtualEnvironmentRoot) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(virtualEnvironmentRoot, "Scripts", "python.exe")
            : Path.Combine(virtualEnvironmentRoot, "bin", "python3");

    public static IEnumerable<string> BundledPythonCandidates()
    {
        var pythonRoot = Path.Combine(ResourceRoot, "python");
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(pythonRoot, "python.exe");
        }
        else
        {
            yield return Path.Combine(pythonRoot, "bin", "python3");
            yield return Path.Combine(pythonRoot, "bin", "python");
        }
    }

    public static IEnumerable<string> TesseractExecutableCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("DOC2MD_TESSERACT_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return configured;
        }

        var bundledRoot = Path.Combine(ResourceRoot, "tesseract");
        yield return OperatingSystem.IsWindows()
            ? Path.Combine(bundledRoot, "tesseract.exe")
            : Path.Combine(bundledRoot, "bin", "tesseract");

        var pathCommand = FindCommandOnPath(OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract");
        if (pathCommand is not null)
        {
            yield return pathCommand;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Tesseract-OCR", "tesseract.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin/tesseract";
            yield return "/usr/local/bin/tesseract";
        }
    }

    public static IEnumerable<string> LibreOfficeExecutableCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("DOC2MD_SOFFICE_PATH")
            ?? Environment.GetEnvironmentVariable("DOC2MD_LIBREOFFICE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var candidate in ExpandLibreOfficePath(configured))
            {
                yield return candidate;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(ResourceRoot, "libreoffice", "program", "soffice.exe");

            foreach (var specialFolder in new[]
                     {
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86
                     })
            {
                var root = Environment.GetFolderPath(specialFolder);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/LibreOffice.app/Contents/MacOS/soffice";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications",
                "LibreOffice.app",
                "Contents",
                "MacOS",
                "soffice");
            yield return "/opt/homebrew/bin/soffice";
            yield return "/usr/local/bin/soffice";
        }

        var pathCommand = FindCommandOnPath(OperatingSystem.IsWindows() ? "soffice.exe" : "soffice");
        if (pathCommand is not null)
        {
            yield return pathCommand;
        }
    }

    public static string? FindCommandOnPath(string command)
    {
        if (Path.IsPathFullyQualified(command) || command.Contains(Path.DirectorySeparatorChar))
        {
            return File.Exists(command) ? Path.GetFullPath(command) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : new[] { string.Empty };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, command);
                if (OperatingSystem.IsWindows()
                    && string.IsNullOrEmpty(Path.GetExtension(command)))
                {
                    candidate += extension;
                }

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> ExpandLibreOfficePath(string configured)
    {
        yield return configured;

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(configured, "program", "soffice.exe");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(configured, "Contents", "MacOS", "soffice");
            yield return Path.Combine(configured, "MacOS", "soffice");
        }
    }

    private static bool IsResourceRoot(string path) =>
        Directory.Exists(Path.Combine(path, "markitdown", "src"))
        || Directory.Exists(Path.Combine(path, "python"))
        || Directory.Exists(Path.Combine(path, "tessdata"));
}
