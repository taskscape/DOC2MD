using System.Text;
using System.Globalization;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Identity;
using Docnet.Core;
using Docnet.Core.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using PDFtoImage;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

internal enum PdfProcessingMode
{
    Local,
    Azure,
    MarkItDown
}

internal enum PdfSplittingMode
{
    Auto,
    Always,
    Never
}

internal enum AzureDocumentIntelligenceTier
{
    FreeF0,
    StandardS0
}

internal sealed record PdfConversionOptions(
    PdfProcessingMode ProcessingMode,
    string OcrLanguages,
    string? TessdataPath,
    int TextThreshold,
    int RenderDpi,
    string? AzureEndpoint,
    string? AzureKey,
    string? AzureLocale,
    AzureDocumentIntelligenceTier AzureTier,
    PdfSplittingMode SplittingMode,
    int MaxPagesPerPart,
    int MaxPartSizeMb)
{
    public long MaxPartSizeBytes => MaxPartSizeMb * 1_000_000L;

    /// <summary>
    /// Builds validated PDF conversion options from command-line, environment, and persisted settings.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The effective conversion options.</returns>
    public static PdfConversionOptions FromArgs(string[] args)
    {
        // Explicit CLI values win over environment or persisted settings so each invocation remains independently controllable.
        var configured = Doc2MdConfiguration.Load();
        var mode = ParseProcessingMode(
            Value(args, "--pdf-processing")
            ?? Value(args, "--pdf-mode")
            ?? configured.Get("DOC2MD_PDF_PROCESSING")
            ?? "local");

        var ocrLanguages = Value(args, "--ocr-languages")
            ?? Environment.GetEnvironmentVariable("DOC2MD_OCR_LANGUAGES")
            ?? "eng+pol";

        var tessdataPath = Value(args, "--tessdata")
            ?? Environment.GetEnvironmentVariable("DOC2MD_TESSDATA_PATH");

        var textThreshold = IntValue(
            Value(args, "--pdf-text-threshold") ?? Environment.GetEnvironmentVariable("DOC2MD_PDF_TEXT_THRESHOLD"),
            defaultValue: 40,
            optionName: "--pdf-text-threshold");

        var renderDpi = IntValue(
            Value(args, "--pdf-render-dpi") ?? Environment.GetEnvironmentVariable("DOC2MD_PDF_RENDER_DPI"),
            defaultValue: 300,
            optionName: "--pdf-render-dpi");

        var azureEndpoint = Value(args, "--azure-document-intelligence-endpoint")
            ?? Value(args, "--azure-endpoint")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT");

        var azureKey = Value(args, "--azure-document-intelligence-key")
            ?? Value(args, "--azure-key")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY");

        var azureLocale = Value(args, "--azure-document-intelligence-locale")
            ?? Value(args, "--azure-locale")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_LOCALE");

        var azureTier = ParseAzureTier(
            Value(args, "--azure-document-intelligence-tier")
            ?? Value(args, "--azure-tier")
            ?? configured.Get("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_TIER")
            ?? "s0");

        var splittingMode = ParseSplittingMode(
            Value(args, "--pdf-splitting")
            ?? Environment.GetEnvironmentVariable("DOC2MD_PDF_SPLITTING")
            ?? "always");

        var maxPagesPerPart = IntValue(
            Value(args, "--pdf-max-pages-per-part") ?? Environment.GetEnvironmentVariable("DOC2MD_PDF_MAX_PAGES_PER_PART"),
            defaultValue: DefaultMaxPagesPerPart(mode, azureTier),
            optionName: "--pdf-max-pages-per-part");

        var maxPartSizeMb = IntValue(
            Value(args, "--pdf-max-part-size-mb") ?? Environment.GetEnvironmentVariable("DOC2MD_PDF_MAX_PART_SIZE_MB"),
            defaultValue: DefaultMaxPartSizeMb(mode, azureTier),
            optionName: "--pdf-max-part-size-mb");

        ValidateSingleProcessingStack(args, mode);
        ValidateSplitLimits(mode, azureTier, maxPagesPerPart, maxPartSizeMb);

        if (mode == PdfProcessingMode.Azure && string.IsNullOrWhiteSpace(azureEndpoint))
        {
            throw new ArgumentException(
                "Azure PDF processing requires --azure-document-intelligence-endpoint or DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT.");
        }

        return new PdfConversionOptions(
            mode,
            ocrLanguages,
            string.IsNullOrWhiteSpace(tessdataPath) ? null : tessdataPath,
            textThreshold,
            renderDpi,
            string.IsNullOrWhiteSpace(azureEndpoint) ? null : azureEndpoint,
            string.IsNullOrWhiteSpace(azureKey) ? null : azureKey,
            string.IsNullOrWhiteSpace(azureLocale) ? null : azureLocale,
            azureTier,
            splittingMode,
            maxPagesPerPart,
            maxPartSizeMb);
    }

    /// <summary>
    /// Rejects options belonging to a PDF processing stack other than the selected mode.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="mode">The selected processing mode.</param>
    private static void ValidateSingleProcessingStack(string[] args, PdfProcessingMode mode)
    {
        // Mixing stacks is rejected rather than ignored because silently unused credentials or OCR tuning is misleading.
        var hasLocalOptions = Has(args, "--ocr-languages")
            || Has(args, "--tessdata")
            || Has(args, "--pdf-text-threshold")
            || Has(args, "--pdf-render-dpi");

        var hasAzureOptions = Has(args, "--azure-document-intelligence-endpoint")
            || Has(args, "--azure-endpoint")
            || Has(args, "--azure-document-intelligence-key")
            || Has(args, "--azure-key")
            || Has(args, "--azure-document-intelligence-locale")
            || Has(args, "--azure-locale")
            || Has(args, "--azure-document-intelligence-tier")
            || Has(args, "--azure-tier");

        if (mode != PdfProcessingMode.Local && hasLocalOptions)
        {
            throw new ArgumentException("Local OCR options can only be used with --pdf-processing local.");
        }

        if (mode != PdfProcessingMode.Azure && hasAzureOptions)
        {
            throw new ArgumentException("Azure Document Intelligence options can only be used with --pdf-processing azure.");
        }
    }

    /// <summary>
    /// Parses a PDF processing mode and its supported compatibility aliases.
    /// </summary>
    /// <param name="value">The configured mode text.</param>
    /// <returns>The normalized mode.</returns>
    private static PdfProcessingMode ParseProcessingMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "local" => PdfProcessingMode.Local,
            "azure" => PdfProcessingMode.Azure,
            "cloud" => PdfProcessingMode.Azure,
            "markitdown" => PdfProcessingMode.MarkItDown,
            "mark-it-down" => PdfProcessingMode.MarkItDown,
            "none" => PdfProcessingMode.MarkItDown,
            _ => throw new ArgumentException("--pdf-processing must be one of: local, azure, markitdown.")
        };

    /// <summary>
    /// Parses the Azure service tier used to enforce request limits.
    /// </summary>
    /// <param name="value">The configured tier text.</param>
    /// <returns>The normalized Azure tier.</returns>
    private static AzureDocumentIntelligenceTier ParseAzureTier(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "f0" => AzureDocumentIntelligenceTier.FreeF0,
            "free" => AzureDocumentIntelligenceTier.FreeF0,
            "freef0" => AzureDocumentIntelligenceTier.FreeF0,
            "s0" => AzureDocumentIntelligenceTier.StandardS0,
            "standard" => AzureDocumentIntelligenceTier.StandardS0,
            "standards0" => AzureDocumentIntelligenceTier.StandardS0,
            _ => throw new ArgumentException("--azure-document-intelligence-tier must be one of: f0, s0.")
        };

    /// <summary>
    /// Parses the policy controlling PDF splitting.
    /// </summary>
    /// <param name="value">The configured splitting policy.</param>
    /// <returns>The normalized splitting mode.</returns>
    private static PdfSplittingMode ParseSplittingMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "auto" => PdfSplittingMode.Auto,
            "always" => PdfSplittingMode.Always,
            "never" => PdfSplittingMode.Never,
            _ => throw new ArgumentException("--pdf-splitting must be one of: auto, always, never.")
        };

    /// <summary>
    /// Selects a conservative default page limit for the active processing stack and Azure tier.
    /// </summary>
    /// <param name="mode">The processing mode.</param>
    /// <param name="azureTier">The Azure tier when Azure processing is selected.</param>
    /// <returns>The default maximum page count per part.</returns>
    private static int DefaultMaxPagesPerPart(PdfProcessingMode mode, AzureDocumentIntelligenceTier azureTier) =>
        mode == PdfProcessingMode.Azure
            ? azureTier == AzureDocumentIntelligenceTier.FreeF0 ? 2 : 100
            : 100;

    /// <summary>
    /// Selects a conservative default size limit for the active processing stack and Azure tier.
    /// </summary>
    /// <param name="mode">The processing mode.</param>
    /// <param name="azureTier">The Azure tier when Azure processing is selected.</param>
    /// <returns>The default maximum part size in decimal megabytes.</returns>
    private static int DefaultMaxPartSizeMb(PdfProcessingMode mode, AzureDocumentIntelligenceTier azureTier) =>
        mode == PdfProcessingMode.Azure
            ? azureTier == AzureDocumentIntelligenceTier.FreeF0 ? 3 : 100
            : 100;

    /// <summary>
    /// Ensures configured Azure part limits do not exceed the selected service tier's hard limits.
    /// </summary>
    /// <param name="mode">The processing mode.</param>
    /// <param name="azureTier">The selected Azure tier.</param>
    /// <param name="maxPagesPerPart">The configured page limit.</param>
    /// <param name="maxPartSizeMb">The configured size limit in decimal megabytes.</param>
    private static void ValidateSplitLimits(
        PdfProcessingMode mode,
        AzureDocumentIntelligenceTier azureTier,
        int maxPagesPerPart,
        int maxPartSizeMb)
    {
        if (mode != PdfProcessingMode.Azure)
        {
            return;
        }

        // These are service constraints, while the lower defaults above leave headroom for predictable uploads.
        var servicePageLimit = azureTier == AzureDocumentIntelligenceTier.FreeF0 ? 2 : 2000;
        var serviceSizeLimitMb = azureTier == AzureDocumentIntelligenceTier.FreeF0 ? 4 : 500;

        if (maxPagesPerPart > servicePageLimit)
        {
            throw new ArgumentException(
                $"--pdf-max-pages-per-part cannot exceed {servicePageLimit} for Azure Document Intelligence {azureTier}.");
        }

        if (maxPartSizeMb > serviceSizeLimitMb)
        {
            throw new ArgumentException(
                $"--pdf-max-part-size-mb cannot exceed {serviceSizeLimitMb} for Azure Document Intelligence {azureTier}.");
        }
    }

    /// <summary>
    /// Parses a positive integer option or returns its default.
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="defaultValue">The value used when no option is supplied.</param>
    /// <param name="optionName">The option name used in validation messages.</param>
    /// <returns>The positive configured or default value.</returns>
    private static int IntValue(string? value, int defaultValue, string optionName)
    {
        // Zero is invalid because every caller uses the value as a divisor, batch size, or physical render setting.
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{optionName} must be a positive integer.");
        }

        return parsed;
    }

    /// <summary>
    /// Reads the value immediately following a named option.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="name">The option name.</param>
    /// <returns>The option value, or <see langword="null"/> when absent.</returns>
    private static string? Value(string[] args, string name)
    {
        // The CLI intentionally supports name/value pairs only; equals and combined forms are outside its contract.
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
}

internal sealed record DocumentConversionResult(
    int ExitCode,
    string Converter,
    string? InspectionSummary,
    string Stdout,
    string Stderr);

internal static class DocumentConversion
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Routes a document to MarkItDown, local PDF inspection/OCR, or Azure Document Intelligence.
    /// </summary>
    /// <param name="input">The effective source document.</param>
    /// <param name="output">The Markdown output path.</param>
    /// <param name="options">The validated PDF conversion options.</param>
    /// <param name="runMarkItDownAsync">The caller-provided MarkItDown process adapter.</param>
    /// <returns>The normalized conversion result.</returns>
    public static async Task<DocumentConversionResult> ConvertAsync(
        string input,
        string output,
        PdfConversionOptions options,
        Func<string, string, Task<(int ExitCode, string stdout, string stderr)>> runMarkItDownAsync)
    {
        // Non-PDF formats always use MarkItDown; PDF-specific modes must not change their established behavior.
        if (!Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || options.ProcessingMode == PdfProcessingMode.MarkItDown)
        {
            var result = await runMarkItDownAsync(input, output);
            return FromProcessResult(result, "markitdown", inspectionSummary: null);
        }

        return options.ProcessingMode switch
        {
            PdfProcessingMode.Local => await ConvertLocalPdfAsync(input, output, options, runMarkItDownAsync),
            PdfProcessingMode.Azure => await ConvertAzurePdfAsync(input, output, options),
            _ => throw new InvalidOperationException($"Unsupported PDF processing mode: {options.ProcessingMode}.")
        };
    }

    /// <summary>
    /// Uses extractable PDF text where sufficient and applies Tesseract only to pages that need OCR.
    /// </summary>
    /// <param name="input">The PDF source path.</param>
    /// <param name="output">The Markdown output path.</param>
    /// <param name="options">The local PDF options.</param>
    /// <param name="runMarkItDownAsync">The MarkItDown adapter used for fully extractable PDFs.</param>
    /// <returns>The normalized conversion result.</returns>
    private static async Task<DocumentConversionResult> ConvertLocalPdfAsync(
        string input,
        string output,
        PdfConversionOptions options,
        Func<string, string, Task<(int ExitCode, string stdout, string stderr)>> runMarkItDownAsync)
    {
        var inspection = InspectPdf(input, options.TextThreshold);
        var summary = inspection.ToSummary();

        if (inspection.OcrPageCount == 0)
        {
            // MarkItDown preserves richer document structure, so it remains preferred when OCR adds no value.
            var result = await runMarkItDownAsync(input, output);
            return FromProcessResult(result, "markitdown", summary);
        }

        var tessdataPath = ResolveTessdataPath(options);
        ValidateTessdataLanguages(tessdataPath, options.OcrLanguages);

        var markdown = BuildLocalMixedMarkdown(input, inspection, options, tessdataPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, markdown, Utf8NoBom);

        return new DocumentConversionResult(
            0,
            "local-pdf-inspection-ocr",
            summary,
            string.Empty,
            $"Local PDF processing completed. {summary}");
    }

    /// <summary>
    /// Splits a PDF as required, analyzes each part with Azure Layout, and merges Markdown in source order.
    /// </summary>
    /// <param name="input">The PDF source path.</param>
    /// <param name="output">The Markdown output path.</param>
    /// <param name="options">The Azure PDF options.</param>
    /// <returns>The Azure conversion result.</returns>
    private static async Task<DocumentConversionResult> ConvertAzurePdfAsync(
        string input,
        string output,
        PdfConversionOptions options)
    {
        var client = CreateDocumentIntelligenceClient(options);
        var parts = CreateAzurePdfParts(input, options);

        try
        {
            // Parts are processed sequentially to preserve source ordering and avoid an uncontrolled Azure request burst.
            var markdown = new StringBuilder();

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var content = await AnalyzeAzurePdfPartAsync(client, part.FilePath, options);

                if (parts.Count > 1)
                {
                    markdown.AppendLine($"<!-- DOC2MD Azure split part {i + 1}/{parts.Count}: source pages {part.StartPage}-{part.EndPage}, {FormatBytes(part.SizeBytes)}. -->");
                    markdown.AppendLine();
                }

                if (!string.IsNullOrWhiteSpace(content))
                {
                    markdown.AppendLine(content.Trim());
                }

                markdown.AppendLine();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, markdown.ToString().TrimEnd() + Environment.NewLine, Utf8NoBom);

            return new DocumentConversionResult(
                0,
                parts.Count == 1 ? "azure-document-intelligence-layout" : "azure-document-intelligence-layout-split",
                DescribePdfParts(parts),
                string.Empty,
                $"Azure AI Document Intelligence Layout conversion completed in {parts.Count} part(s).");
        }
        finally
        {
            // Temporary split files are an implementation detail and must be removed on success or failure.
            DeleteTemporaryParts(parts);
        }
    }

    /// <summary>
    /// Sends one PDF file or split part to the Azure prebuilt-layout model.
    /// </summary>
    /// <param name="client">The configured Document Intelligence client.</param>
    /// <param name="input">The PDF part path.</param>
    /// <param name="options">Options containing the optional locale.</param>
    /// <returns>The Markdown content returned by Azure.</returns>
    private static async Task<string> AnalyzeAzurePdfPartAsync(
        DocumentIntelligenceClient client,
        string input,
        PdfConversionOptions options)
    {
        var bytes = await File.ReadAllBytesAsync(input);
        var analyzeOptions = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(bytes))
        {
            OutputContentFormat = DocumentContentFormat.Markdown
        };

        if (!string.IsNullOrWhiteSpace(options.AzureLocale))
        {
            analyzeOptions.Locale = options.AzureLocale;
        }

        // Waiting here keeps the CLI contract synchronous from the caller's perspective: success means output is complete.
        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions);
        return operation.Value.Content ?? string.Empty;
    }

    /// <summary>
    /// Creates an Azure client using an explicit key when supplied, otherwise the default credential chain.
    /// </summary>
    /// <param name="options">Options containing the endpoint and optional API key.</param>
    /// <returns>The configured client.</returns>
    private static DocumentIntelligenceClient CreateDocumentIntelligenceClient(PdfConversionOptions options)
    {
        // The endpoint is validated while options are created, so null-forgiving use is safe at this boundary.
        var endpoint = new Uri(options.AzureEndpoint!);
        if (!string.IsNullOrWhiteSpace(options.AzureKey))
        {
            return new DocumentIntelligenceClient(endpoint, new AzureKeyCredential(options.AzureKey));
        }

        TokenCredential credential = new DefaultAzureCredential();
        return new DocumentIntelligenceClient(endpoint, credential);
    }

    /// <summary>
    /// Produces source-page-ordered PDF parts that satisfy configured page and size limits.
    /// </summary>
    /// <param name="input">The source PDF path.</param>
    /// <param name="options">The splitting limits and policy.</param>
    /// <returns>The original file descriptor or temporary split descriptors.</returns>
    private static IReadOnlyList<PdfPart> CreateAzurePdfParts(string input, PdfConversionOptions options)
    {
        using var document = PdfDocument.Open(input);
        var pageCount = document.NumberOfPages;
        var sourceSize = new FileInfo(input).Length;
        var needsSplit = options.SplittingMode == PdfSplittingMode.Always
            || pageCount > options.MaxPagesPerPart
            || sourceSize > options.MaxPartSizeBytes;

        if (!needsSplit)
        {
            return new[]
            {
                new PdfPart(input, 1, pageCount, sourceSize, IsTemporary: false)
            };
        }

        if (options.SplittingMode == PdfSplittingMode.Never)
        {
            throw new InvalidOperationException(
                $"PDF exceeds configured limits ({FormatBytes(sourceSize)}, {pageCount} page(s)) and --pdf-splitting is never.");
        }

        var parts = new List<PdfPart>();
        var startPage = 1;

        while (startPage <= pageCount)
        {
            var candidatePageCount = Math.Min(options.MaxPagesPerPart, pageCount - startPage + 1);

            while (true)
            {
                var endPage = startPage + candidatePageCount - 1;
                var part = CreatePdfPart(document, startPage, endPage);

                if (part.SizeBytes <= options.MaxPartSizeBytes)
                {
                    parts.Add(part);
                    startPage = endPage + 1;
                    break;
                }

                DeleteTemporaryPart(part);

                if (candidatePageCount == 1)
                {
                    throw new InvalidOperationException(
                        $"A single-page PDF split for source page {startPage} is {FormatBytes(part.SizeBytes)}, " +
                        $"which exceeds the configured {FormatBytes(options.MaxPartSizeBytes)} per-part limit.");
                }

                // Halving converges quickly without assuming that compressed size is proportional to page count.
                candidatePageCount = Math.Max(1, candidatePageCount / 2);
            }
        }

        return parts;
    }

    /// <summary>
    /// Copies an inclusive one-based page range into a uniquely named temporary PDF.
    /// </summary>
    /// <param name="document">The open source PDF.</param>
    /// <param name="startPage">The first one-based source page.</param>
    /// <param name="endPage">The last one-based source page.</param>
    /// <returns>The temporary part descriptor.</returns>
    private static PdfPart CreatePdfPart(PdfDocument document, int startPage, int endPage)
    {
        // PdfPig's builder consumes one-based page numbers, matching the values shown in user-facing diagnostics.
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"doc2md-pdf-part-{Guid.NewGuid():N}-pages-{startPage}-{endPage}.pdf");

        var builder = new PdfDocumentBuilder();
        for (var pageNumber = startPage; pageNumber <= endPage; pageNumber++)
        {
            builder.AddPage(document, pageNumber);
        }

        File.WriteAllBytes(tempPath, builder.Build());
        return new PdfPart(tempPath, startPage, endPage, new FileInfo(tempPath).Length, IsTemporary: true);
    }

    /// <summary>
    /// Best-effort deletes every temporary part in a conversion set.
    /// </summary>
    /// <param name="parts">The source and temporary part descriptors.</param>
    private static void DeleteTemporaryParts(IEnumerable<PdfPart> parts)
    {
        // Original source descriptors are included in the collection but are protected by IsTemporary.
        foreach (var part in parts)
        {
            DeleteTemporaryPart(part);
        }
    }

    /// <summary>
    /// Best-effort deletes one generated PDF part without masking the primary conversion outcome.
    /// </summary>
    /// <param name="part">The part descriptor.</param>
    private static void DeleteTemporaryPart(PdfPart part)
    {
        if (!part.IsTemporary)
        {
            return;
        }

        try
        {
            if (File.Exists(part.FilePath))
            {
                File.Delete(part.FilePath);
            }
        }
        catch (IOException)
        {
            // Temporary split cleanup failure should not hide a successful conversion.
        }
    }

    /// <summary>
    /// Builds a concise diagnostic summary of a PDF part set.
    /// </summary>
    /// <param name="parts">The part descriptors.</param>
    /// <returns>A part, page, and largest-size summary.</returns>
    private static string DescribePdfParts(IReadOnlyList<PdfPart> parts)
    {
        // Page ranges are authoritative even when a rewritten part's byte size differs from the source encoding.
        var totalPages = parts.Sum(part => part.EndPage - part.StartPage + 1);
        var maxSize = parts.Count == 0 ? 0 : parts.Max(part => part.SizeBytes);
        return $"{parts.Count} part(s), {totalPages} page(s), largest part {FormatBytes(maxSize)}.";
    }

    /// <summary>
    /// Formats bytes as invariant-culture decimal megabytes.
    /// </summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The formatted size.</returns>
    private static string FormatBytes(long bytes)
    {
        // Azure documents its upload limits in decimal MB, so use 1,000,000 bytes rather than MiB.
        var megabytes = bytes / 1_000_000d;
        return string.Create(CultureInfo.InvariantCulture, $"{megabytes:0.##} MB");
    }

    /// <summary>
    /// Classifies each PDF page by the amount of meaningful extractable text it contains.
    /// </summary>
    /// <param name="input">The source PDF path.</param>
    /// <param name="textThreshold">The minimum non-whitespace character count for an extractable page.</param>
    /// <returns>The per-page inspection result.</returns>
    private static PdfInspectionResult InspectPdf(string input, int textThreshold)
    {
        // Classification is page-based because mixed PDFs require OCR only where the text layer is absent or inadequate.
        using var document = PdfDocument.Open(input);
        var pages = new List<PdfPageInspection>();

        foreach (var page in document.GetPages())
        {
            var text = ExtractPageText(page);
            var meaningfulCharacters = CountMeaningfulCharacters(text);
            pages.Add(new PdfPageInspection(
                page.Number,
                NormalizeMarkdownText(text),
                meaningfulCharacters,
                meaningfulCharacters >= textThreshold));
        }

        return new PdfInspectionResult(pages);
    }

    /// <summary>
    /// Builds page-ordered Markdown from extracted text and OCR results in a single Tesseract session.
    /// </summary>
    /// <param name="input">The source PDF path.</param>
    /// <param name="inspection">The page classifications and extracted text.</param>
    /// <param name="options">The OCR and batching options.</param>
    /// <param name="tessdataPath">The validated trained-data directory.</param>
    /// <returns>The combined Markdown document.</returns>
    private static string BuildLocalMixedMarkdown(
        string input,
        PdfInspectionResult inspection,
        PdfConversionOptions options,
        string tessdataPath)
    {
        // Reuse one engine because loading multiple language models is expensive and pages are processed serially.
        using var engine = new TesseractEngine(tessdataPath, options.OcrLanguages, EngineMode.Default)
        {
            DefaultPageSegMode = PageSegMode.Auto
        };

        var markdown = new StringBuilder();
        markdown.AppendLine($"<!-- DOC2MD local PDF processing: {inspection.TextPageCount} extracted page(s), {inspection.OcrPageCount} OCR page(s). -->");
        markdown.AppendLine($"<!-- Source: {Path.GetFileName(input)} -->");
        markdown.AppendLine();

        var batches = GetPageBatches(inspection.Pages, options.MaxPagesPerPart).ToArray();
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
            if (batches.Length > 1)
            {
                markdown.AppendLine($"<!-- DOC2MD local processing part {batchIndex + 1}/{batches.Length}: source pages {batches[batchIndex][0].PageNumber}-{batches[batchIndex][^1].PageNumber}. -->");
                markdown.AppendLine();
            }

            foreach (var page in batches[batchIndex])
            {
                markdown.AppendLine($"<!-- Page {page.PageNumber}: {(page.HasExtractableText ? "extractable text" : "OCR")} -->");
                markdown.AppendLine($"## Page {page.PageNumber}");
                markdown.AppendLine();

                var pageText = page.HasExtractableText
                    ? page.Text
                    : OcrPage(input, page.PageNumber, options, engine);

                if (string.IsNullOrWhiteSpace(pageText))
                {
                    markdown.AppendLine($"<!-- No text was recognized on page {page.PageNumber}. -->");
                }
                else
                {
                    markdown.AppendLine(pageText.Trim());
                }

                markdown.AppendLine();
            }
        }

        return markdown.ToString();
    }

    /// <summary>
    /// Partitions inspected pages into stable source-order batches.
    /// </summary>
    /// <param name="pages">The inspected pages.</param>
    /// <param name="maxPagesPerPart">The maximum batch size.</param>
    /// <returns>The lazily generated batches.</returns>
    private static IEnumerable<IReadOnlyList<PdfPageInspection>> GetPageBatches(
        IReadOnlyList<PdfPageInspection> pages,
        int maxPagesPerPart)
    {
        // Option validation guarantees a positive batch size, preventing a non-advancing iterator.
        for (var i = 0; i < pages.Count; i += maxPagesPerPart)
        {
            yield return pages.Skip(i).Take(maxPagesPerPart).ToArray();
        }
    }

    /// <summary>
    /// Renders one PDF page to a temporary image and recognizes it with an existing Tesseract engine.
    /// </summary>
    /// <param name="input">The source PDF path.</param>
    /// <param name="pageNumber">The one-based source page number.</param>
    /// <param name="options">The render and OCR options.</param>
    /// <param name="engine">The initialized Tesseract engine.</param>
    /// <returns>Normalized recognized text.</returns>
    private static string OcrPage(
        string input,
        int pageNumber,
        PdfConversionOptions options,
        TesseractEngine engine)
    {
        var tempImage = CreateOcrTemporaryImagePath(pageNumber);

        try
        {
            // Grayscale and tiling reduce memory pressure while retaining enough contrast for document OCR.
            var renderOptions = new RenderOptions
            {
                Dpi = options.RenderDpi,
                Grayscale = true,
                UseTiling = true
            };

#pragma warning disable CA1416
            try
            {
                Conversion.SavePng(tempImage, input, new Index(pageNumber - 1), null, renderOptions);
            }
            catch (FormatException)
            {
                // Some browser-readable PDFs are not accepted by PDFtoImage's
                // renderer. Render just the affected page with Docnet instead,
                // then continue through the same Tesseract OCR path.
                RenderPageWithDocnet(tempImage, input, pageNumber - 1);
            }
#pragma warning restore CA1416

            using var pix = Pix.LoadFromFile(tempImage);
            using var page = engine.Process(pix, PageSegMode.Auto);
            return NormalizeMarkdownText(page.GetText() ?? string.Empty);
        }
        finally
        {
            try
            {
                if (File.Exists(tempImage))
                {
                    File.Delete(tempImage);
                }
            }
            catch (IOException)
            {
                // Temporary image cleanup failure should not hide a successful conversion.
            }
        }
    }

    /// <summary>
    /// Creates an ASCII-only OCR image path so native Tesseract file APIs do
    /// not receive source filenames containing unsupported characters.
    /// </summary>
    /// <param name="pageNumber">The one-based PDF page number.</param>
    /// <returns>A unique PNG path below the operating-system temporary folder.</returns>
    internal static string CreateOcrTemporaryImagePath(int pageNumber) =>
        Path.Combine(
            Path.GetTempPath(),
            $"doc2md-{Guid.NewGuid():N}-page-{pageNumber}.png");

    /// <summary>
    /// Renders a PDF page with Docnet when PDFtoImage cannot decode the document.
    /// </summary>
    /// <param name="outputPath">The PNG output path.</param>
    /// <param name="inputPath">The source PDF path.</param>
    /// <param name="zeroBasedPageIndex">The zero-based page index required by Docnet.</param>
    private static void RenderPageWithDocnet(
        string outputPath,
        string inputPath,
        int zeroBasedPageIndex)
    {
        // Fixed fallback dimensions favor readable OCR output without reproducing potentially extreme source page sizes.
        using var document = DocLib.Instance.GetDocReader(
            inputPath,
            new PageDimensions(1700, 2200));
        using var page = document.GetPageReader(zeroBasedPageIndex);
        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var bgra = page.GetImage();
        using var image = Image.LoadPixelData<Bgra32>(bgra, width, height);
        image.Save(outputPath, new PngEncoder());
    }

    /// <summary>
    /// Resolves Tesseract trained data from explicit options, the package layout, or conventional installations.
    /// </summary>
    /// <param name="options">Options containing an optional explicit trained-data path.</param>
    /// <returns>The resolved trained-data directory.</returns>
    private static string ResolveTessdataPath(PdfConversionOptions options)
    {
        // Explicit paths remain authoritative so validation reports the caller's precise configuration error.
        if (!string.IsNullOrWhiteSpace(options.TessdataPath))
        {
            return Path.GetFullPath(options.TessdataPath);
        }

        var candidates = new[]
        {
            Path.Combine(FindRepoRoot(), "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tesseract-OCR", "tessdata"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tesseract-OCR", "tessdata")
        };

        var existing = candidates.FirstOrDefault(Directory.Exists);
        if (existing is not null)
        {
            return existing;
        }

        throw new DirectoryNotFoundException(
            "Tesseract OCR is required for scanned PDF pages, but no tessdata folder was found. " +
            "Set --tessdata or DOC2MD_TESSDATA_PATH to a folder containing eng.traineddata and/or pol.traineddata.");
    }

    /// <summary>
    /// Ensures every requested OCR language has a corresponding trained-data model.
    /// </summary>
    /// <param name="tessdataPath">The trained-data directory.</param>
    /// <param name="languages">The Tesseract language expression.</param>
    private static void ValidateTessdataLanguages(string tessdataPath, string languages)
    {
        if (!Directory.Exists(tessdataPath))
        {
            throw new DirectoryNotFoundException($"Tesseract tessdata folder was not found: {tessdataPath}");
        }

        // Accept Tesseract's plus syntax and common list separators used by configuration systems.
        var missing = languages
            .Split(['+', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(language => !File.Exists(Path.Combine(tessdataPath, $"{language}.traineddata")))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                $"The tessdata folder '{tessdataPath}' is missing trained data for: {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Extracts readable text from a PdfPig page, preferring word boundaries over raw content order.
    /// </summary>
    /// <param name="page">The source page.</param>
    /// <returns>The best available extracted text.</returns>
    private static string ExtractPageText(UglyToad.PdfPig.Content.Page page)
    {
        // Word extraction generally restores spacing that is absent from a PDF's raw text stream.
        var words = page.GetWords()
            .Select(word => word.Text)
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToArray();

        return words.Length > 0
            ? string.Join(" ", words)
            : page.Text ?? string.Empty;
    }

    /// <summary>
    /// Normalizes line endings, removes trailing whitespace, and trims surrounding blank content.
    /// </summary>
    /// <param name="text">The extracted or recognized text.</param>
    /// <returns>Platform-normalized Markdown text.</returns>
    private static string NormalizeMarkdownText(string text)
    {
        // Normalize through LF first so mixed line endings cannot produce duplicate blank lines on Windows.
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd());

        return string.Join(Environment.NewLine, lines).Trim();
    }

    /// <summary>
    /// Counts non-whitespace characters used by the extractable-text threshold.
    /// </summary>
    /// <param name="text">The page text.</param>
    /// <returns>The meaningful character count.</returns>
    private static int CountMeaningfulCharacters(string text) =>
        text.Count(character => !char.IsWhiteSpace(character));

    /// <summary>
    /// Adapts a MarkItDown process tuple to the shared conversion result contract.
    /// </summary>
    /// <param name="result">The process result.</param>
    /// <param name="converter">The converter identifier.</param>
    /// <param name="inspectionSummary">An optional PDF inspection summary.</param>
    /// <returns>The normalized conversion result.</returns>
    private static DocumentConversionResult FromProcessResult(
        (int ExitCode, string stdout, string stderr) result,
        string converter,
        string? inspectionSummary) =>
        new(result.ExitCode, converter, inspectionSummary, result.stdout, result.stderr);

    /// <summary>
    /// Finds the nearest ancestor containing the vendored MarkItDown source layout.
    /// </summary>
    /// <returns>The DOC2MD root, or the current directory when discovery fails.</returns>
    private static string FindRepoRoot()
    {
        // The same marker exists in development checkouts and dependency-complete installer payloads.
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
}

internal sealed record PdfInspectionResult(IReadOnlyList<PdfPageInspection> Pages)
{
    public int PageCount => Pages.Count;

    public int TextPageCount => Pages.Count(page => page.HasExtractableText);

    public int OcrPageCount => Pages.Count - TextPageCount;

    /// <summary>
    /// Formats the page classification totals for logs and structured results.
    /// </summary>
    /// <returns>The inspection summary.</returns>
    public string ToSummary() =>
        $"{PageCount} page(s), {TextPageCount} extractable text page(s), {OcrPageCount} OCR page(s).";
}

internal sealed record PdfPageInspection(
    int PageNumber,
    string Text,
    int MeaningfulCharacterCount,
    bool HasExtractableText);

internal sealed record PdfPart(
    string FilePath,
    int StartPage,
    int EndPage,
    long SizeBytes,
    bool IsTemporary);
