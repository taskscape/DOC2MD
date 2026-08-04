using System.Diagnostics;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/convert", async (ConvertDocumentRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.InputPath) || string.IsNullOrWhiteSpace(request.OutputPath))
    {
        return Results.BadRequest(new { error = "InputPath and OutputPath are required." });
    }

    // The API validates transport-level requirements; the CLI remains authoritative for formats and processing options.
    var args = $"convert --input {Quote(request.InputPath)} --output {Quote(request.OutputPath)} --json";
    if (request.Overwrite)
    {
        args += " --overwrite";
    }

    args += BuildPdfOptions(request);

    var result = await CliRunner.RunAsync(args, request.AzureDocumentIntelligenceKey);
    return CliRunner.ToHttpResult(result);
})
.WithName("ConvertDocument")
.WithOpenApi();

app.MapPost("/convert-folder", async (ConvertFolderRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.InputFolder))
    {
        return Results.BadRequest(new { error = "InputFolder is required." });
    }

    // Folder output stays implicit because the CLI writes Markdown beside each selected source document.
    var args = $"convert-folder --input {Quote(request.InputFolder)} --json";
    if (request.Recursive)
    {
        args += " --recursive";
    }

    if (request.Overwrite)
    {
        args += " --overwrite";
    }

    if (request.ContinueOnError)
    {
        args += " --continue-on-error";
    }

    args += BuildPdfOptions(request);

    var result = await CliRunner.RunAsync(args, request.AzureDocumentIntelligenceKey);
    return CliRunner.ToHttpResult(result);
})
.WithName("ConvertFolder")
.WithOpenApi();

app.Run();

// Quotes one CLI argument; top-level local functions cannot carry C# XML documentation comments.
static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

// Builds the shared PDF argument fragment while leaving cross-stack validation to the CLI.
static string BuildPdfOptions(IPdfProcessingRequest request)
{
    // Optional values are omitted instead of serialized as empty strings so CLI defaults and persisted settings still apply.
    var args = new StringBuilder();
    Append(args, "--pdf-processing", request.PdfProcessing);
    Append(args, "--ocr-languages", request.OcrLanguages);
    Append(args, "--tessdata", request.TessdataPath);
    Append(args, "--pdf-text-threshold", request.PdfTextThreshold?.ToString());
    Append(args, "--pdf-render-dpi", request.PdfRenderDpi?.ToString());
    Append(args, "--pdf-splitting", request.PdfSplitting);
    Append(args, "--pdf-max-pages-per-part", request.PdfMaxPagesPerPart?.ToString());
    Append(args, "--pdf-max-part-size-mb", request.PdfMaxPartSizeMb?.ToString());
    Append(args, "--azure-document-intelligence-endpoint", request.AzureDocumentIntelligenceEndpoint);
    Append(args, "--azure-document-intelligence-locale", request.AzureDocumentIntelligenceLocale);
    Append(args, "--azure-document-intelligence-tier", request.AzureDocumentIntelligenceTier);
    return args.ToString();
}

// Appends a quoted name/value option only when the request supplied a meaningful value.
static void Append(StringBuilder args, string name, string? value)
{
    // A leading space makes fragments composable with the already-built command verb and required arguments.
    if (!string.IsNullOrWhiteSpace(value))
    {
        args.Append(' ').Append(name).Append(' ').Append(Quote(value));
    }
}

internal interface IPdfProcessingRequest
{
    string? PdfProcessing { get; }

    string? OcrLanguages { get; }

    string? TessdataPath { get; }

    int? PdfTextThreshold { get; }

    int? PdfRenderDpi { get; }

    string? PdfSplitting { get; }

    int? PdfMaxPagesPerPart { get; }

    int? PdfMaxPartSizeMb { get; }

    string? AzureDocumentIntelligenceEndpoint { get; }

    string? AzureDocumentIntelligenceKey { get; }

    string? AzureDocumentIntelligenceLocale { get; }

    string? AzureDocumentIntelligenceTier { get; }
}

internal sealed record ConvertDocumentRequest(
    string InputPath,
    string OutputPath,
    bool Overwrite = false,
    string? PdfProcessing = null,
    string? OcrLanguages = null,
    string? TessdataPath = null,
    int? PdfTextThreshold = null,
    int? PdfRenderDpi = null,
    string? PdfSplitting = null,
    int? PdfMaxPagesPerPart = null,
    int? PdfMaxPartSizeMb = null,
    string? AzureDocumentIntelligenceEndpoint = null,
    string? AzureDocumentIntelligenceKey = null,
    string? AzureDocumentIntelligenceLocale = null,
    string? AzureDocumentIntelligenceTier = null) : IPdfProcessingRequest;

internal sealed record ConvertFolderRequest(
    string InputFolder,
    bool Recursive = false,
    bool Overwrite = false,
    bool ContinueOnError = true,
    string? PdfProcessing = null,
    string? OcrLanguages = null,
    string? TessdataPath = null,
    int? PdfTextThreshold = null,
    int? PdfRenderDpi = null,
    string? PdfSplitting = null,
    int? PdfMaxPagesPerPart = null,
    int? PdfMaxPartSizeMb = null,
    string? AzureDocumentIntelligenceEndpoint = null,
    string? AzureDocumentIntelligenceKey = null,
    string? AzureDocumentIntelligenceLocale = null,
    string? AzureDocumentIntelligenceTier = null) : IPdfProcessingRequest;

internal static class CliRunner
{
    /// <summary>
    /// Runs the DOC2MD CLI for an API request and captures its complete result.
    /// </summary>
    /// <param name="arguments">The prepared CLI arguments.</param>
    /// <param name="azureDocumentIntelligenceKey">An optional request-scoped Azure key.</param>
    /// <returns>The CLI exit code and output streams.</returns>
    public static async Task<CliResult> RunAsync(string arguments, string? azureDocumentIntelligenceKey)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ResolveCliPath(),
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(azureDocumentIntelligenceKey))
        {
            // Secrets use the child environment so they do not appear in command lines or process listings.
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = azureDocumentIntelligenceKey;
        }

        process.Start();
        // Drain both redirected streams before waiting to prevent a full pipe from blocking the CLI.
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Maps a CLI result to the established HTTP success or bad-request response contract.
    /// </summary>
    /// <param name="result">The captured CLI result.</param>
    /// <returns>An HTTP result containing structured CLI JSON when available.</returns>
    public static IResult ToHttpResult(CliResult result)
    {
        // Preserve structured CLI payloads, but retain raw streams when a startup failure produced non-JSON output.
        object body;
        try
        {
            body = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
        }
        catch (JsonException)
        {
            body = new { succeeded = false, exitCode = result.ExitCode, stdout = result.Stdout, stderr = result.Stderr };
        }

        return result.ExitCode == 0 ? Results.Ok(body) : Results.BadRequest(body);
    }

    /// <summary>
    /// Resolves the CLI from explicit deployment configuration, a development checkout, or the process search path.
    /// </summary>
    /// <returns>The CLI executable path or fallback command name.</returns>
    private static string ResolveCliPath()
    {
        // Explicit configuration supports IIS deployments where the API and CLI publish directories are separated.
        var configured = Environment.GetEnvironmentVariable("DOC2MD_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "DOC2MD.Cli", "bin", "Debug", "net8.0", "DOC2MD.Cli.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return "DOC2MD.Cli.exe";
    }
}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
