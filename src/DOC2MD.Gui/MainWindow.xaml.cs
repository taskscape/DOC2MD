using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace DOC2MD.Gui;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".docm", ".xlsx", ".xls", ".xlsm", ".pptx", ".ppt", ".pptm",
        ".rtf", ".odt", ".ods", ".odp", ".txt", ".text", ".csv", ".html", ".htm", ".epub"
    };

    private static readonly HashSet<string> ModernizedSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docm", ".rtf", ".odt", ".xls", ".xlsm", ".ods", ".ppt", ".pptm", ".odp"
    };

    private CancellationTokenSource? _operationCts;
    private Process? _currentProcess;

    public MainWindow()
    {
        InitializeComponent();
        ConfigurePdfDefaults();
        ToggleMode();
        TogglePdfOptions();
    }

    private void ModeChanged(object sender, RoutedEventArgs e) => ToggleMode();

    private void PdfProcessingChanged(object sender, SelectionChangedEventArgs e) => TogglePdfOptions();

    private void ToggleMode()
    {
        var busy = _operationCts is not null;
        var folderMode = FolderRadioButton.IsChecked == true;
        RecursiveCheckBox.IsEnabled = folderMode && !busy;
        OutputTextBox.IsEnabled = !folderMode && !busy;
        BrowseOutputButton.IsEnabled = !folderMode && !busy;

        if (folderMode)
        {
            OutputTextBox.Clear();
        }
    }

    private void TogglePdfOptions()
    {
        var busy = _operationCts is not null;
        var mode = SelectedPdfProcessing();
        var local = mode == "local" && !busy;
        var azure = mode == "azure" && !busy;

        PdfProcessingComboBox.IsEnabled = !busy;
        OcrLanguagesTextBox.IsEnabled = local;
        TessdataTextBox.IsEnabled = local;
        BrowseTessdataButton.IsEnabled = local;
        AzureEndpointTextBox.IsEnabled = azure;
        AzureKeyPasswordBox.IsEnabled = azure;
    }

    private void BrowseInputClick(object sender, RoutedEventArgs e)
    {
        if (FolderRadioButton.IsChecked == true)
        {
            var dialog = new OpenFolderDialog();
            if (dialog.ShowDialog(this) == true)
            {
                InputTextBox.Text = dialog.FolderName;
            }

            return;
        }

        var open = new OpenFileDialog
        {
            Filter = "All supported documents|*.pdf;*.doc;*.docx;*.docm;*.xls;*.xlsx;*.xlsm;*.ppt;*.pptx;*.pptm;*.rtf;*.odt;*.ods;*.odp;*.txt;*.text;*.csv;*.html;*.htm;*.epub"
        };
        if (open.ShowDialog(this) == true)
        {
            InputTextBox.Text = open.FileName;
            OutputTextBox.Text = Path.ChangeExtension(open.FileName, ".md");
        }
    }

    private void BrowseOutputClick(object sender, RoutedEventArgs e)
    {
        var save = new SaveFileDialog { Filter = "Markdown|*.md|All files|*.*", DefaultExt = ".md" };
        if (save.ShowDialog(this) == true)
        {
            OutputTextBox.Text = save.FileName;
        }
    }

    private void BrowseTessdataClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
        {
            TessdataTextBox.Text = dialog.FolderName;
        }
    }

    private async void ConvertClick(object sender, RoutedEventArgs e) => await ConvertAsync();

    private async Task ConvertAsync()
    {
        if (_operationCts is not null)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        SetBusy(true);
        LogTextBox.Clear();
        ResetProgress(1);

        try
        {
            if (SingleFileRadioButton.IsChecked == true)
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
            StatusLabel.Content = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            StatusLabel.Content = "Failed";
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
        if (string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            throw new InvalidOperationException("Input file is required.");
        }

        if (string.IsNullOrWhiteSpace(OutputTextBox.Text))
        {
            throw new InvalidOperationException("Output file is required.");
        }

        var args = $"convert --input {Quote(InputTextBox.Text)} --output {Quote(OutputTextBox.Text)} --json";
        if (OverwriteCheckBox.IsChecked == true)
        {
            args += " --overwrite";
        }

        args += BuildPdfArguments();
        StatusLabel.Content = "Converting 1 of 1";
        var result = await ExecuteCliCommandAsync(args, cancellationToken);
        SetProgressValue(1);
        StatusLabel.Content = result.exitCode == 0 ? "Completed" : "Failed";
    }

    private async Task ConvertFolderAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(InputTextBox.Text))
        {
            throw new InvalidOperationException("Input folder is required.");
        }

        if (!Directory.Exists(InputTextBox.Text))
        {
            throw new DirectoryNotFoundException($"Input folder was not found: {InputTextBox.Text}");
        }

        var files = SelectFolderConversionInputs(Directory.EnumerateFiles(
                InputTextBox.Text,
                "*.*",
                RecursiveCheckBox.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)))
            .Where(file => !Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase));

        if (files.Length == 0)
        {
            AppendLog("No supported source documents were found.");
            StatusLabel.Content = "No files";
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
            StatusLabel.Content = $"Converting {i + 1} of {files.Length}";

            if (File.Exists(outputFile) && OverwriteCheckBox.IsChecked != true)
            {
                AppendLog($"SKIP: {outputFile} already exists. Enable overwrite to replace it.");
                SetProgressValue(i + 1);
                continue;
            }

            var args = $"convert --input {Quote(inputFile)} --output {Quote(outputFile)} --json";
            if (OverwriteCheckBox.IsChecked == true)
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

        StatusLabel.Content = failures == 0 ? $"Completed {files.Length} files" : $"Completed with {failures} failure(s)";
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

        if (SelectedPdfProcessing() == "azure" && !string.IsNullOrWhiteSpace(AzureKeyPasswordBox.Password))
        {
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = AzureKeyPasswordBox.Password;
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

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private void CancelClick(object sender, RoutedEventArgs e) => CancelOperation();

    private void CancelOperation()
    {
        if (_operationCts is null || _operationCts.IsCancellationRequested)
        {
            return;
        }

        AppendLog("Cancellation requested.");
        StatusLabel.Content = "Cancelling";
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
        SingleFileRadioButton.IsEnabled = !busy;
        FolderRadioButton.IsEnabled = !busy;
        InputTextBox.IsEnabled = !busy;
        BrowseInputButton.IsEnabled = !busy;
        OverwriteCheckBox.IsEnabled = !busy;
        ConvertButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        ToggleMode();
        TogglePdfOptions();
    }

    private void ResetProgress(int maximum)
    {
        ConversionProgressBar.Maximum = Math.Max(1, maximum);
        ConversionProgressBar.Value = 0;
    }

    private void SetProgressValue(int value) =>
        ConversionProgressBar.Value = Math.Min(Math.Max(value, ConversionProgressBar.Minimum), ConversionProgressBar.Maximum);

    private void AppendLog(string text)
    {
        LogTextBox.AppendText(text + Environment.NewLine);
        LogTextBox.ScrollToEnd();
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
        var configured = LoadStoredSettings();
        var configuredMode = Environment.GetEnvironmentVariable("DOC2MD_PDF_PROCESSING") ?? configured.PdfProcessing;
        PdfProcessingComboBox.SelectedIndex = string.Equals(configuredMode, "azure", StringComparison.OrdinalIgnoreCase)
            ? 1
            : string.Equals(configuredMode, "markitdown", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        OcrLanguagesTextBox.Text = Environment.GetEnvironmentVariable("DOC2MD_OCR_LANGUAGES") ?? "eng+pol";
        TessdataTextBox.Text = Environment.GetEnvironmentVariable("DOC2MD_TESSDATA_PATH") ?? "";
        AzureEndpointTextBox.Text = Environment.GetEnvironmentVariable("DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT")
            ?? configured.AzureDocumentIntelligenceEndpoint
            ?? "";
    }

    private static StoredDoc2MdSettings LoadStoredSettings()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DOC2MD", "settings.json");
        if (!File.Exists(path))
        {
            return new StoredDoc2MdSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<StoredDoc2MdSettings>(File.ReadAllText(path)) ?? new StoredDoc2MdSettings();
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
            args += OptionalArgument("--ocr-languages", OcrLanguagesTextBox.Text);
            args += OptionalArgument("--tessdata", TessdataTextBox.Text);
        }
        else if (mode == "azure")
        {
            args += OptionalArgument("--azure-document-intelligence-endpoint", AzureEndpointTextBox.Text);
        }

        return args;
    }

    private string SelectedPdfProcessing() =>
        (PdfProcessingComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "local";

    private static string OptionalArgument(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $" {name} {Quote(value)}";

    private static string RedactSecretArguments(string arguments)
    {
        foreach (var name in new[] { "--azure-document-intelligence-key", "--azure-key" })
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
