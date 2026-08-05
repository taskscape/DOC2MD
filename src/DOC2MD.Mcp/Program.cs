using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

while (await Console.In.ReadLineAsync() is { } line)
{
    // MCP stdio transports frame messages as one JSON-RPC request per line. Ignoring blank or
    // malformed input keeps a bad notification from terminating the long-lived server process.
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
        // JSON-RPC notifications intentionally have no response.
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

// Advertise the protocol and capabilities expected by MCP clients. Keep this version pinned until
// the request and response shapes below are deliberately updated for a newer MCP revision.
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

// Describe the public tools at the MCP boundary. These schemas must remain aligned with the CLI
// builders because the CLI remains the final validation authority.
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

// Dispatch a tool call to the shared CLI and translate its process result into MCP content.
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

    // Azure secrets travel through the child environment rather than command-line arguments,
    // where process-inspection tools and MCP logs could expose them.
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

// Build one-file conversion arguments. Required values are checked here so the MCP caller receives
// a tool error before a child process is started.
static IReadOnlyList<string> BuildConvertArguments(JsonObject arguments)
{
    var inputPath = Required(arguments, "inputPath");
    var outputPath = Required(arguments, "outputPath");
    var cliArgs = new List<string> { "convert", "--input", inputPath, "--output", outputPath, "--json" };
    if (Bool(arguments, "overwrite"))
    {
        cliArgs.Add("--overwrite");
    }

    AppendPdfOptions(cliArgs, arguments);
    return cliArgs;
}

// Build folder conversion arguments. The CLI derives output paths from the input tree, so the MCP
// contract intentionally requires only the source folder.
static IReadOnlyList<string> BuildConvertFolderArguments(JsonObject arguments)
{
    var inputFolder = Required(arguments, "inputFolder");
    var cliArgs = new List<string> { "convert-folder", "--input", inputFolder, "--json" };
    if (Bool(arguments, "recursive"))
    {
        cliArgs.Add("--recursive");
    }

    if (Bool(arguments, "overwrite"))
    {
        cliArgs.Add("--overwrite");
    }

    if (Bool(arguments, "continueOnError", defaultValue: true))
    {
        cliArgs.Add("--continue-on-error");
    }

    AppendPdfOptions(cliArgs, arguments);
    return cliArgs;
}

// Forward only explicitly supplied PDF options. Cross-option compatibility is deliberately checked
// by the CLI so API, GUI, and MCP callers all follow one policy.
static void AppendPdfOptions(List<string> cliArgs, JsonObject arguments)
{
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
}

// Append one optional CLI switch as a distinct process argument.
static void AppendOption(List<string> cliArgs, string name, string? value)
{
    if (!string.IsNullOrWhiteSpace(value))
    {
        cliArgs.Add(name);
        cliArgs.Add(value);
    }
}

// Read a required string and turn a missing value into a caller-facing validation error.
static string Required(JsonObject arguments, string name) =>
    arguments[name]?.GetValue<string>() ?? throw new InvalidOperationException($"{name} is required.");

// Read an optional string without coercing other JSON primitive types.
static string? String(JsonObject arguments, string name) =>
    arguments[name]?.GetValue<string>();

// Read an optional integer without inventing a default; the CLI supplies its documented defaults.
static int? Int(JsonObject arguments, string name) =>
    arguments[name]?.GetValue<int>();

// Preserve the distinction between an omitted Boolean and an explicitly supplied false value.
static bool Bool(JsonObject arguments, string name, bool defaultValue = false) =>
    arguments[name]?.GetValue<bool>() ?? defaultValue;

// Emit and flush one response immediately, as required by interactive stdio clients.
static async Task WriteAsync(JsonObject message)
{
    await Console.Out.WriteLineAsync(message.ToJsonString());
    await Console.Out.FlushAsync();
}

internal static class CliRunner
{
    /// <summary>
    /// Executes the DOC2MD CLI and captures its complete output for an MCP tool response.
    /// </summary>
    /// <param name="arguments">The individual command-line arguments.</param>
    /// <param name="azureDocumentIntelligenceKey">An optional Azure key passed through the child environment.</param>
    /// <returns>The child process exit code and captured standard streams.</returns>
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
            // Keep credentials out of the command line, which can be visible to other local users.
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = azureDocumentIntelligenceKey;
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

}

internal sealed record CliResult(int ExitCode, string Stdout, string Stderr);
