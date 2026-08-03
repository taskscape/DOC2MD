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

    public static PdfConversionOptions FromArgs(string[] args)
    {
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

    private static void ValidateSingleProcessingStack(string[] args, PdfProcessingMode mode)
    {
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

    private static PdfSplittingMode ParseSplittingMode(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "auto" => PdfSplittingMode.Auto,
            "always" => PdfSplittingMode.Always,
            "never" => PdfSplittingMode.Never,
            _ => throw new ArgumentException("--pdf-splitting must be one of: auto, always, never.")
        };

    private static int DefaultMaxPagesPerPart(PdfProcessingMode mode, AzureDocumentIntelligenceTier azureTier) =>
        mode == PdfProcessingMode.Azure
            ? azureTier == AzureDocumentIntelligenceTier.FreeF0 ? 2 : 100
            : 100;

    private static int DefaultMaxPartSizeMb(PdfProcessingMode mode, AzureDocumentIntelligenceTier azureTier) =>
        mode == PdfProcessingMode.Azure
            ? azureTier == AzureDocumentIntelligenceTier.FreeF0 ? 3 : 100
            : 100;

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

    private static int IntValue(string? value, int defaultValue, string optionName)
    {
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

    public static async Task<DocumentConversionResult> ConvertAsync(
        string input,
        string output,
        PdfConversionOptions options,
        Func<string, string, Task<(int ExitCode, string stdout, string stderr)>> runMarkItDownAsync)
    {
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

    private static async Task<DocumentConversionResult> ConvertAzurePdfAsync(
        string input,
        string output,
        PdfConversionOptions options)
    {
        var client = CreateDocumentIntelligenceClient(options);
        var parts = CreateAzurePdfParts(input, options);

        try
        {
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
            DeleteTemporaryParts(parts);
        }
    }

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

        var operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions);
        return operation.Value.Content ?? string.Empty;
    }

    private static DocumentIntelligenceClient CreateDocumentIntelligenceClient(PdfConversionOptions options)
    {
        var endpoint = new Uri(options.AzureEndpoint!);
        if (!string.IsNullOrWhiteSpace(options.AzureKey))
        {
            return new DocumentIntelligenceClient(endpoint, new AzureKeyCredential(options.AzureKey));
        }

        TokenCredential credential = new DefaultAzureCredential();
        return new DocumentIntelligenceClient(endpoint, credential);
    }

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

                candidatePageCount = Math.Max(1, candidatePageCount / 2);
            }
        }

        return parts;
    }

    private static PdfPart CreatePdfPart(PdfDocument document, int startPage, int endPage)
    {
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

    private static void DeleteTemporaryParts(IEnumerable<PdfPart> parts)
    {
        foreach (var part in parts)
        {
            DeleteTemporaryPart(part);
        }
    }

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

    private static string DescribePdfParts(IReadOnlyList<PdfPart> parts)
    {
        var totalPages = parts.Sum(part => part.EndPage - part.StartPage + 1);
        var maxSize = parts.Count == 0 ? 0 : parts.Max(part => part.SizeBytes);
        return $"{parts.Count} part(s), {totalPages} page(s), largest part {FormatBytes(maxSize)}.";
    }

    private static string FormatBytes(long bytes)
    {
        var megabytes = bytes / 1_000_000d;
        return string.Create(CultureInfo.InvariantCulture, $"{megabytes:0.##} MB");
    }

    private static PdfInspectionResult InspectPdf(string input, int textThreshold)
    {
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

    private static string BuildLocalMixedMarkdown(
        string input,
        PdfInspectionResult inspection,
        PdfConversionOptions options,
        string tessdataPath)
    {
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

    private static IEnumerable<IReadOnlyList<PdfPageInspection>> GetPageBatches(
        IReadOnlyList<PdfPageInspection> pages,
        int maxPagesPerPart)
    {
        for (var i = 0; i < pages.Count; i += maxPagesPerPart)
        {
            yield return pages.Skip(i).Take(maxPagesPerPart).ToArray();
        }
    }

    private static string OcrPage(
        string input,
        int pageNumber,
        PdfConversionOptions options,
        TesseractEngine engine)
    {
        var tempImage = Path.Combine(
            Path.GetTempPath(),
            $"doc2md-{Path.GetFileNameWithoutExtension(input)}-{Guid.NewGuid():N}-page-{pageNumber}.png");

        try
        {
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

    private static void RenderPageWithDocnet(
        string outputPath,
        string inputPath,
        int zeroBasedPageIndex)
    {
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

    private static string ResolveTessdataPath(PdfConversionOptions options)
    {
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

    private static void ValidateTessdataLanguages(string tessdataPath, string languages)
    {
        if (!Directory.Exists(tessdataPath))
        {
            throw new DirectoryNotFoundException($"Tesseract tessdata folder was not found: {tessdataPath}");
        }

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

    private static string ExtractPageText(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords()
            .Select(word => word.Text)
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .ToArray();

        return words.Length > 0
            ? string.Join(" ", words)
            : page.Text ?? string.Empty;
    }

    private static string NormalizeMarkdownText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd());

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static int CountMeaningfulCharacters(string text) =>
        text.Count(character => !char.IsWhiteSpace(character));

    private static DocumentConversionResult FromProcessResult(
        (int ExitCode, string stdout, string stderr) result,
        string converter,
        string? inspectionSummary) =>
        new(result.ExitCode, converter, inspectionSummary, result.stdout, result.stderr);

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
}

internal sealed record PdfInspectionResult(IReadOnlyList<PdfPageInspection> Pages)
{
    public int PageCount => Pages.Count;

    public int TextPageCount => Pages.Count(page => page.HasExtractableText);

    public int OcrPageCount => Pages.Count - TextPageCount;

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
