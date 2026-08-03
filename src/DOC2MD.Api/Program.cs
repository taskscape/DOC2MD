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

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

static string BuildPdfOptions(IPdfProcessingRequest request)
{
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

static void Append(StringBuilder args, string name, string? value)
{
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
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = azureDocumentIntelligenceKey;
        }

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, stdout, stderr);
    }

    public static IResult ToHttpResult(CliResult result)
    {
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

    private static string ResolveCliPath()
    {
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
