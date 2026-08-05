using System.Diagnostics;
using System.Text;

internal sealed class TesseractOcrEngine
{
    private readonly string _executablePath;
    private readonly string _tessdataPath;
    private readonly string _languages;

    private TesseractOcrEngine(string executablePath, string tessdataPath, string languages)
    {
        _executablePath = executablePath;
        _tessdataPath = tessdataPath;
        _languages = languages;
    }

    public static async Task<TesseractOcrEngine> CreateAsync(string tessdataPath, string languages)
    {
        var executable = ApplicationPaths.TesseractExecutableCandidates().FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "Tesseract OCR was not found. Reinstall DOC2MD or set DOC2MD_TESSERACT_PATH to the native Tesseract executable.");

        var version = await RunAsync(executable, ["--version"]);
        if (version.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Tesseract OCR could not start from '{executable}'. {FormatError(version)}");
        }

        return new TesseractOcrEngine(executable, tessdataPath, languages);
    }

    public async Task<string> RecognizeAsync(string imagePath)
    {
        var result = await RunAsync(
            _executablePath,
            [imagePath, "stdout", "--tessdata-dir", _tessdataPath, "-l", _languages, "--psm", "3"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Tesseract OCR failed for '{imagePath}'. {FormatError(result)}");
        }

        return result.Stdout;
    }

    private static async Task<TesseractProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new TesseractProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new TesseractProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private static string FormatError(TesseractProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
        return $"Exit code {result.ExitCode}: {detail.Trim()}";
    }

    private sealed record TesseractProcessResult(int ExitCode, string Stdout, string Stderr);
}
