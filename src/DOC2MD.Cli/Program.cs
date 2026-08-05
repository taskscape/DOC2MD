using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = Encoding.UTF8;

return await Cli.RunAsync(args);

internal static class Cli
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc",
        ".docx",
        ".docm",
        ".xlsx",
        ".xls",
        ".xlsm",
        ".pptx",
        ".ppt",
        ".pptm",
        ".rtf",
        ".odt",
        ".ods",
        ".odp",
        ".txt",
        ".text",
        ".csv",
        ".html",
        ".htm",
        ".epub"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a"
    };

    private static readonly Dictionary<string, string> ModernizedExtensionBySourceExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = ".docx",
        [".docm"] = ".docx",
        [".rtf"] = ".docx",
        [".odt"] = ".docx",
        [".xls"] = ".xlsx",
        [".xlsm"] = ".xlsx",
        [".ods"] = ".xlsx",
        [".ppt"] = ".pptx",
        [".pptm"] = ".pptx",
        [".odp"] = ".pptx"
    };

    /// <summary>
    /// Dispatches the requested CLI command and translates unhandled command errors into a stable process exit code.
    /// </summary>
    /// <param name="args">The command-line arguments supplied to DOC2MD.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || Has(args, "--help") || Has(args, "-h"))
        {
            PrintHelp();
            return 0;
        }

        var json = Has(args, "--json");
        try
        {
            if (args[0].Equals("convert", StringComparison.OrdinalIgnoreCase)
                || args[0].Equals("convert-folder", StringComparison.OrdinalIgnoreCase))
            {
                await RequireLibreOfficeAsync();
            }

            return args[0].ToLowerInvariant() switch
            {
                "convert" => await ConvertFileAsync(args, json),
                "convert-folder" => await ConvertFolderAsync(args, json),
                "configure-azure" => ConfigureAzure(args, json),
                "install-markitdown" => await InstallMarkItDownAsync(args, json),
                "check-dependencies" => await CheckDependenciesAsync(json),
                _ => Fail($"Unknown command '{args[0]}'.", json)
            };
        }
        catch (Exception ex)
        {
            // The CLI is the process boundary, so command exceptions become user-facing errors rather than crash reports.
            return Fail(ex.Message, json);
        }
    }

    /// <summary>
    /// Converts one supported source document to the requested Markdown file.
    /// </summary>
    /// <param name="args">Arguments containing the input, output, overwrite, and PDF options.</param>
    /// <param name="json">Whether to format the result as indented JSON.</param>
    /// <returns>The converter exit code.</returns>
    private static async Task<int> ConvertFileAsync(string[] args, bool json)
    {
        var input = Path.GetFullPath(Required(args, "--input"));
        var output = Path.GetFullPath(Required(args, "--output"));
        var overwrite = Has(args, "--overwrite");
        var conversionOptions = PdfConversionOptions.FromArgs(args);

        if (!File.Exists(input))
        {
            throw new FileNotFoundException("Input file was not found.", input);
        }

        if (!SupportedExtensions.Contains(Path.GetExtension(input)))
        {
            throw new ArgumentException(
                $"Unsupported document extension '{Path.GetExtension(input)}'. Supported extensions: {SupportedExtensionsLabel()}.");
        }

        if (File.Exists(output) && !overwrite)
        {
            throw new IOException($"Output file already exists: {output}. Use --overwrite to replace it.");
        }

        var prepared = await PrepareInputForConversionAsync(input);
        if (prepared.Skipped)
        {
            // Retain a structured per-file result if LibreOffice disappears after the command-level dependency check.
            WriteResult(json, new
            {
                succeeded = false,
                skipped = true,
                input = Path.GetFullPath(input),
                output = Path.GetFullPath(output),
                warning = prepared.Warning,
                exitCode = 0,
                stderr = string.Empty
            });
            return 0;
        }

        var result = await ConvertAtomicallyAsync(
            prepared.InputPath,
            output,
            overwrite,
            conversionOptions);
        WriteResult(json, new
        {
            succeeded = result.ExitCode == 0,
            input = Path.GetFullPath(input),
            convertedInput = Path.GetFullPath(prepared.InputPath),
            modernizedInput = prepared.ModernizedPath is null ? null : Path.GetFullPath(prepared.ModernizedPath),
            modernization = prepared.Modernization,
            output = Path.GetFullPath(output),
            converter = prepared.ModernizedPath is null ? result.Converter : $"libreoffice-modernize+{result.Converter}",
            inspection = result.InspectionSummary,
            exitCode = result.ExitCode,
            stderr = result.Stderr
        });
        return result.ExitCode == 0 ? 0 : result.ExitCode;
    }

    /// <summary>
    /// Converts supported documents in a folder while preserving per-file outcomes for batch callers.
    /// </summary>
    /// <param name="args">Arguments containing folder traversal, overwrite, error-continuation, and PDF options.</param>
    /// <param name="json">Whether to format the aggregate result as indented JSON.</param>
    /// <returns>Zero when all attempted conversions succeed; otherwise one.</returns>
    private static async Task<int> ConvertFolderAsync(string[] args, bool json)
    {
        var inputFolder = Path.GetFullPath(Required(args, "--input"));
        var recursive = Has(args, "--recursive");
        var overwrite = Has(args, "--overwrite");
        var continueOnError = Has(args, "--continue-on-error");
        var conversionOptions = PdfConversionOptions.FromArgs(args);

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input folder was not found: {inputFolder}");
        }

        // Selection is centralized so a legacy source and its modern sibling never generate competing Markdown outputs.
        var files = SelectFolderConversionInputs(Directory.EnumerateFiles(inputFolder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetExtension(f).Equals(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        var items = new List<object>();
        var failures = 0;

        foreach (var file in files)
        {
            var output = Path.ChangeExtension(file, ".md");
            if (File.Exists(output) && !overwrite)
            {
                items.Add(new { succeeded = false, skipped = true, input = Path.GetFullPath(file), output = Path.GetFullPath(output), error = "Output exists." });
                continue;
            }

            PreparedInput prepared;
            DocumentConversionResult result;
            try
            {
                prepared = await PrepareInputForConversionAsync(file);
                if (prepared.Skipped)
                {
                    items.Add(new
                    {
                        succeeded = false,
                        skipped = true,
                        input = Path.GetFullPath(file),
                        output = Path.GetFullPath(output),
                        warning = prepared.Warning
                    });
                    continue;
                }

                result = await ConvertAtomicallyAsync(
                    prepared.InputPath,
                    output,
                    overwrite,
                    conversionOptions);
            }
            catch (Exception ex)
            {
                failures++;
                items.Add(new
                {
                    succeeded = false,
                    input = Path.GetFullPath(file),
                    output = Path.GetFullPath(output),
                    error = ex.Message
                });

                if (!continueOnError)
                {
                    break;
                }

                continue;
            }

            var succeeded = result.ExitCode == 0;
            if (!succeeded)
            {
                failures++;
            }

            items.Add(new
            {
                succeeded,
                input = Path.GetFullPath(file),
                convertedInput = Path.GetFullPath(prepared.InputPath),
                modernizedInput = prepared.ModernizedPath is null ? null : Path.GetFullPath(prepared.ModernizedPath),
                modernization = prepared.Modernization,
                output = Path.GetFullPath(output),
                converter = prepared.ModernizedPath is null ? result.Converter : $"libreoffice-modernize+{result.Converter}",
                inspection = result.InspectionSummary,
                exitCode = result.ExitCode,
                stderr = result.Stderr
            });

            if (!succeeded && !continueOnError)
            {
                break;
            }
        }

        WriteResult(json, new
        {
            succeeded = failures == 0,
            input = Path.GetFullPath(inputFolder),
            recursive,
            total = files.Length,
            failures,
            items
        });
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Creates the repository-local Python environment and installs the vendored MarkItDown package with all extras.
    /// </summary>
    /// <param name="args">Arguments optionally selecting the Python executable.</param>
    /// <param name="json">Whether to format the installation result as indented JSON.</param>
    /// <returns>The failing subprocess exit code, or zero after a successful installation.</returns>
    private static async Task<int> InstallMarkItDownAsync(string[] args, bool json)
    {
        var python = Value(args, "--python") ?? ResolvePythonForEnvironmentCreation();
        var root = ApplicationPaths.ResourceRoot;
        var packagePath = ApplicationPaths.MarkItDownPackageRoot;
        if (!Directory.Exists(packagePath))
        {
            throw new DirectoryNotFoundException($"MarkItDown source package was not found: {packagePath}");
        }

        var version = await RunProcessAsync(
            python,
            ["-c", "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"],
            root);
        if (version.ExitCode != 0 || !IsSupportedPythonVersion(version.stdout))
        {
            throw new InvalidOperationException(
                $"MarkItDown requires Python 3.10 or newer. Could not use '{python}'. {version.stderr.Trim()}");
        }

        var venv = ApplicationPaths.UserMarkItDownVenvRoot;
        Directory.CreateDirectory(ApplicationPaths.UserRuntimeRoot);
        var create = await RunProcessAsync(python, ["-m", "venv", venv], root);
        if (create.ExitCode != 0)
        {
            WriteResult(json, new { succeeded = false, step = "venv", exitCode = create.ExitCode, create.stderr });
            return create.ExitCode;
        }

        var venvPython = ApplicationPaths.GetVirtualEnvironmentPython(venv);
        var install = await RunProcessAsync(
            venvPython,
            ["-m", "pip", "install", "-e", packagePath + "[all]"],
            root);
        WriteResult(json, new
        {
            succeeded = install.ExitCode == 0,
            venv,
            python = venvPython,
            step = "pip-install",
            exitCode = install.ExitCode,
            install.stderr
        });
        return install.ExitCode;
    }

    /// <summary>
    /// Validates and securely persists Azure Document Intelligence defaults for the current user.
    /// </summary>
    /// <param name="args">Arguments containing endpoint, credential, locale, tier, and default-mode choices.</param>
    /// <param name="json">Whether to format the configuration result as indented JSON.</param>
    /// <returns>Zero after the settings are stored.</returns>
    private static int ConfigureAzure(string[] args, bool json)
    {
        // Command-line aliases take precedence, then environment variables, then the existing per-user settings.
        var configured = Doc2MdConfiguration.Load();
        var endpoint = Value(args, "--azure-document-intelligence-endpoint")
            ?? Value(args, "--azure-endpoint")
            ?? Value(args, "--endpoint")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT");
        var key = Value(args, "--azure-document-intelligence-key")
            ?? Value(args, "--azure-key")
            ?? Value(args, "--key")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY");
        var locale = Value(args, "--azure-document-intelligence-locale")
            ?? Value(args, "--azure-locale")
            ?? Value(args, "--locale")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_LOCALE");
        var tier = Value(args, "--azure-document-intelligence-tier")
            ?? Value(args, "--azure-tier")
            ?? Value(args, "--tier")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_TIER")
            ?? "s0";
        var useAzureByDefault = !Has(args, "--do-not-use-azure-by-default");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("Missing Azure Document Intelligence endpoint.");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Missing Azure Document Intelligence key.");
        }

        if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Azure Document Intelligence endpoint must be an HTTPS URL.");
        }

        if (!string.Equals(tier, "s0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tier, "f0", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Azure Document Intelligence tier must be one of: f0, s0.");
        }

        Doc2MdConfiguration.SaveAzure(endpoint, key, locale, tier.ToLowerInvariant(), useAzureByDefault);
        WriteResult(json, new
        {
            succeeded = true,
            pdfProcessing = useAzureByDefault ? "azure" : "unchanged",
            endpoint,
            tier = tier.ToLowerInvariant(),
            key = $"stored-with-{Doc2MdConfiguration.SecretStorageDescription}",
            settingsPath = Doc2MdConfiguration.SettingsPath
        });
        return 0;
    }

    /// <summary>
    /// Selects one source per eventual Markdown path, preferring an original legacy document over its modern sibling.
    /// </summary>
    /// <param name="files">The candidate source files.</param>
    /// <returns>A deterministic, case-insensitively ordered source list.</returns>
    private static string[] SelectFolderConversionInputs(IEnumerable<string> files) =>
        files.GroupBy(file => Path.ChangeExtension(file, ".md"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => ModernizedExtensionBySourceExtension.ContainsKey(Path.GetExtension(file)) ? 1 : 0)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Converts one prepared document without exposing a partial final output.
    /// </summary>
    /// <param name="input">The prepared input path.</param>
    /// <param name="output">The requested final Markdown path.</param>
    /// <param name="overwrite">Whether an existing final output may be replaced.</param>
    /// <param name="conversionOptions">The selected PDF conversion behavior.</param>
    /// <returns>The underlying document conversion result.</returns>
    private static Task<DocumentConversionResult> ConvertAtomicallyAsync(
        string input,
        string output,
        bool overwrite,
        PdfConversionOptions conversionOptions) =>
        AtomicFileOutput.WriteAsync(
            output,
            overwrite,
            temporaryOutput => DocumentConversion.ConvertAsync(
                input,
                temporaryOutput,
                conversionOptions,
                RunMarkItDownAsync),
            result => result.ExitCode == 0);

    /// <summary>
    /// Resolves a directly convertible input or prepares a modern Office-format copy with LibreOffice.
    /// </summary>
    /// <param name="input">The source document path.</param>
    /// <returns>A ready input descriptor or a non-fatal skipped descriptor.</returns>
    private static async Task<PreparedInput> PrepareInputForConversionAsync(string input)
    {
        var extension = Path.GetExtension(input);
        if (!ModernizedExtensionBySourceExtension.TryGetValue(extension, out var targetExtension))
        {
            return PreparedInput.Ready(input, ModernizedPath: null, Modernization: null);
        }

        var modernizedPath = Path.ChangeExtension(input, targetExtension);
        if (File.Exists(modernizedPath))
        {
            // Existing siblings are user-owned artifacts and are reused rather than overwritten by automatic modernization.
            return PreparedInput.Ready(
                modernizedPath,
                modernizedPath,
                $"Used existing modernized {targetExtension} copy created beside the source document.");
        }

        var modernization = await ModernizeWithLibreOfficeAsync(input, modernizedPath, targetExtension);
        if (!modernization.Succeeded)
        {
            return PreparedInput.SkippedWithWarning(input, modernization.Warning!);
        }

        return PreparedInput.Ready(
            modernizedPath,
            modernizedPath,
            $"Modernized {extension} to {targetExtension} with LibreOffice before Markdown conversion.");
    }

    /// <summary>
    /// Runs LibreOffice headlessly to create a modern Office document beside a legacy source.
    /// </summary>
    /// <param name="input">The legacy source path.</param>
    /// <param name="modernizedPath">The path LibreOffice is expected to produce.</param>
    /// <param name="targetExtension">The modern target extension.</param>
    /// <returns>A success result, or a skipped result when LibreOffice cannot be started.</returns>
    private static async Task<ModernizationResult> ModernizeWithLibreOfficeAsync(string input, string modernizedPath, string targetExtension)
    {
        var soffice = ResolveLibreOfficePath();
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(input)) ?? Directory.GetCurrentDirectory();
        var targetFormat = targetExtension.TrimStart('.');
        var result = await RunProcessAsync(
            soffice,
            ["--headless", "--convert-to", targetFormat, "--outdir", sourceDirectory, input],
            sourceDirectory);

        if (result.ExitCode == -1)
        {
            // RunProcessAsync reserves -1 for process-discovery failures, which are optional for non-legacy inputs.
            return ModernizationResult.Skipped(
                $"LibreOffice is not available, so '{input}' was not modernized to {targetExtension} and was skipped. " +
                "Install LibreOffice or set DOC2MD_SOFFICE_PATH to its soffice executable.");
        }

        if (result.ExitCode != 0 || !File.Exists(modernizedPath))
        {
            var stderr = string.IsNullOrWhiteSpace(result.stderr) ? "No stderr was returned." : result.stderr.Trim();
            throw new InvalidOperationException(
                $"LibreOffice could not modernize '{input}' to '{modernizedPath}'. " +
                $"Install LibreOffice or set DOC2MD_SOFFICE_PATH to its soffice executable. {stderr}");
        }

        return ModernizationResult.Success();
    }

    /// <summary>
    /// Resolves LibreOffice from explicit configuration, the Windows payload, or conventional platform locations.
    /// </summary>
    /// <returns>The discovered LibreOffice executable path.</returns>
    private static string ResolveLibreOfficePath()
    {
        return ApplicationPaths.LibreOfficeExecutableCandidates().FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "LibreOffice is required by DOC2MD but was not detected. Install LibreOffice or set " +
                "DOC2MD_SOFFICE_PATH to the soffice executable or LibreOffice installation root.");
    }

    private static async Task<string> RequireLibreOfficeAsync()
    {
        var executable = ResolveLibreOfficePath();
        var result = await RunProcessAsync(executable, ["--headless", "--version"], ApplicationPaths.ResourceRoot);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.stderr) ? result.stdout : result.stderr;
            throw new InvalidOperationException(
                $"LibreOffice was detected at '{executable}' but could not start. {detail.Trim()}");
        }

        return executable;
    }

    private static async Task<int> CheckDependenciesAsync(bool json)
    {
        var libreOffice = await RequireLibreOfficeAsync();
        var python = ResolveAvailablePython();
        var tesseract = ApplicationPaths.TesseractExecutableCandidates().FirstOrDefault(File.Exists);

        WriteResult(json, new
        {
            succeeded = true,
            resourceRoot = ApplicationPaths.ResourceRoot,
            libreOffice,
            python,
            tesseract,
            markItDownSource = ApplicationPaths.MarkItDownSourceRoot,
            tessdata = ApplicationPaths.BundledTessdataRoot
        });
        return 0;
    }

    /// <summary>
    /// Runs MarkItDown using the first available command from DOC2MD's documented resolution order.
    /// </summary>
    /// <param name="input">The source document path.</param>
    /// <param name="output">The Markdown output path.</param>
    /// <returns>The child-process exit code and captured output streams.</returns>
    private static async Task<(int ExitCode, string stdout, string stderr)> RunMarkItDownAsync(string input, string output)
    {
        // Resolution favors explicit and application-bundled runtimes so installed builds do not depend on global Python state.
        var root = ApplicationPaths.ResourceRoot;
        var configured = Environment.GetEnvironmentVariable("DOC2MD_MARKITDOWN_COMMAND");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var result = await RunProcessAsync(configured, [input, "-o", output], root);
            return FilterBenignMarkItDownWarnings(input, result);
        }

        foreach (var bundledPython in ApplicationPaths.BundledPythonCandidates().Where(File.Exists))
        {
            var result = await RunPythonMarkItDownAsync(bundledPython, input, output, root);
            if (result.ExitCode != -1)
            {
                return result;
            }
        }

        var userVenvPython = ApplicationPaths.GetVirtualEnvironmentPython(ApplicationPaths.UserMarkItDownVenvRoot);
        if (File.Exists(userVenvPython))
        {
            return await RunPythonMarkItDownAsync(userVenvPython, input, output, root);
        }

        var pathResult = await RunProcessAsync("markitdown", [input, "-o", output], root);
        if (pathResult.ExitCode != -1)
        {
            return FilterBenignMarkItDownWarnings(input, pathResult);
        }

        foreach (var python in SystemPythonCandidates())
        {
            var result = await RunPythonMarkItDownAsync(python, input, output, root);
            if (result.ExitCode != -1)
            {
                return result;
            }
        }

        return (-1, string.Empty, "Python 3.10 or newer with MarkItDown was not found. Reinstall DOC2MD or run install-markitdown.");
    }

    /// <summary>
    /// Executes the MarkItDown module with the vendored source directory exposed through <c>PYTHONPATH</c>.
    /// </summary>
    /// <param name="python">The Python executable or command name.</param>
    /// <param name="input">The source document path.</param>
    /// <param name="output">The Markdown output path.</param>
    /// <param name="root">The DOC2MD repository or installation root.</param>
    /// <returns>The child-process exit code and captured output streams.</returns>
    private static async Task<(int ExitCode, string stdout, string stderr)> RunPythonMarkItDownAsync(string python, string input, string output, string root)
    {
        // The installer retains vendored source at the same relative path used by development checkouts.
        var source = Environment.GetEnvironmentVariable("DOC2MD_MARKITDOWN_SOURCE")
            ?? ApplicationPaths.MarkItDownSourceRoot;

        var result = await RunProcessAsync(
            python,
            ["-m", "markitdown", input, "-o", output],
            root,
            new Dictionary<string, string?> { ["PYTHONPATH"] = source });
        return FilterBenignMarkItDownWarnings(input, result);
    }

    /// <summary>
    /// Removes the known pydub FFmpeg warning when converting document types that cannot require audio decoding.
    /// </summary>
    /// <param name="input">The source path used to distinguish audio from document inputs.</param>
    /// <param name="result">The original MarkItDown process result.</param>
    /// <returns>The result with only the irrelevant warning lines removed.</returns>
    private static (int ExitCode, string stdout, string stderr) FilterBenignMarkItDownWarnings(
        string input,
        (int ExitCode, string stdout, string stderr) result)
    {
        if (result.ExitCode != 0
            || string.IsNullOrWhiteSpace(result.stderr)
            || AudioExtensions.Contains(Path.GetExtension(input)))
        {
            return result;
        }

        // Preserve the warning for audio inputs because FFmpeg is then a real runtime dependency rather than optional noise.
        var filtered = new StringBuilder();
        using var reader = new StringReader(result.stderr);
        while (reader.ReadLine() is { } line)
        {
            if (IsBenignPydubFfmpegWarningLine(line))
            {
                continue;
            }

            filtered.AppendLine(line);
        }

        return (result.ExitCode, result.stdout, filtered.ToString());
    }

    /// <summary>
    /// Identifies either line emitted by pydub's two-line missing-FFmpeg warning.
    /// </summary>
    /// <param name="line">A standard-error line.</param>
    /// <returns><see langword="true"/> when the line belongs to the known warning.</returns>
    private static bool IsBenignPydubFfmpegWarningLine(string line) =>
        line.Contains("RuntimeWarning: Couldn't find ffmpeg or avconv - defaulting to ffmpeg, but may not work", StringComparison.Ordinal)
        || line.Contains("warn(\"Couldn't find ffmpeg or avconv - defaulting to ffmpeg, but may not work\", RuntimeWarning)", StringComparison.Ordinal);

    /// <summary>
    /// Runs a child process without a shell and captures its UTF-8 output streams.
    /// </summary>
    /// <param name="fileName">The executable path or command name.</param>
    /// <param name="arguments">The individual command-line arguments.</param>
    /// <param name="workingDirectory">The child process working directory.</param>
    /// <param name="environment">Optional environment overrides.</param>
    /// <returns>The process result; exit code -1 indicates that the operating system could not start the executable.</returns>
    private static async Task<(int ExitCode, string stdout, string stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (environment is not null)
            {
                foreach (var item in environment)
                {
                    process.StartInfo.Environment[item.Key] = item.Value;
                }
            }

            process.Start();
            // Read both redirected streams before waiting so a full pipe cannot deadlock the child process.
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }

    private static string ResolvePythonForEnvironmentCreation() =>
        ResolveAvailablePython()
        ?? throw new FileNotFoundException("Python 3.10 or newer was not found. Set --python to a compatible Python executable.");

    private static string? ResolveAvailablePython() =>
        ApplicationPaths.BundledPythonCandidates()
            .Concat(new[] { ApplicationPaths.GetVirtualEnvironmentPython(ApplicationPaths.UserMarkItDownVenvRoot) })
            .Concat(SystemPythonCandidates().Select(candidate => ApplicationPaths.FindCommandOnPath(candidate) ?? candidate))
            .FirstOrDefault(candidate => File.Exists(candidate) || ApplicationPaths.FindCommandOnPath(candidate) is not null);

    private static IEnumerable<string> SystemPythonCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "python";
            yield return "py";
        }
        else
        {
            yield return "python3";
            yield return "python";
        }
    }

    private static bool IsSupportedPythonVersion(string versionText)
    {
        var version = versionText.Trim();
        return Version.TryParse(version, out var parsed)
            && (parsed.Major > 3 || parsed is { Major: 3, Minor: >= 10 });
    }

    /// <summary>
    /// Reads a required option value.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="name">The option name.</param>
    /// <returns>The option value.</returns>
    /// <exception cref="ArgumentException">Thrown when the option is absent.</exception>
    private static string Required(string[] args, string name) =>
        Value(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    /// <summary>
    /// Reads the value immediately following a named command-line option.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="name">The option name.</param>
    /// <returns>The option value, or <see langword="null"/> when absent.</returns>
    private static string? Value(string[] args, string name)
    {
        // Options use a deliberately small name/value parser; combined or equals syntax is not part of the CLI contract.
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Determines whether a case-insensitive flag is present.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="name">The flag name.</param>
    /// <returns><see langword="true"/> when the flag is present.</returns>
    private static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a failed command result and returns the conventional nonzero exit code.
    /// </summary>
    /// <param name="message">The user-facing error message.</param>
    /// <param name="json">Whether to indent the JSON output.</param>
    /// <returns>Exit code one.</returns>
    private static int Fail(string message, bool json)
    {
        // All CLI output remains JSON-shaped so GUI, API, and MCP wrappers can consume the same error contract.
        WriteResult(json, new { succeeded = false, error = message });
        return 1;
    }

    /// <summary>
    /// Serializes a command result to standard output.
    /// </summary>
    /// <param name="json">Whether to use human-readable indentation.</param>
    /// <param name="result">The result payload.</param>
    private static void WriteResult(bool json, object result)
    {
        // Even non-indented mode emits JSON because machine-readable output is the wrapper integration contract.
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = json }));
    }

    /// <summary>
    /// Formats the supported extension set deterministically for diagnostics.
    /// </summary>
    /// <returns>A comma-separated extension list.</returns>
    private static string SupportedExtensionsLabel() =>
        string.Join(", ", SupportedExtensions.OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase));

    private sealed record PreparedInput(
        string InputPath,
        string? ModernizedPath,
        string? Modernization,
        bool Skipped,
        string? Warning)
    {
        /// <summary>
        /// Creates a descriptor for an input that is ready to convert.
        /// </summary>
        /// <param name="inputPath">The effective conversion input.</param>
        /// <param name="ModernizedPath">The generated or reused modern sibling, if any.</param>
        /// <param name="Modernization">A user-facing modernization description.</param>
        /// <returns>The ready descriptor.</returns>
        public static PreparedInput Ready(string inputPath, string? ModernizedPath, string? Modernization) =>
            new(inputPath, ModernizedPath, Modernization, Skipped: false, Warning: null);

        /// <summary>
        /// Creates a non-fatal descriptor for an input skipped because an optional capability is unavailable.
        /// </summary>
        /// <param name="inputPath">The original input path.</param>
        /// <param name="warning">The reason for skipping the input.</param>
        /// <returns>The skipped descriptor.</returns>
        public static PreparedInput SkippedWithWarning(string inputPath, string warning) =>
            new(inputPath, ModernizedPath: null, Modernization: null, Skipped: true, Warning: warning);
    }

    private sealed record ModernizationResult(bool Succeeded, string? Warning)
    {
        /// <summary>
        /// Creates a successful modernization result.
        /// </summary>
        /// <returns>The successful result.</returns>
        public static ModernizationResult Success() => new(Succeeded: true, Warning: null);

        /// <summary>
        /// Creates a skipped modernization result with a user-facing warning.
        /// </summary>
        /// <param name="warning">The reason modernization could not run.</param>
        /// <returns>The skipped result.</returns>
        public static ModernizationResult Skipped(string warning) => new(Succeeded: false, warning);
    }

    /// <summary>
    /// Writes command usage, supported formats, and configuration precedence to standard output.
    /// </summary>
    private static void PrintHelp()
    {
        // Keep this text synchronized with the parser and README because it is the offline operational reference.
        Console.WriteLine("""
DOC2MD.Cli

Commands:
  convert --input <file> --output <markdown> [--overwrite] [--json] [PDF options]
  convert-folder --input <folder> [--recursive] [--overwrite] [--continue-on-error] [--json] [PDF options]
  configure-azure --endpoint <url> [--tier <f0|s0>] [--json]
  install-markitdown [--python <python-exe>] [--json]
  check-dependencies [--json]

Folder conversion document extensions:
  .pdf, .doc, .docx, .docm, .xls, .xlsx, .xlsm, .ppt, .pptx,
  .pptm, .rtf, .odt, .ods, .odp, .txt, .text, .csv, .html,
  .htm, .epub

Legacy and OpenDocument modernization:
  .doc, .docm, .rtf, .odt -> .docx
  .xls, .xlsm, .ods       -> .xlsx
  .ppt, .pptm, .odp       -> .pptx
  LibreOffice is a required DOC2MD runtime dependency. Every conversion checks
  that its headless executable can start before processing any input.
  Set DOC2MD_SOFFICE_PATH to the soffice executable or LibreOffice installation root.

PDF options:
  --pdf-processing <local|azure|markitdown>
      local is the built-in default unless configure-azure or environment
      settings select azure. Local inspects PDFs with PdfPig, uses MarkItDown
      for fully extractable PDFs, and uses Tesseract OCR only for pages without
      extractable text.
  --ocr-languages <languages>              Default: eng+pol
  --tessdata <folder>                      Tesseract traineddata folder
  --pdf-text-threshold <chars>             Default: 40
  --pdf-render-dpi <dpi>                   Default: 300
  --pdf-splitting <auto|always|never>      Default: always
  --pdf-max-pages-per-part <pages>         Default: 100 (Azure F0 default: 2)
  --pdf-max-part-size-mb <mb>              Default: 100 (Azure F0 default: 3)
  --azure-document-intelligence-endpoint <url>
  --azure-document-intelligence-key <key>  Optional when DefaultAzureCredential is usable
  --azure-document-intelligence-locale <locale>
  --azure-document-intelligence-tier <f0|s0>

Secure Azure setup:
  configure-azure reads DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY when --key is not
  supplied, protects it with Windows DPAPI or macOS Keychain for the current
  user, and stores only the protected reference in DOC2MD settings.

Environment equivalents:
  DOC2MD_PDF_PROCESSING, DOC2MD_OCR_LANGUAGES, DOC2MD_TESSDATA_PATH,
  DOC2MD_PDF_TEXT_THRESHOLD, DOC2MD_PDF_RENDER_DPI, DOC2MD_PDF_SPLITTING,
  DOC2MD_PDF_MAX_PAGES_PER_PART, DOC2MD_PDF_MAX_PART_SIZE_MB,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_LOCALE,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_TIER, DOC2MD_RESOURCE_ROOT,
  DOC2MD_MARKITDOWN_COMMAND, DOC2MD_MARKITDOWN_SOURCE,
  DOC2MD_TESSERACT_PATH, DOC2MD_SOFFICE_PATH

MarkItDown resolution:
  1. DOC2MD_MARKITDOWN_COMMAND, if set
  2. bundled Python below the platform-neutral DOC2MD resource root
  3. the per-user virtual environment created by install-markitdown
  4. markitdown on PATH
  5. python3/python -m markitdown with PYTHONPATH set to Resources/markitdown/src
""");
    }
}
