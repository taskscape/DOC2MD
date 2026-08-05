using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

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
        AzureKeyTextBox.IsEnabled = azure;
    }

    private async void BrowseInputClick(object? sender, RoutedEventArgs e)
    {
        if (FolderRadioButton.IsChecked == true)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select input folder",
                AllowMultiple = false
            });
            var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                InputTextBox.Text = folderPath;
            }

            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select document",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported documents")
                {
                    Patterns = SupportedExtensions.Select(extension => $"*{extension}").ToArray()
                }
            ]
        });
        var filePath = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            InputTextBox.Text = filePath;
            OutputTextBox.Text = Path.ChangeExtension(filePath, ".md");
        }
    }

    private async void BrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Select Markdown output",
            SuggestedFileName = Path.GetFileName(OutputTextBox.Text),
            DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }]
        });
        var filePath = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            OutputTextBox.Text = filePath;
        }
    }

    private async void BrowseTessdataClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Tesseract trained data folder",
            AllowMultiple = false
        });
        var folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            TessdataTextBox.Text = folderPath;
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
            StatusTextBlock.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            StatusTextBlock.Text = "Failed";
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

        var args = new List<string> { "convert", "--input", InputTextBox.Text, "--output", OutputTextBox.Text, "--json" };
        if (OverwriteCheckBox.IsChecked == true)
        {
            args.Add("--overwrite");
        }

        AppendPdfArguments(args);
        StatusTextBlock.Text = "Converting 1 of 1";
        var result = await ExecuteCliCommandAsync(args, cancellationToken);
        SetProgressValue(1);
        StatusTextBlock.Text = result.exitCode == 0 ? "Completed" : "Failed";
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
            StatusTextBlock.Text = "No files";
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
            StatusTextBlock.Text = $"Converting {i + 1} of {files.Length}";

            if (File.Exists(outputFile) && OverwriteCheckBox.IsChecked != true)
            {
                AppendLog($"SKIP: {outputFile} already exists. Enable overwrite to replace it.");
                SetProgressValue(i + 1);
                continue;
            }

            var args = new List<string> { "convert", "--input", inputFile, "--output", outputFile, "--json" };
            if (OverwriteCheckBox.IsChecked == true)
            {
                args.Add("--overwrite");
            }

            AppendPdfArguments(args);
            var result = await ExecuteCliCommandAsync(args, cancellationToken);
            if (result.exitCode != 0)
            {
                failures++;
            }

            SetProgressValue(i + 1);
        }

        StatusTextBlock.Text = failures == 0 ? $"Completed {files.Length} files" : $"Completed with {failures} failure(s)";
    }

    private async Task<(int exitCode, string stdout, string stderr)> ExecuteCliCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var cliPath = CliExecutableLocator.Resolve();
        AppendLog("> " + FormatCommandForLog(cliPath, arguments));
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

    private async Task<(int exitCode, string stdout, string stderr)> RunCliAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = cliPath,
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

        if (SelectedPdfProcessing() == "azure" && !string.IsNullOrWhiteSpace(AzureKeyTextBox.Text))
        {
            process.StartInfo.Environment["DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY"] = AzureKeyTextBox.Text;
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
        StatusTextBlock.Text = "Cancelling";
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
        LogTextBox.Text = (LogTextBox.Text ?? string.Empty) + text + Environment.NewLine;
        LogTextBox.CaretIndex = LogTextBox.Text.Length;
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

    private void AppendPdfArguments(List<string> args)
    {
        var mode = SelectedPdfProcessing();
        AppendArgument(args, "--pdf-processing", mode);
        if (mode == "local")
        {
            AppendArgument(args, "--ocr-languages", OcrLanguagesTextBox.Text);
            AppendArgument(args, "--tessdata", TessdataTextBox.Text);
        }
        else if (mode == "azure")
        {
            AppendArgument(args, "--azure-document-intelligence-endpoint", AzureEndpointTextBox.Text);
        }
    }

    private string SelectedPdfProcessing() =>
        (PdfProcessingComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "local";

    private static void AppendArgument(List<string> arguments, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments.Add(name);
            arguments.Add(value);
        }
    }

    private static string FormatCommandForLog(string executable, IReadOnlyList<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteForLog));

    private static string QuoteForLog(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;

    private sealed class StoredDoc2MdSettings
    {
        public string? PdfProcessing { get; set; }

        public string? AzureDocumentIntelligenceEndpoint { get; set; }
    }
}
