using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

[Collection("DOC2MD sample conversions")]
public sealed class SampleConversionIntegrationTests
{
    [SampleIntegrationFact]
    public async Task ConvertsEnglishCvFromDocx()
    {
        var result = await ConvertSampleAsync("example-cv.docx");

        Assert.Equal("markitdown", result.Converter);
        Assert.True(result.Markdown.Length > 1_000, "The converted CV was unexpectedly short.");
        Assert.Contains("Registered nurse", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Education", result.Markdown, StringComparison.OrdinalIgnoreCase);
    }

    [SampleIntegrationFact]
    public async Task ConvertsEnglishImagePdfWithNativeOcr()
    {
        var result = await ConvertSampleAsync(
            "examples-download.pdf",
            "--pdf-processing", "local",
            "--ocr-languages", "eng");

        Assert.Equal("local-pdf-inspection-ocr", result.Converter);
        Assert.Contains("15 OCR page(s)", result.Inspection, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Markdown.Length > 5_000, "The OCR result was unexpectedly short.");
        Assert.Contains("Word document templates", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Download", result.Markdown, StringComparison.OrdinalIgnoreCase);
    }

    [SampleIntegrationFact]
    public async Task ConvertsPolishTextPdfWithDiacritics()
    {
        var result = await ConvertSampleAsync(
            "example-ebook.pdf",
            "--pdf-processing", "local",
            "--ocr-languages", "eng");

        Assert.Equal("markitdown", result.Converter);
        Assert.Contains("183 page(s)", result.Inspection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 OCR page(s)", result.Inspection, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Markdown.Length > 100_000, "The converted ebook was unexpectedly short.");
        Assert.Contains("Anatomia", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("negocjacji", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("polskim biznesie", result.Markdown, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ConversionResult> ConvertSampleAsync(string sampleName, params string[] extraArguments)
    {
        var repositoryRoot = FindRepositoryRoot();
        var samplePath = Path.Combine(repositoryRoot, "samples", sampleName);
        Assert.True(File.Exists(samplePath), $"The sample file was not found: {samplePath}");

        using var outputFolder = new TemporaryFolder();
        var outputPath = Path.Combine(outputFolder.Path, Path.ChangeExtension(sampleName, ".md"));
        var arguments = new List<string>
        {
            "convert",
            "--input", Path.Combine("samples", sampleName),
            "--output", outputPath,
            "--overwrite",
            "--json"
        };
        arguments.AddRange(extraArguments);

        var configuredTessdata = Environment.GetEnvironmentVariable("DOC2MD_SAMPLE_TEST_TESSDATA");
        if (!string.IsNullOrWhiteSpace(configuredTessdata) && extraArguments.Contains("--pdf-processing"))
        {
            arguments.Add("--tessdata");
            arguments.Add(configuredTessdata);
        }

        var processResult = await RunCliAsync(repositoryRoot, arguments);
        Assert.True(
            processResult.ExitCode == 0,
            $"DOC2MD failed for {sampleName} with exit code {processResult.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{processResult.Stdout}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{processResult.Stderr}");
        Assert.True(File.Exists(outputPath), $"DOC2MD did not create the expected output: {outputPath}");

        using var json = JsonDocument.Parse(processResult.Stdout);
        var root = json.RootElement;
        Assert.True(root.GetProperty("succeeded").GetBoolean());

        return new ConversionResult(
            root.GetProperty("converter").GetString() ?? string.Empty,
            root.TryGetProperty("inspection", out var inspection) && inspection.ValueKind == JsonValueKind.String
                ? inspection.GetString() ?? string.Empty
                : string.Empty,
            await File.ReadAllTextAsync(outputPath));
    }

    private static async Task<ProcessResult> RunCliAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var configured = Environment.GetEnvironmentVariable("DOC2MD_SAMPLE_TEST_CLI");
        var executable = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "DOC2MD.Cli.exe" : "DOC2MD.Cli")
            : Path.GetFullPath(configured);
        Assert.True(File.Exists(executable), $"The DOC2MD CLI executable was not found: {executable}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"DOC2MD did not finish within five minutes: {executable}");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DOC2MD.slnx"))
                && Directory.Exists(Path.Combine(current.FullName, "samples")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the DOC2MD repository containing the samples folder.");
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed record ConversionResult(string Converter, string Inspection, string Markdown);

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"DOC2MD.Integration.Tests.{Guid.NewGuid():N}"))
                .FullName;
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

public sealed class SampleIntegrationFactAttribute : FactAttribute
{
    public SampleIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DOC2MD_RUN_SAMPLE_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set DOC2MD_RUN_SAMPLE_TESTS=1 to run tests requiring LibreOffice, Python/MarkItDown, and Tesseract.";
        }
    }
}

[CollectionDefinition("DOC2MD sample conversions", DisableParallelization = true)]
public sealed class SampleConversionCollection;
