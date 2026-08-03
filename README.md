# DOC2MD

DOC2MD is a high fidelity converter of different office documents to markdown format. It provides CLI, API, MCP interfaces wrapping around the vendored Microsoft MarkItDown source in `lib`, Microsoft Document Intelligence and open source OCR/PDF conversion libraries.

## Projects

- `DOC2MD.Cli` converts one document or a folder of supported documents.
- `DOC2MD.Gui` is a Windows Forms front end that calls the CLI.
- `DOC2MD.Api` is an ASP.NET Core REST API suitable for IIS hosting and calls the CLI.
- `DOC2MD.Mcp` is a stdio MCP server exposing the same conversion operations as tools.

## Windows installer

Build the complete Windows x64 installer from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Version 1.0.0
```

The script publishes all four .NET projects as framework-dependent executables, creates a portable Python runtime with `markitdown[all]`, downloads English and Polish Tesseract trained-data models, copies a complete LibreOffice runtime, and compiles `artifacts\installer\DOC2MD-<version>-win-x64-Setup.exe` with Inno Setup. Pass `-LibreOfficePath` when LibreOffice is not installed in its standard `Program Files` location, and `-IsccPath` when `ISCC.exe` is not discoverable automatically.

The installer contains the Python runtime and libraries, both OCR models, LibreOffice, and the native/managed libraries produced by `dotnet publish`. It does not contain or install .NET. Install the .NET 8 Desktop Runtime (which also supplies the base .NET runtime) and the ASP.NET Core 8 Runtime if the API executable will be used.

Installed entry points are `DOC2MD.Gui.exe`, `DOC2MD.Cli.exe`, `DOC2MD.Api.exe`, and `DOC2MD.Mcp.exe`. The installer adds a Start menu shortcut for the GUI and can optionally add the install directory to the current user's `PATH`.

Every push to `main` or `master` runs `.github\workflows\publish-installer.yml`. The workflow assigns version `1.0.<run-number>`, builds and verifies the complete installer, creates a SHA-256 checksum, and publishes both files in a non-draft GitHub Release tagged `v1.0.<run-number>`. Re-running the same workflow run updates the existing release assets without assigning a different version.

## MarkItDown on Windows without Docker

MarkItDown does not require Docker for local Windows use. The upstream README says it requires Python 3.10 or newer and can be installed with `pip install 'markitdown[all]'` or from source with `pip install -e 'packages/markitdown[all]'`. This wrapper uses that path instead of Docker.

Initialize the local virtual environment:

```powershell
dotnet run --project .\src\DOC2MD.Cli -- install-markitdown --json
```

The CLI then resolves MarkItDown in this order:

1. `DOC2MD_MARKITDOWN_COMMAND`
2. `.markitdown-venv\Scripts\python.exe`
3. `markitdown` on `PATH`
4. `python -m markitdown` with `PYTHONPATH` pointed at `lib\packages\markitdown\src`

## CLI

Convert one file:

```powershell
dotnet run --project .\src\DOC2MD.Cli -- convert --input C:\Docs\file.pdf --output C:\Docs\file.md --overwrite
```

Convert a folder, writing `.md` files beside source documents:

```powershell
dotnet run --project .\src\DOC2MD.Cli -- convert-folder --input C:\Docs --recursive --overwrite --continue-on-error
```

Folder conversion is intentionally limited to typical document files. DOC2MD currently scans only these extensions:

| Category | Extensions |
| --- | --- |
| PDF | `.pdf` |
| Word processing | `.doc`, `.docx`, `.docm`, `.rtf`, `.odt` |
| Spreadsheets | `.xls`, `.xlsx`, `.xlsm`, `.ods`, `.csv` |
| Presentations | `.ppt`, `.pptx`, `.pptm`, `.odp` |
| Text | `.txt`, `.text` |
| Web documents | `.html`, `.htm` |
| E-books | `.epub` |

The folder scanner skips Markdown output/source files (`.md`, `.markdown`) and configuration/data/code/media/archive formats such as `.json`, `.xml`, `.jpg`, `.png`, `.mp3`, `.wav`, `.zip`, `.msg`, and `.ipynb`.

Legacy Office, macro-enabled Office, RTF, and OpenDocument files are modernized before Markdown conversion:

| Source extensions | Modernized side-by-side file |
| --- | --- |
| `.doc`, `.docm`, `.rtf`, `.odt` | `.docx` |
| `.xls`, `.xlsm`, `.ods` | `.xlsx` |
| `.ppt`, `.pptm`, `.odp` | `.pptx` |

DOC2MD uses LibreOffice headless as the default modernization prerequisite. It looks for the runtime bundled by the Windows installer, `soffice.exe` on `PATH`, the usual `Program Files\LibreOffice\program` locations, or `DOC2MD_SOFFICE_PATH`. `DOC2MD_SOFFICE_PATH` can point either to `soffice.exe` or to the LibreOffice installation root.

If LibreOffice is present, DOC2MD first saves the old-format document beside the source in the modern format and then converts the modernized copy to Markdown. If the modernized file already exists beside the source, DOC2MD reuses it instead of overwriting it.

If LibreOffice is not present, DOC2MD reports a warning for each old-format file and skips those files. The rest of the folder conversion continues.

## PDF processing

PDFs default to local processing:

1. PdfPig inspects every page for extractable text.
2. If every page has enough extractable text, the CLI keeps the existing MarkItDown conversion path.
3. If any page lacks extractable text, the CLI writes one Markdown file in page order. Text pages are extracted with PdfPig and image-only pages are rendered with PDFtoImage/PDFium and OCRed with Tesseract.

The default OCR language setting is `eng+pol`.

Local OCR needs Tesseract trained data. Put `eng.traineddata` and/or `pol.traineddata` in one of these locations:

- `.\tessdata`
- `%ProgramFiles%\Tesseract-OCR\tessdata`
- a custom folder passed with `--tessdata` or `DOC2MD_TESSDATA_PATH`

Use Azure AI Document Intelligence Layout instead:

```powershell
$env:DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY = "<key>"
dotnet run --project .\src\DOC2MD.Cli -- configure-azure `
  --endpoint https://<resource>.cognitiveservices.azure.com/ `
  --tier s0
Remove-Item Env:\DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY

dotnet run --project .\src\DOC2MD.Cli -- convert `
  --input C:\Docs\scan.pdf `
  --output C:\Docs\scan.md `
  --pdf-processing azure `
  --overwrite
```

`configure-azure` protects the key with Windows DPAPI for the current user and stores it outside the repository in `%APPDATA%\DOC2MD\settings.json`. The CLI, GUI, API, and MCP wrappers all call the CLI, so they can use the configured key without putting it on the command line. A key can still be supplied with `--azure-document-intelligence-key` or `DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY` when needed, but the wrappers avoid forwarding request keys as CLI arguments.

Use raw MarkItDown PDF handling:

```powershell
dotnet run --project .\src\DOC2MD.Cli -- convert --input C:\Docs\file.pdf --output C:\Docs\file.md --pdf-processing markitdown
```

Only one PDF stack can be selected per conversion. The CLI rejects Azure options unless `--pdf-processing azure` is selected, and rejects local OCR options unless `--pdf-processing local` is selected.

### PDF size and page limits

Azure AI Document Intelligence limits are service limits. Microsoft documents the current v4.0 limits in [Service quotas and limits](https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits?view=doc-intel-4.0.0) and [Layout model input requirements](https://learn.microsoft.com/azure/ai-services/document-intelligence/prebuilt/layout?view=doc-intel-4.0.0#input-requirements):

| Processing stack | Hard limits | DOC2MD default behavior |
| --- | --- | --- |
| Azure Document Intelligence S0 | 500 MB per document, 2,000 PDF/TIFF pages, image dimensions from 50 x 50 to 10,000 x 10,000 pixels. | Splits PDFs into parts of at most 100 pages and 100 MB, analyzes each part, then merges the Markdown in source page order. |
| Azure Document Intelligence F0 | 4 MB per document, only the first 2 pages are processed. | Select with `--azure-document-intelligence-tier f0`; DOC2MD splits to at most 2 pages and 3 MB per part. Be aware each part is still subject to F0 billing/allowance behavior. |
| Local PdfPig/PDFtoImage/Tesseract | No fixed cloud-service file-size or page-count limit. Practical limits are local RAM, disk, PDF complexity, page dimensions, render DPI, and installed Tesseract trained data. | Processes OCR one page at a time from the PDF path instead of loading the whole PDF into memory. Large local PDFs are batched by page for merged Markdown output; default batch size is 100 pages. |
| MarkItDown fallback | No DOC2MD-specific hard limit. Limits depend on MarkItDown, Python packages, file type, and local resources. | Used for non-PDF files and for PDFs where local inspection finds all pages already extractable. |

When Azure splitting is enabled, DOC2MD creates temporary page-range PDFs, sends each part to Document Intelligence Layout, and writes one final Markdown file. The final Markdown includes comments showing which original source pages each split part came from. Temporary split files are deleted after conversion.

Useful PDF options:

- `--pdf-processing local|azure|markitdown`
- `--ocr-languages eng+pol`
- `--tessdata C:\Path\To\tessdata`
- `--pdf-text-threshold 40`
- `--pdf-render-dpi 300`
- `--pdf-splitting always`
- `--pdf-max-pages-per-part 100`
- `--pdf-max-part-size-mb 100`
- `--azure-document-intelligence-endpoint <url>`
- `--azure-document-intelligence-key <key>` (prefer `configure-azure` for local Windows use)
- `--azure-document-intelligence-locale <locale>`
- `--azure-document-intelligence-tier f0|s0`

Environment equivalents:

- `DOC2MD_PDF_PROCESSING`
- `DOC2MD_OCR_LANGUAGES`
- `DOC2MD_TESSDATA_PATH`
- `DOC2MD_PDF_TEXT_THRESHOLD`
- `DOC2MD_PDF_RENDER_DPI`
- `DOC2MD_PDF_SPLITTING`
- `DOC2MD_PDF_MAX_PAGES_PER_PART`
- `DOC2MD_PDF_MAX_PART_SIZE_MB`
- `DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT`
- `DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY`
- `DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_LOCALE`
- `DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_TIER`

## API

Run locally:

```powershell
dotnet run --project .\src\DOC2MD.Api
```

Endpoints:

- `GET /health`
- `POST /convert` with `{ "inputPath": "...", "outputPath": "...", "overwrite": true }`
- `POST /convert-folder` with `{ "inputFolder": "...", "recursive": true, "overwrite": true, "continueOnError": true }`

Both conversion endpoints also accept the optional PDF fields `pdfProcessing`, `ocrLanguages`, `tessdataPath`, `pdfTextThreshold`, `pdfRenderDpi`, `pdfSplitting`, `pdfMaxPagesPerPart`, `pdfMaxPartSizeMb`, `azureDocumentIntelligenceEndpoint`, `azureDocumentIntelligenceKey`, `azureDocumentIntelligenceLocale`, and `azureDocumentIntelligenceTier`.

For IIS, publish `DOC2MD.Api` and set `DOC2MD_CLI_PATH` to the published or built `DOC2MD.Cli.exe`.

## MCP

Run the MCP server over stdio:

```powershell
dotnet run --project .\src\DOC2MD.Mcp
```

Tools:

- `convert_document`
- `convert_folder`

Both tools accept the same optional PDF settings as the API.

Set `DOC2MD_CLI_PATH` if the MCP server cannot discover the CLI executable.
