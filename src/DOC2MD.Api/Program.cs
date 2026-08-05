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
    var args = new List<string> { "convert", "--input", request.InputPath, "--output", request.OutputPath, "--json" };
    if (request.Overwrite)
    {
        args.Add("--overwrite");
    }

    AppendPdfOptions(args, request);

    var result = await CliRunner.RunAsync(args, request.AzureDocumentIntelligenceKey);
    return CliRunner.ToHttpResult(result);
})
.WithName("ConvertDocument");

app.MapPost("/convert-folder", async (ConvertFolderRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.InputFolder))
    {
        return Results.BadRequest(new { error = "InputFolder is required." });
    }

    // Folder output stays implicit because the CLI writes Markdown beside each selected source document.
    var args = new List<string> { "convert-folder", "--input", request.InputFolder, "--json" };
    if (request.Recursive)
    {
        args.Add("--recursive");
    }

    if (request.Overwrite)
    {
        args.Add("--overwrite");
    }

    if (request.ContinueOnError)
    {
        args.Add("--continue-on-error");
    }

    AppendPdfOptions(args, request);

    var result = await CliRunner.RunAsync(args, request.AzureDocumentIntelligenceKey);
    return CliRunner.ToHttpResult(result);
})
.WithName("ConvertFolder");

app.Run();

// Appends shared PDF options while leaving cross-stack validation to the CLI.
static void AppendPdfOptions(List<string> args, IPdfProcessingRequest request)
{
    // Optional values are omitted instead of serialized as empty strings so CLI defaults and persisted settings still apply.
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
}

// Appends a name/value option only when the request supplied a meaningful value.
static void Append(List<string> args, string name, string? value)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        args.Add(name);
        args.Add(value);
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
    public static async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, string? azureDocumentIntelligenceKey)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = CliExecutableLocator.Resolve(),
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

}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
