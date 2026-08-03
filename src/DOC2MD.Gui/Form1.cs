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

    public Form1()
    {
        InitializeComponent();
        BuildUi();
    }

    private void BuildUi()
    {
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

    private Control BuildCommandPanel()
    {
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

    private Control BuildStatusPanel()
    {
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

    private static Label CreateRowLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 0, 10, 0)
    };

    private void ToggleMode()
    {
        var busy = _operationCts is not null;
        _recursive.Enabled = _folder.Checked && !busy;
        _output.Enabled = _singleFile.Checked && !busy;
        _browseOutput.Enabled = _singleFile.Checked && !busy;

        if (_folder.Checked)
        {
            _output.Text = "";
        }
    }

    private void TogglePdfOptions()
    {
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

    private void BrowseInput()
    {
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

    private void BrowseOutput()
    {
        using var save = new SaveFileDialog { Filter = "Markdown|*.md|All files|*.*", DefaultExt = "md" };
        if (save.ShowDialog(this) == DialogResult.OK)
        {
            _output.Text = save.FileName;
        }
    }

    private void BrowseTessdata()
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _tessdata.Text = dialog.SelectedPath;
        }
    }

    private async Task ConvertAsync()
    {
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

    private async Task ConvertSingleFileAsync(CancellationToken cancellationToken)
    {
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

    private async Task<(int exitCode, string stdout, string stderr)> ExecuteCliCommandAsync(string arguments, CancellationToken cancellationToken)
    {
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

    private static string[] SelectFolderConversionInputs(IEnumerable<string> files) =>
        files.GroupBy(file => Path.ChangeExtension(file, ".md"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(file => ModernizedSourceExtensions.Contains(Path.GetExtension(file)) ? 1 : 0)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = _azureKey.Text;
        }

        process.Start();
        _currentProcess = process;

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

    private void CancelOperation()
    {
        if (_operationCts is null || _operationCts.IsCancellationRequested)
        {
            return;
        }

        AppendLog("Cancellation requested.");
        _status.Text = "Cancelling";
        _operationCts.Cancel();
        KillCurrentProcess();
    }

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
        }
    }

    private void SetBusy(bool busy)
    {
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

    private void ResetProgress(int maximum)
    {
        _progress.Minimum = 0;
        _progress.Maximum = Math.Max(1, maximum);
        _progress.Value = 0;
    }

    private void SetProgressValue(int value)
    {
        _progress.Value = Math.Min(Math.Max(value, _progress.Minimum), _progress.Maximum);
    }

    private void AppendLog(string text)
    {
        _log.AppendText(text + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
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

    private void ConfigurePdfDefaults()
    {
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
        _azureKey.Text = "";
    }

    private static StoredDoc2MdSettings LoadStoredSettings()
    {
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

    private string BuildPdfArguments()
    {
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

    private string SelectedPdfProcessing() =>
        _pdfProcessing.SelectedItem?.ToString() ?? "local";

    private static string OptionalArgument(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $" {name} {Quote(value)}";

    private static string RedactSecretArguments(string arguments)
    {
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

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private sealed class StoredDoc2MdSettings
    {
        public string? PdfProcessing { get; set; }

        public string? AzureDocumentIntelligenceEndpoint { get; set; }
    }
}
