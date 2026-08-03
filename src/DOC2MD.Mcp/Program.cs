using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonNode? request;
    try
    {
        request = JsonNode.Parse(line);
    }
    catch (JsonException)
    {
        continue;
    }

    var id = request?["id"];
    var method = request?["method"]?.GetValue<string>();

    if (id is null)
    {
        continue;
    }

    try
    {
        var result = method switch
        {
            "initialize" => InitializeResult(),
            "tools/list" => ToolsListResult(),
            "tools/call" => await CallToolAsync(request?["params"]),
            _ => throw new InvalidOperationException($"Unsupported MCP method: {method}")
        };

        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["result"] = result
        });
    }
    catch (Exception ex)
    {
        await WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32000,
                ["message"] = ex.Message
            }
        });
    }
}

static JsonObject InitializeResult() => new()
{
    ["protocolVersion"] = "2024-11-05",
    ["capabilities"] = new JsonObject
    {
        ["tools"] = new JsonObject()
    },
    ["serverInfo"] = new JsonObject
    {
        ["name"] = "doc2md-mcp",
        ["version"] = "1.0.0"
    }
};

static JsonObject ToolsListResult() => new()
{
    ["tools"] = new JsonArray
    {
        new JsonObject
        {
            ["name"] = "convert_document",
            ["description"] = "Convert one supported document to a specified Markdown output file.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["inputPath"] = new JsonObject { ["type"] = "string" },
                    ["outputPath"] = new JsonObject { ["type"] = "string" },
                    ["overwrite"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                    ["pdfProcessing"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("local", "azure", "markitdown"), ["default"] = "local" },
                    ["ocrLanguages"] = new JsonObject { ["type"] = "string", ["default"] = "eng+pol" },
                    ["tessdataPath"] = new JsonObject { ["type"] = "string" },
                    ["pdfTextThreshold"] = new JsonObject { ["type"] = "integer", ["default"] = 40 },
                    ["pdfRenderDpi"] = new JsonObject { ["type"] = "integer", ["default"] = 300 },
                    ["pdfSplitting"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("auto", "always", "never"), ["default"] = "always" },
                    ["pdfMaxPagesPerPart"] = new JsonObject { ["type"] = "integer", ["default"] = 100 },
                    ["pdfMaxPartSizeMb"] = new JsonObject { ["type"] = "integer", ["default"] = 100 },
                    ["azureDocumentIntelligenceEndpoint"] = new JsonObject { ["type"] = "string" },
                    ["azureDocumentIntelligenceKey"] = new JsonObject { ["type"] = "string" },
                    ["azureDocumentIntelligenceLocale"] = new JsonObject { ["type"] = "string" },
                    ["azureDocumentIntelligenceTier"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("f0", "s0"), ["default"] = "s0" }
                },
                ["required"] = new JsonArray("inputPath", "outputPath")
            }
        },
        new JsonObject
        {
            ["name"] = "convert_folder",
            ["description"] = "Convert supported documents in a folder to Markdown files beside the source documents.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["inputFolder"] = new JsonObject { ["type"] = "string" },
                    ["recursive"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                    ["overwrite"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                    ["continueOnError"] = new JsonObject { ["type"] = "boolean", ["default"] = true },
                    ["pdfProcessing"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("local", "azure", "markitdown"), ["default"] = "local" },
                    ["ocrLanguages"] = new JsonObject { ["type"] = "string", ["default"] = "eng+pol" },
                    ["tessdataPath"] = new JsonObject { ["type"] = "string" },
                    ["pdfTextThreshold"] = new JsonObject { ["type"] = "integer", ["default"] = 40 },
                    ["pdfRenderDpi"] = new JsonObject { ["type"] = "integer", ["default"] = 300 },
                    ["pdfSplitting"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("auto", "always", "never"), ["default"] = "always" },
                    ["pdfMaxPagesPerPart"] = new JsonObject { ["type"] = "integer", ["default"] = 100 },
                    ["pdfMaxPartSizeMb"] = new JsonObject { ["type"] = "integer", ["default"] = 100 },
                    ["azureDocumentIntelligenceEndpoint"] = new JsonObject { ["type"] = "string" },
                    ["azureDocumentIntelligenceKey"] = new JsonObject { ["type"] = "string" },
                    ["azureDocumentIntelligenceLocale"] = new JsonObject { ["type"] = "string" },
                    ["azureDocumentIntelligenceTier"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("f0", "s0"), ["default"] = "s0" }
                },
                ["required"] = new JsonArray("inputFolder")
            }
        }
    }
};

static async Task<JsonObject> CallToolAsync(JsonNode? parameters)
{
    var name = parameters?["name"]?.GetValue<string>() ?? throw new InvalidOperationException("Tool name is required.");
    var arguments = parameters?["arguments"]?.AsObject() ?? new JsonObject();

    var cliArguments = name switch
    {
        "convert_document" => BuildConvertArguments(arguments),
        "convert_folder" => BuildConvertFolderArguments(arguments),
        _ => throw new InvalidOperationException($"Unknown tool: {name}")
    };

    var result = await CliRunner.RunAsync(cliArguments, String(arguments, "azureDocumentIntelligenceKey"));
    return new JsonObject
    {
        ["isError"] = result.ExitCode != 0,
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = string.IsNullOrWhiteSpace(result.Stderr)
                    ? result.Stdout
                    : result.Stdout + Environment.NewLine + result.Stderr
            }
        }
    };
}

static string BuildConvertArguments(JsonObject arguments)
{
    var inputPath = Required(arguments, "inputPath");
    var outputPath = Required(arguments, "outputPath");
    var cliArgs = $"convert --input {Quote(inputPath)} --output {Quote(outputPath)} --json";
    if (Bool(arguments, "overwrite"))
    {
        cliArgs += " --overwrite";
    }

    cliArgs += BuildPdfOptions(arguments);
    return cliArgs;
}

static string BuildConvertFolderArguments(JsonObject arguments)
{
    var inputFolder = Required(arguments, "inputFolder");
    var cliArgs = $"convert-folder --input {Quote(inputFolder)} --json";
    if (Bool(arguments, "recursive"))
    {
        cliArgs += " --recursive";
    }

    if (Bool(arguments, "overwrite"))
    {
        cliArgs += " --overwrite";
    }

    if (Bool(arguments, "continueOnError", defaultValue: true))
    {
        cliArgs += " --continue-on-error";
    }

    cliArgs += BuildPdfOptions(arguments);
    return cliArgs;
}

static string BuildPdfOptions(JsonObject arguments)
{
    var cliArgs = new StringBuilder();
    AppendOption(cliArgs, "--pdf-processing", String(arguments, "pdfProcessing"));
    AppendOption(cliArgs, "--ocr-languages", String(arguments, "ocrLanguages"));
    AppendOption(cliArgs, "--tessdata", String(arguments, "tessdataPath"));
    AppendOption(cliArgs, "--pdf-text-threshold", Int(arguments, "pdfTextThreshold")?.ToString());
    AppendOption(cliArgs, "--pdf-render-dpi", Int(arguments, "pdfRenderDpi")?.ToString());
    AppendOption(cliArgs, "--pdf-splitting", String(arguments, "pdfSplitting"));
    AppendOption(cliArgs, "--pdf-max-pages-per-part", Int(arguments, "pdfMaxPagesPerPart")?.ToString());
    AppendOption(cliArgs, "--pdf-max-part-size-mb", Int(arguments, "pdfMaxPartSizeMb")?.ToString());
    AppendOption(cliArgs, "--azure-document-intelligence-endpoint", String(arguments, "azureDocumentIntelligenceEndpoint"));
    AppendOption(cliArgs, "--azure-document-intelligence-locale", String(arguments, "azureDocumentIntelligenceLocale"));
    AppendOption(cliArgs, "--azure-document-intelligence-tier", String(arguments, "azureDocumentIntelligenceTier"));
    return cliArgs.ToString();
}

static void AppendOption(StringBuilder cliArgs, string name, string? value)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        cliArgs.Append(' ').Append(name).Append(' ').Append(Quote(value));
    }
}

static string Required(JsonObject arguments, string name) =>
    arguments[name]?.GetValue<string>() ?? throw new InvalidOperationException($"{name} is required.");

static string? String(JsonObject arguments, string name) =>
    arguments[name]?.GetValue<string>();

static int? Int(JsonObject arguments, string name) =>
    arguments[name]?.GetValue<int>();

static bool Bool(JsonObject arguments, string name, bool defaultValue = false) =>
    arguments[name]?.GetValue<bool>() ?? defaultValue;

static async Task WriteAsync(JsonObject message)
{
    await Console.Out.WriteLineAsync(message.ToJsonString());
    await Console.Out.FlushAsync();
}

static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

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
