# DOC2MD

DOC2MD is a .NET 10 document-conversion suite for Windows and macOS. It combines the vendored Microsoft MarkItDown source, local PDF inspection and native Tesseract OCR, LibreOffice modernization, and optional Azure AI Document Intelligence processing.

The solution and installers contain four cross-platform frontends:

- `DOC2MD.Gui`: Avalonia desktop application
- `DOC2MD.Cli`: command-line converter and shared process boundary
- `DOC2MD.Api`: ASP.NET Core HTTP API
- `DOC2MD.Mcp`: stdio MCP server

## Runtime requirements

LibreOffice is required for every DOC2MD conversion. DOC2MD checks that `soffice --headless --version` starts successfully before processing input and returns an error when it cannot be found.

Standard locations are detected automatically:

- Windows installer payload and `Program Files\LibreOffice\program\soffice.exe`
- `/Applications/LibreOffice.app/Contents/MacOS/soffice`
- Homebrew paths on Apple Silicon and Intel macOS
- `soffice` on `PATH`

Set `DOC2MD_SOFFICE_PATH` when LibreOffice is installed elsewhere. The value may be the executable or the LibreOffice installation root.

## Installers

### Windows x64

The Windows installer is self-contained and includes all four frontends, the .NET 10 runtime, Python and MarkItDown, native Tesseract OCR, English and Polish trained data, and LibreOffice:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1 -Version 1.0.0
```

The output is `artifacts\installer\DOC2MD-<version>-win-x64-Setup.exe`.

### macOS

DOC2MD requires macOS 14 or newer. Install LibreOffice in `/Applications` before running the DOC2MD installer. The package preinstall check stops with an error when LibreOffice is absent.

Build the installer on a machine matching the target architecture:

```bash
brew install tesseract dylibbundler
brew install --cask libreoffice
./installer/Build-MacInstaller.sh --version 1.0.0 --runtime osx-arm64
```

Use `osx-x64` on an Intel Mac. The output is `artifacts/installer/DOC2MD-<version>-<runtime>.pkg`. It installs:

- `/Applications/DOC2MD.app`
- `/usr/local/bin/doc2md`
- `/usr/local/bin/doc2md-api`
- `/usr/local/bin/doc2md-mcp`

Opening `DOC2MD.app` starts the Avalonia GUI. The bundle also contains the CLI, API, and MCP executables. Python, MarkItDown, tessdata, Tesseract and its native dylibs, documentation, and supporting files are stored under `DOC2MD.app/Contents/Resources`.

For signed distribution, set `DOC2MD_CODESIGN_IDENTITY` and `DOC2MD_INSTALLER_SIGN_IDENTITY` while building. Public packages should also be submitted to Apple's notarization service.

## Commands

Start the desktop GUI from a development checkout:

```bash
dotnet run --project src/DOC2MD.Gui
```

Check runtime discovery:

```bash
doc2md check-dependencies --json
```

Convert one file:

```bash
doc2md convert --input /path/to/file.pdf --output /path/to/file.md --overwrite
```

Convert a folder, writing Markdown beside each source file:

```bash
doc2md convert-folder --input /path/to/documents --recursive --overwrite --continue-on-error
```

Start the HTTP API or MCP server from an installed macOS bundle with `doc2md-api` and `doc2md-mcp`. During development, use:

```bash
dotnet run --project src/DOC2MD.Api
dotnet run --project src/DOC2MD.Mcp
```

All three secondary frontends resolve a sibling CLI automatically. Set `DOC2MD_CLI_PATH` only when deploying a frontend separately from the CLI.

Supported extensions are `.pdf`, `.doc`, `.docx`, `.docm`, `.xlsx`, `.xls`, `.xlsm`, `.pptx`, `.ppt`, `.pptm`, `.rtf`, `.odt`, `.ods`, `.odp`, `.txt`, `.text`, `.csv`, `.html`, `.htm`, and `.epub`.

Legacy, macro-enabled, RTF, and OpenDocument inputs are modernized with headless LibreOffice before MarkItDown conversion:

| Source | Modernized output |
| --- | --- |
| `.doc`, `.docm`, `.rtf`, `.odt` | `.docx` |
| `.xls`, `.xlsm`, `.ods` | `.xlsx` |
| `.ppt`, `.pptm`, `.odp` | `.pptx` |

## Application resources

Runtime discovery uses a platform-neutral resource root. It does not search parent folders for a repository checkout.

The normal layouts are:

- Windows: `<install>/Resources`
- macOS: `DOC2MD.app/Contents/Resources`
- Development build: `<output>/Resources`

Set `DOC2MD_RESOURCE_ROOT` to override the complete resource root. Its relevant children are:

```text
Resources/
  markitdown/
    pyproject.toml
    src/
  python/
  tessdata/
    eng.traineddata
    pol.traineddata
  tesseract/
```

## MarkItDown and Python

MarkItDown requires Python 3.10 or newer. DOC2MD resolves it in this order:

1. `DOC2MD_MARKITDOWN_COMMAND`
2. bundled Python below the resource root
3. the per-user virtual environment created by `install-markitdown`
4. `markitdown` on `PATH`
5. `python3` or `python` with `PYTHONPATH` set to `Resources/markitdown/src`

Create the per-user fallback environment when running a development build without packaged Python:

```bash
dotnet run --project src/DOC2MD.Cli -- install-markitdown --python python3 --json
```

The environment is stored in the user's DOC2MD application-data directory, not in the repository or installed application bundle.

## PDF processing and OCR

Local PDF processing is the default:

1. PdfPig checks every page for extractable text.
2. Fully extractable PDFs are converted with MarkItDown.
3. Image-only pages are rendered with PDFium and recognized by the packaged native Tesseract executable.

The default OCR languages are `eng+pol`. Override the native executable with `DOC2MD_TESSERACT_PATH` and trained data with `--tessdata` or `DOC2MD_TESSDATA_PATH`.

Useful options include:

- `--pdf-processing local|azure|markitdown`
- `--ocr-languages eng+pol`
- `--tessdata <folder>`
- `--pdf-text-threshold 40`
- `--pdf-render-dpi 300`
- `--pdf-max-pages-per-part 100`
- `--pdf-max-part-size-mb 100`

## Azure Document Intelligence

Configure Azure Layout processing:

```bash
export DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY='<key>'
doc2md configure-azure \
  --endpoint 'https://<resource>.cognitiveservices.azure.com/' \
  --tier s0 \
  --json
unset DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY
```

The API key is protected with Windows DPAPI on Windows and the current user's Keychain on macOS. Only a protected reference is stored in the JSON settings file. Environment variables remain authoritative for automation.

## Development

Install the .NET 10 SDK, then build and test the complete solution on Windows or macOS:

```bash
dotnet build DOC2MD.slnx --configuration Release
dotnet test DOC2MD.slnx --configuration Release
```

The ordinary test command runs fast unit tests and reports the external-runtime sample tests as skipped. Run the three real sample conversions explicitly after installing LibreOffice, Python/MarkItDown, and Tesseract:

```bash
DOC2MD_RUN_SAMPLE_TESTS=1 \
  dotnet test tests/DOC2MD.Integration.Tests/DOC2MD.Integration.Tests.csproj --configuration Release
```

Set `DOC2MD_SAMPLE_TEST_CLI` and `DOC2MD_SAMPLE_TEST_TESSDATA` to test an installer-staged CLI and OCR models. Release CI sets both variables and validates `example-cv.docx`, `examples-download.pdf`, and `example-ebook.pdf` against the packaged application.

Release CI builds and smoke-tests the complete Windows x64, macOS Apple Silicon, and macOS Intel installers before attaching them to the same GitHub release.
