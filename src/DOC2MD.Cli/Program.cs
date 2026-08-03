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
            return args[0].ToLowerInvariant() switch
            {
                "convert" => await ConvertFileAsync(args, json),
                "convert-folder" => await ConvertFolderAsync(args, json),
                "configure-azure" => ConfigureAzure(args, json),
                "install-markitdown" => await InstallMarkItDownAsync(args, json),
                _ => Fail($"Unknown command '{args[0]}'.", json)
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, json);
        }
    }

    private static async Task<int> ConvertFileAsync(string[] args, bool json)
    {
        var input = Required(args, "--input");
        var output = Required(args, "--output");
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

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var prepared = await PrepareInputForConversionAsync(input);
        if (prepared.Skipped)
        {
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

        var result = await DocumentConversion.ConvertAsync(prepared.InputPath, output, conversionOptions, RunMarkItDownAsync);
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

    private static async Task<int> ConvertFolderAsync(string[] args, bool json)
    {
        var inputFolder = Required(args, "--input");
        var recursive = Has(args, "--recursive");
        var overwrite = Has(args, "--overwrite");
        var continueOnError = Has(args, "--continue-on-error");
        var conversionOptions = PdfConversionOptions.FromArgs(args);

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input folder was not found: {inputFolder}");
        }

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

                result = await DocumentConversion.ConvertAsync(prepared.InputPath, output, conversionOptions, RunMarkItDownAsync);
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

    private static async Task<int> InstallMarkItDownAsync(string[] args, bool json)
    {
        var python = Value(args, "--python") ?? "python";
        var root = FindRepoRoot();
        var packagePath = Path.Combine(root, "lib", "packages", "markitdown");
        if (!Directory.Exists(packagePath))
        {
            throw new DirectoryNotFoundException($"MarkItDown source package was not found: {packagePath}");
        }

        var venv = Path.Combine(root, ".markitdown-venv");
        var create = await RunProcessAsync(python, $"-m venv {Quote(venv)}", root);
        if (create.ExitCode != 0)
        {
            WriteResult(json, new { succeeded = false, step = "venv", exitCode = create.ExitCode, create.stderr });
            return create.ExitCode;
        }

        var venvPython = Path.Combine(venv, "Scripts", "python.exe");
        var install = await RunProcessAsync(venvPython, $"-m pip install -e {Quote(packagePath + "[all]")}", root);
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

    private static int ConfigureAzure(string[] args, bool json)
    {
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
            key = "stored-with-windows-dpapi",
            settingsPath = Doc2MdConfiguration.SettingsPath
        });
        return 0;
    }

    private static string[] SelectFolderConversionInputs(IEnumerable<string> files) =>
        files.GroupBy(file => Path.ChangeExtension(file, ".md"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => ModernizedExtensionBySourceExtension.ContainsKey(Path.GetExtension(file)) ? 1 : 0)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private static async Task<ModernizationResult> ModernizeWithLibreOfficeAsync(string input, string modernizedPath, string targetExtension)
    {
        var soffice = ResolveSofficePath();
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(input)) ?? Directory.GetCurrentDirectory();
        var targetFormat = targetExtension.TrimStart('.');
        var result = await RunProcessAsync(
            soffice,
            $"--headless --convert-to {targetFormat} --outdir {Quote(sourceDirectory)} {Quote(input)}",
            sourceDirectory);

        if (result.ExitCode == -1)
        {
            return ModernizationResult.Skipped(
                $"LibreOffice is not available, so '{input}' was not modernized to {targetExtension} and was skipped. " +
                "Install LibreOffice or set DOC2MD_SOFFICE_PATH to soffice.exe to enable legacy document conversion.");
        }

        if (result.ExitCode != 0 || !File.Exists(modernizedPath))
        {
            var stderr = string.IsNullOrWhiteSpace(result.stderr) ? "No stderr was returned." : result.stderr.Trim();
            throw new InvalidOperationException(
                $"LibreOffice could not modernize '{input}' to '{modernizedPath}'. " +
                $"Install LibreOffice or set DOC2MD_SOFFICE_PATH to soffice.exe. {stderr}");
        }

        return ModernizationResult.Success();
    }

    private static string ResolveSofficePath()
    {
        var configured = Environment.GetEnvironmentVariable("DOC2MD_SOFFICE_PATH")
            ?? Environment.GetEnvironmentVariable("DOC2MD_LIBREOFFICE_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            var configuredDirectoryCandidate = Path.Combine(configured, "program", "soffice.exe");
            if (File.Exists(configuredDirectoryCandidate))
            {
                return configuredDirectoryCandidate;
            }

            return configured;
        }

        var candidates = new[]
        {
            Path.Combine(FindRepoRoot(), "runtime", "libreoffice", "program", "soffice.exe"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path.EndsWith("soffice.exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : Path.Combine(path, "LibreOffice", "program", "soffice.exe"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "soffice";
    }

    private static async Task<(int ExitCode, string stdout, string stderr)> RunMarkItDownAsync(string input, string output)
    {
        var root = FindRepoRoot();
        var configured = Environment.GetEnvironmentVariable("DOC2MD_MARKITDOWN_COMMAND");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var result = await RunProcessAsync(configured, $"{Quote(input)} -o {Quote(output)}", root);
            return FilterBenignMarkItDownWarnings(input, result);
        }

        var venvPython = Path.Combine(root, ".markitdown-venv", "Scripts", "python.exe");
        if (File.Exists(venvPython))
        {
            return await RunPythonMarkItDownAsync(venvPython, input, output, root);
        }

        var pathResult = await RunProcessAsync("markitdown", $"{Quote(input)} -o {Quote(output)}", root);
        if (pathResult.ExitCode != -1)
        {
            return FilterBenignMarkItDownWarnings(input, pathResult);
        }

        return await RunPythonMarkItDownAsync("python", input, output, root);
    }

    private static async Task<(int ExitCode, string stdout, string stderr)> RunPythonMarkItDownAsync(string python, string input, string output, string root)
    {
        var source = Environment.GetEnvironmentVariable("DOC2MD_MARKITDOWN_SOURCE")
            ?? Path.Combine(root, "lib", "packages", "markitdown", "src");

        var result = await RunProcessAsync(
            python,
            $"-m markitdown {Quote(input)} -o {Quote(output)}",
            root,
            new Dictionary<string, string?> { ["PYTHONPATH"] = source });
        return FilterBenignMarkItDownWarnings(input, result);
    }

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

    private static bool IsBenignPydubFfmpegWarningLine(string line) =>
        line.Contains("RuntimeWarning: Couldn't find ffmpeg or avconv - defaulting to ffmpeg, but may not work", StringComparison.Ordinal)
        || line.Contains("warn(\"Couldn't find ffmpeg or avconv - defaulting to ffmpeg, but may not work\", RuntimeWarning)", StringComparison.Ordinal);

    private static async Task<(int ExitCode, string stdout, string stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (environment is not null)
            {
                foreach (var item in environment)
                {
                    process.StartInfo.Environment[item.Key] = item.Value;
                }
            }

            process.Start();
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

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "lib", "packages", "markitdown", "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string Required(string[] args, string name) =>
        Value(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool Has(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static int Fail(string message, bool json)
    {
        WriteResult(json, new { succeeded = false, error = message });
        return 1;
    }

    private static void WriteResult(bool json, object result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = json }));
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string SupportedExtensionsLabel() =>
        string.Join(", ", SupportedExtensions.OrderBy(extension => extension, StringComparer.OrdinalIgnoreCase));

    private sealed record PreparedInput(
        string InputPath,
        string? ModernizedPath,
        string? Modernization,
        bool Skipped,
        string? Warning)
    {
        public static PreparedInput Ready(string inputPath, string? ModernizedPath, string? Modernization) =>
            new(inputPath, ModernizedPath, Modernization, Skipped: false, Warning: null);

        public static PreparedInput SkippedWithWarning(string inputPath, string warning) =>
            new(inputPath, ModernizedPath: null, Modernization: null, Skipped: true, Warning: warning);
    }

    private sealed record ModernizationResult(bool Succeeded, string? Warning)
    {
        public static ModernizationResult Success() => new(Succeeded: true, Warning: null);

        public static ModernizationResult Skipped(string warning) => new(Succeeded: false, warning);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
DOC2MD.Cli

Commands:
  convert --input <file> --output <markdown> [--overwrite] [--json] [PDF options]
  convert-folder --input <folder> [--recursive] [--overwrite] [--continue-on-error] [--json] [PDF options]
  configure-azure --endpoint <url> [--tier <f0|s0>] [--json]
  install-markitdown [--python <python-exe>] [--json]

Folder conversion document extensions:
  .pdf, .doc, .docx, .docm, .xls, .xlsx, .xlsm, .ppt, .pptx,
  .pptm, .rtf, .odt, .ods, .odp, .txt, .text, .csv, .html,
  .htm, .epub

Legacy and OpenDocument modernization:
  .doc, .docm, .rtf, .odt -> .docx
  .xls, .xlsm, .ods       -> .xlsx
  .ppt, .pptm, .odp       -> .pptx
  Uses LibreOffice headless by default. If soffice.exe is not present, these
  old-format files are skipped with warnings and the batch continues.
  Set DOC2MD_SOFFICE_PATH to a soffice.exe path or LibreOffice installation root.

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
  supplied, protects it with Windows DPAPI for the current user, and stores it
  outside the repository. The configured key is then used automatically.

Environment equivalents:
  DOC2MD_PDF_PROCESSING, DOC2MD_OCR_LANGUAGES, DOC2MD_TESSDATA_PATH,
  DOC2MD_PDF_TEXT_THRESHOLD, DOC2MD_PDF_RENDER_DPI, DOC2MD_PDF_SPLITTING,
  DOC2MD_PDF_MAX_PAGES_PER_PART, DOC2MD_PDF_MAX_PART_SIZE_MB,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_LOCALE,
  DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_TIER

MarkItDown resolution on Windows:
  1. DOC2MD_MARKITDOWN_COMMAND, if set
  2. .markitdown-venv\Scripts\python.exe created by install-markitdown
  3. markitdown on PATH
  4. python -m markitdown with PYTHONPATH pointed at lib\packages\markitdown\src
""");
    }
}
