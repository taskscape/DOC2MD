using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DOC2MD.Gui;

public partial class Form1 : Form
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc",
        ".docx",
        ".docm",
        ".xlsx",
        ".xls",
        ".xlsm",
        ".pptx",
        ".ppt",
        ".pptm",
        ".rtf",
        ".odt",
        ".ods",
        ".odp",
        ".txt",
        ".text",
        ".csv",
        ".html",
        ".htm",
        ".epub"
    };

    private static readonly HashSet<string> ModernizedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc",
        ".docm",
        ".rtf",
        ".odt",
        ".xls",
        ".xlsm",
        ".ods",
        ".ppt",
        ".pptm",
        ".odp"
    };

    private readonly RadioButton _singleFile = new() { Text = "Single document", Checked = true, AutoSize = true, MinimumSize = new Size(230, 42) };
    private readonly RadioButton _folder = new() { Text = "Folder", AutoSize = true, MinimumSize = new Size(130, 42) };
    private readonly TextBox _input = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8) };
    private readonly TextBox _output = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8) };
    private readonly CheckBox _recursive = new() { Text = "Recursive", AutoSize = true, MinimumSize = new Size(150, 42), Enabled = false };
    private readonly CheckBox _overwrite = new() { Text = "Overwrite existing markdown", AutoSize = true, MinimumSize = new Size(330, 42) };
    private readonly ComboBox _pdfProcessing = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(6, 8, 6, 8) };
    private readonly TextBox _ocrLanguages = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8) };
    private readonly TextBox _tessdata = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8) };
    private readonly TextBox _azureEndpoint = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8) };
    private readonly TextBox _azureKey = new() { Dock = DockStyle.Fill, Margin = new Padding(6, 8, 6, 8), UseSystemPasswordChar = true, PlaceholderText = "Configured key is used automatically" };
    private readonly Button _browseInput = new() { Text = "Browse", Dock = DockStyle.Fill, MinimumSize = new Size(150, 44), Margin = new Padding(8, 6, 0, 6) };
    private readonly Button _browseOutput = new() { Text = "Browse", Dock = DockStyle.Fill, MinimumSize = new Size(150, 44), Margin = new Padding(8, 6, 0, 6) };
    private readonly Button _browseTessdata = new() { Text = "Browse", Dock = DockStyle.Fill, MinimumSize = new Size(150, 44), Margin = new Padding(8, 6, 0, 6) };
    private readonly Button _convert = new() { Text = "Convert", Dock = DockStyle.Fill, MinimumSize = new Size(150, 44), Margin = new Padding(8, 6, 0, 6) };
    private readonly Button _cancel = new() { Text = "Cancel", Dock = DockStyle.Fill, MinimumSize = new Size(150, 42), Enabled = false, Margin = new Padding(10, 5, 0, 5) };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        ReadOnly = true,
        Dock = DockStyle.Fill,
        Margin = new Padding(18, 0, 18, 0)
    };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1, Margin = new Padding(8, 12, 8, 12) };
    private readonly Label _status = new() { Text = "Ready", AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 8, 0) };

    private CancellationTokenSource? _operationCts;
    private Process? _currentProcess;

    /// <summary>
    /// Initializes the DOC2MD desktop form and its programmatically composed controls.
    /// </summary>
    public Form1()
    {
        // The designer owns only the base form; BuildUi composes the responsive operational interface.
        InitializeComponent();
        BuildUi();
    }

    /// <summary>
    /// Configures the main window, composes its layout, and wires user interactions.
    /// </summary>
    private void BuildUi()
    {
        // Fixed command/status heights protect labels and controls from clipping while the log absorbs resizing.
        Text = "DOC2MD";
        Font = new Font("Segoe UI", 11F);
        MinimumSize = new Size(1080, 820);
        Size = new Size(1280, 860);

        _log.Font = new Font("Consolas", 9.5F);
        ConfigurePdfDefaults();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 504));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        root.Controls.Add(BuildCommandPanel(), 0, 0);
        root.Controls.Add(_log, 0, 1);
        root.Controls.Add(BuildStatusPanel(), 0, 2);

        Controls.Add(root);

        _singleFile.CheckedChanged += (_, _) => ToggleMode();
        _folder.CheckedChanged += (_, _) => ToggleMode();
        _browseInput.Click += (_, _) => BrowseInput();
        _browseOutput.Click += (_, _) => BrowseOutput();
        _browseTessdata.Click += (_, _) => BrowseTessdata();
        _pdfProcessing.SelectedIndexChanged += (_, _) => TogglePdfOptions();
        _convert.Click += async (_, _) => await ConvertAsync();
        _cancel.Click += (_, _) => CancelOperation();
        TogglePdfOptions();
    }

    /// <summary>
    /// Builds the document, PDF-processing, and conversion command controls.
    /// </summary>
    /// <returns>The composed command panel.</returns>
    private Control BuildCommandPanel()
    {
        // The explicit row geometry mirrors the minimum form size established in BuildUi.
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            Padding = new Padding(18, 12, 20, 12)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var modePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 0)
        };
        modePanel.Controls.Add(_singleFile);
        modePanel.Controls.Add(_folder);
        panel.Controls.Add(modePanel, 0, 0);
        panel.SetColumnSpan(modePanel, 3);

        panel.Controls.Add(CreateRowLabel("Input"), 0, 1);
        panel.Controls.Add(_input, 1, 1);
        panel.Controls.Add(_browseInput, 2, 1);

        panel.Controls.Add(CreateRowLabel("Output"), 0, 2);
        panel.Controls.Add(_output, 1, 2);
        panel.Controls.Add(_browseOutput, 2, 2);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(6, 7, 0, 0)
        };
        options.Controls.Add(_recursive);
        options.Controls.Add(_overwrite);
        panel.Controls.Add(options, 1, 3);
        panel.Controls.Add(_convert, 2, 3);

        panel.Controls.Add(CreateRowLabel("PDF processing"), 0, 4);
        panel.Controls.Add(_pdfProcessing, 1, 4);

        panel.Controls.Add(CreateRowLabel("OCR languages"), 0, 5);
        panel.Controls.Add(_ocrLanguages, 1, 5);

        panel.Controls.Add(CreateRowLabel("Tessdata"), 0, 6);
        panel.Controls.Add(_tessdata, 1, 6);
        panel.Controls.Add(_browseTessdata, 2, 6);

        panel.Controls.Add(CreateRowLabel("Azure endpoint"), 0, 7);
        panel.Controls.Add(_azureEndpoint, 1, 7);

        panel.Controls.Add(CreateRowLabel("Azure key"), 0, 8);
        panel.Controls.Add(_azureKey, 1, 8);

        return panel;
    }

    /// <summary>
    /// Builds the status label, progress bar, and cancellation controls.
    /// </summary>
    /// <returns>The composed status panel.</returns>
    private Control BuildStatusPanel()
    {
        // Status and cancel use fixed widths so progress receives all additional horizontal space.
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 4, 20, 8)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        panel.Controls.Add(_status, 0, 0);
        panel.Controls.Add(_progress, 1, 0);
        panel.Controls.Add(_cancel, 2, 0);
        return panel;
    }

    /// <summary>
    /// Creates a consistently aligned command-panel label.
    /// </summary>
    /// <param name="text">The label text.</param>
    /// <returns>The configured label.</returns>
    private static Label CreateRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 10, 0)
    };

    /// <summary>
    /// Applies the single-document or folder-mode control state.
    /// </summary>
    private void ToggleMode()
    {
        // Mode-dependent controls remain disabled while a child process is active to keep arguments immutable.
        var busy = _operationCts is not null;
        _recursive.Enabled = _folder.Checked && !busy;
        _output.Enabled = _singleFile.Checked && !busy;
        _browseOutput.Enabled = _singleFile.Checked && !busy;

        if (_folder.Checked)
        {
            _output.Text = "";
        }
    }

    /// <summary>
    /// Enables only the controls relevant to the selected PDF processing stack.
    /// </summary>
    private void TogglePdfOptions()
    {
        // Local and Azure options are mutually exclusive because the CLI rejects mixed-stack arguments.
        var busy = _operationCts is not null;
        var mode = SelectedPdfProcessing();
        var local = mode == "local" && !busy;
        var azure = mode == "azure" && !busy;

        _pdfProcessing.Enabled = !busy;
        _ocrLanguages.Enabled = local;
        _tessdata.Enabled = local;
        _browseTessdata.Enabled = local;
        _azureEndpoint.Enabled = azure;
        _azureKey.Enabled = azure;
    }

    /// <summary>
    /// Opens the mode-appropriate input picker and derives a single-file output path.
    /// </summary>
    private void BrowseInput()
    {
        // Folder mode deliberately leaves output implicit because Markdown files are written beside each source.
        if (_folder.Checked)
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _input.Text = dialog.SelectedPath;
            }

            return;
        }

        using var open = new OpenFileDialog
        {
            Filter = "All supported documents|*.pdf;*.doc;*.docx;*.docm;*.xls;*.xlsx;*.xlsm;*.ppt;*.pptx;*.pptm;*.rtf;*.odt;*.ods;*.odp;*.txt;*.text;*.csv;*.html;*.htm;*.epub"
        };
        if (open.ShowDialog(this) == DialogResult.OK)
        {
            _input.Text = open.FileName;
            _output.Text = Path.ChangeExtension(open.FileName, ".md");
        }
    }

    /// <summary>
    /// Prompts for the Markdown output path used by single-document conversion.
    /// </summary>
    private void BrowseOutput()
    {
        // The output picker is unused in folder mode and ToggleMode keeps it disabled there.
        using var save = new SaveFileDialog { Filter = "Markdown|*.md|All files|*.*", DefaultExt = "md" };
        if (save.ShowDialog(this) == DialogResult.OK)
        {
            _output.Text = save.FileName;
        }
    }

    /// <summary>
    /// Prompts for a Tesseract trained-data directory.
    /// </summary>
    private void BrowseTessdata()
    {
        // Language model presence is validated by the CLI so the picker remains a generic folder selector.
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _tessdata.Text = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Owns one GUI conversion operation, including busy state, cancellation, and user-visible error handling.
    /// </summary>
    private async Task ConvertAsync()
    {
        // A single operation is allowed because the form tracks only one child process and one progress sequence.
        if (_operationCts is not null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true);
        _log.Clear();
        ResetProgress(1);

        try
        {
            if (_singleFile.Checked)
            {
                await ConvertSingleFileAsync(cts.Token);
            }
            else
            {
                await ConvertFolderAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Operation cancelled.");
            _status.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            // The GUI is a user boundary, so command/setup errors are logged rather than allowed to terminate WinForms.
            AppendLog("ERROR: " + ex.Message);
            _status.Text = "Failed";
        }
        finally
        {
            _operationCts = null;
            _currentProcess = null;
            SetBusy(false);
        }
    }

    /// <summary>
    /// Builds and executes the CLI command for the selected single document.
    /// </summary>
    /// <param name="cancellationToken">Cancels the child CLI process.</param>
    private async Task ConvertSingleFileAsync(CancellationToken cancellationToken)
    {
        // Validation occurs before command construction so quoted empty values never reach the CLI parser.
        if (string.IsNullOrWhiteSpace(_input.Text))
        {
            throw new InvalidOperationException("Input file is required.");
        }

        if (string.IsNullOrWhiteSpace(_output.Text))
        {
            throw new InvalidOperationException("Output file is required.");
        }

        var args = $"convert --input {Quote(_input.Text)} --output {Quote(_output.Text)} --json";
        if (_overwrite.Checked)
        {
            args += " --overwrite";
        }

        args += BuildPdfArguments();
        _status.Text = "Converting 1 of 1";
        var result = await ExecuteCliCommandAsync(args, cancellationToken);
        SetProgressValue(1);
        _status.Text = result.exitCode == 0 ? "Completed" : "Failed";
    }

    /// <summary>
    /// Converts selected folder inputs sequentially while updating deterministic progress.
    /// </summary>
    /// <param name="cancellationToken">Cancels the active or next child CLI process.</param>
    private async Task ConvertFolderAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_input.Text))
        {
            throw new InvalidOperationException("Input folder is required.");
        }

        if (!Directory.Exists(_input.Text))
        {
            throw new DirectoryNotFoundException($"Input folder was not found: {_input.Text}");
        }

        // The GUI enumerates files itself to provide per-file progress and cancellation between CLI invocations.
        var files = SelectFolderConversionInputs(Directory.EnumerateFiles(_input.Text, "*.*", _recursive.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .Where(file => !Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase));

        if (files.Length == 0)
        {
            AppendLog("No supported source documents were found.");
            _status.Text = "No files";
            ResetProgress(1);
            return;
        }

        ResetProgress(files.Length);
        var failures = 0;

        for (var i = 0; i < files.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputFile = files[i];
            var outputFile = Path.ChangeExtension(inputFile, ".md");
            _status.Text = $"Converting {i + 1} of {files.Length}";

            if (File.Exists(outputFile) && !_overwrite.Checked)
            {
                AppendLog($"SKIP: {outputFile} already exists. Enable overwrite to replace it.");
                SetProgressValue(i + 1);
                continue;
            }

            var args = $"convert --input {Quote(inputFile)} --output {Quote(outputFile)} --json";
            if (_overwrite.Checked)
            {
                args += " --overwrite";
            }

            args += BuildPdfArguments();
            var result = await ExecuteCliCommandAsync(args, cancellationToken);
            if (result.exitCode != 0)
            {
                failures++;
            }

            SetProgressValue(i + 1);
        }

        _status.Text = failures == 0 ? $"Completed {files.Length} files" : $"Completed with {failures} failure(s)";
    }

    /// <summary>
    /// Executes one CLI command and appends its complete observable result to the GUI log.
    /// </summary>
    /// <param name="arguments">The prepared CLI arguments.</param>
    /// <param name="cancellationToken">Cancels the child process.</param>
    /// <returns>The CLI exit code and captured output streams.</returns>
    private async Task<(int exitCode, string stdout, string stderr)> ExecuteCliCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        // Log a redacted command before launch so failures during process startup still leave actionable context.
        var cliPath = ResolveCliPath();
        AppendLog("> " + cliPath + " " + RedactSecretArguments(arguments));
        var result = await RunCliAsync(cliPath, arguments, cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.stdout))
        {
            AppendLog(result.stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(result.stderr))
        {
            AppendLog("STDERR:");
            AppendLog(result.stderr.TrimEnd());
        }

        AppendLog($"Exit code: {result.exitCode}");
        AppendLog("");
        return result;
    }

    /// <summary>
    /// Selects one source per Markdown output, preferring legacy originals over modernized siblings.
    /// </summary>
    /// <param name="files">The candidate source files.</param>
    /// <returns>A deterministic source list.</returns>
    private static string[] SelectFolderConversionInputs(IEnumerable<string> files) =>
        files.GroupBy(file => Path.ChangeExtension(file, ".md"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => ModernizedSourceExtensions.Contains(Path.GetExtension(file)) ? 1 : 0)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Starts the DOC2MD CLI without a shell and captures both UTF-8 output streams.
    /// </summary>
    /// <param name="cliPath">The CLI executable path or command name.</param>
    /// <param name="arguments">The prepared command-line arguments.</param>
    /// <param name="cancellationToken">Cancels the child process.</param>
    /// <returns>The CLI exit code and captured output streams.</returns>
    private async Task<(int exitCode, string stdout, string stderr)> RunCliAsync(string cliPath, string arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (SelectedPdfProcessing() == "azure" && !string.IsNullOrWhiteSpace(_azureKey.Text))
        {
            // Secrets travel through the child environment so they never appear in arguments, logs, or process listings.
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = _azureKey.Text;
        }

        process.Start();
        _currentProcess = process;

        // Drain both pipes concurrently; waiting first could deadlock if either redirected buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            KillCurrentProcess();
            try
            {
                await process.WaitForExitAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }
        finally
        {
            if (ReferenceEquals(_currentProcess, process))
            {
                _currentProcess = null;
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Signals cooperative cancellation and terminates the active CLI process tree.
    /// </summary>
    private void CancelOperation()
    {
        // Cancellation is idempotent because a button click can race with natural process completion.
        if (_operationCts is null || _operationCts.IsCancellationRequested)
        {
            return;
        }

        AppendLog("Cancellation requested.");
        _status.Text = "Cancelling";
        _operationCts.Cancel();
        KillCurrentProcess();
    }

    /// <summary>
    /// Best-effort terminates the GUI-owned CLI process and any converter subprocesses it started.
    /// </summary>
    private void KillCurrentProcess()
    {
        try
        {
            if (_currentProcess is { HasExited: false })
            {
                _currentProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process exit can race with HasExited/Kill; completion has already achieved the desired state.
        }
    }

    /// <summary>
    /// Applies the global enabled state for controls during a conversion.
    /// </summary>
    /// <param name="busy">Whether a conversion is active.</param>
    private void SetBusy(bool busy)
    {
        // Reapply mode-specific rules after the global state so controls do not become valid in the wrong mode.
        _singleFile.Enabled = !busy;
        _folder.Enabled = !busy;
        _input.Enabled = !busy;
        _browseInput.Enabled = !busy;
        _overwrite.Enabled = !busy;
        _convert.Enabled = !busy;
        _cancel.Enabled = busy;
        ToggleMode();
        TogglePdfOptions();
    }

    /// <summary>
    /// Resets the progress bar for a new operation.
    /// </summary>
    /// <param name="maximum">The expected number of progress units.</param>
    private void ResetProgress(int maximum)
    {
        // WinForms requires Maximum to exceed Minimum even when there are no discovered files.
        _progress.Minimum = 0;
        _progress.Maximum = Math.Max(1, maximum);
        _progress.Value = 0;
    }

    /// <summary>
    /// Sets progress after clamping it to the current WinForms range.
    /// </summary>
    /// <param name="value">The requested progress value.</param>
    private void SetProgressValue(int value)
    {
        // Clamping tolerates late progress updates when a cancellation changes the effective work count.
        _progress.Value = Math.Min(Math.Max(value, _progress.Minimum), _progress.Maximum);
    }

    /// <summary>
    /// Appends text to the operational log and keeps the newest entry visible.
    /// </summary>
    /// <param name="text">The text to append.</param>
    private void AppendLog(string text)
    {
        // All calls originate on the UI synchronization context, so no cross-thread marshaling is required.
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    /// <summary>
    /// Resolves the CLI from explicit configuration, a development checkout, or the installed application directory.
    /// </summary>
    /// <returns>The CLI executable path or fallback command name.</returns>
    private static string ResolveCliPath()
    {
        // Environment configuration is authoritative for IIS-style or custom deployments with separated components.
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

    /// <summary>
    /// Initializes PDF controls from environment and non-secret persisted settings.
    /// </summary>
    private void ConfigurePdfDefaults()
    {
        // Environment values override user settings to match the CLI's configuration precedence.
        _pdfProcessing.Items.AddRange(new object[] { "local", "azure", "markitdown" });
        var configured = LoadStoredSettings();
        var configuredMode = Environment.GetEnvironmentVariable("DOC2MD_PDF_PROCESSING")
            ?? configured.PdfProcessing;
        _pdfProcessing.SelectedItem = string.Equals(configuredMode, "azure", StringComparison.OrdinalIgnoreCase)
            ? "azure"
            : string.Equals(configuredMode, "markitdown", StringComparison.OrdinalIgnoreCase)
                ? "markitdown"
                : "local";

        _ocrLanguages.Text = Environment.GetEnvironmentVariable("DOC2MD_OCR_LANGUAGES") ?? "eng+pol";
        _tessdata.Text = Environment.GetEnvironmentVariable("DOC2MD_TESSDATA_PATH") ?? "";
        _azureEndpoint.Text = Environment.GetEnvironmentVariable("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT")
            ?? configured.AzureDocumentIntelligenceEndpoint
            ?? "";
        // The protected key is resolved by the CLI and is never decrypted into a GUI control.
        _azureKey.Text = "";
    }

    /// <summary>
    /// Loads the non-secret subset of persisted settings required to initialize the form.
    /// </summary>
    /// <returns>Stored display defaults, or an empty object for missing or malformed settings.</returns>
    private static StoredDoc2MdSettings LoadStoredSettings()
    {
        // Malformed settings should not prevent the desktop UI from opening; the CLI remains the validation authority.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DOC2MD",
            "settings.json");

        if (!File.Exists(path))
        {
            return new StoredDoc2MdSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<StoredDoc2MdSettings>(File.ReadAllText(path))
                ?? new StoredDoc2MdSettings();
        }
        catch (JsonException)
        {
            return new StoredDoc2MdSettings();
        }
    }

    /// <summary>
    /// Builds only the CLI options applicable to the selected PDF processing stack.
    /// </summary>
    /// <returns>The quoted PDF argument fragment.</returns>
    private string BuildPdfArguments()
    {
        // Avoid forwarding disabled controls because the CLI intentionally rejects mixed-stack configuration.
        var mode = SelectedPdfProcessing();
        var args = $" --pdf-processing {Quote(mode)}";

        if (mode == "local")
        {
            args += OptionalArgument("--ocr-languages", _ocrLanguages.Text);
            args += OptionalArgument("--tessdata", _tessdata.Text);
        }
        else if (mode == "azure")
        {
            args += OptionalArgument("--azure-document-intelligence-endpoint", _azureEndpoint.Text);
        }

        return args;
    }

    /// <summary>
    /// Gets the selected processing mode with the local mode as a safe initialization fallback.
    /// </summary>
    /// <returns>The CLI processing-mode value.</returns>
    private string SelectedPdfProcessing() =>
        _pdfProcessing.SelectedItem?.ToString() ?? "local";

    /// <summary>
    /// Formats an optional quoted CLI name/value argument.
    /// </summary>
    /// <param name="name">The option name.</param>
    /// <param name="value">The optional value.</param>
    /// <returns>An empty string when unset; otherwise the formatted argument.</returns>
    private static string OptionalArgument(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $" {name} {Quote(value)}";

    /// <summary>
    /// Replaces recognized Azure key argument values before commands are logged.
    /// </summary>
    /// <param name="arguments">The complete CLI argument string.</param>
    /// <returns>The argument string with secret values replaced.</returns>
    private static string RedactSecretArguments(string arguments)
    {
        // Handle quoted and unquoted values because callers outside this form may reuse the logging helper contract.
        var names = new[] { "--azure-document-intelligence-key", "--azure-key" };
        foreach (var name in names)
        {
            var index = arguments.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var valueStart = index + name.Length;
            while (valueStart < arguments.Length && char.IsWhiteSpace(arguments[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= arguments.Length)
            {
                continue;
            }

            var valueEnd = valueStart;
            if (arguments[valueStart] == '"')
            {
                valueEnd++;
                while (valueEnd < arguments.Length && arguments[valueEnd] != '"')
                {
                    valueEnd++;
                }

                if (valueEnd < arguments.Length)
                {
                    valueEnd++;
                }
            }
            else
            {
                while (valueEnd < arguments.Length && !char.IsWhiteSpace(arguments[valueEnd]))
                {
                    valueEnd++;
                }
            }

            arguments = arguments[..valueStart] + "\"***\"" + arguments[valueEnd..];
        }

        return arguments;
    }

    /// <summary>
    /// Quotes a command-line value and escapes embedded quotation marks.
    /// </summary>
    /// <param name="value">The value to quote.</param>
    /// <returns>The quoted value.</returns>
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private sealed class StoredDoc2MdSettings
    {
        public string? PdfProcessing { get; set; }

        public string? AzureDocumentIntelligenceEndpoint { get; set; }
    }
}
