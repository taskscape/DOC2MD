/// <summary>
/// Publishes conversion outputs only after their temporary write succeeds.
/// </summary>
internal static class AtomicFileOutput
{
    /// <summary>
    /// Writes through an ASCII-only temporary path in the destination folder,
    /// then moves that file to the requested output path atomically.
    /// </summary>
    /// <typeparam name="T">The conversion result type.</typeparam>
    /// <param name="outputPath">The final output path.</param>
    /// <param name="overwrite">Whether an existing final output may be replaced.</param>
    /// <param name="writeTemporaryOutputAsync">Writes the conversion to the supplied temporary path.</param>
    /// <param name="succeeded">Returns whether the conversion result can be published.</param>
    /// <returns>The conversion result returned by the writer.</returns>
    internal static async Task<T> WriteAsync<T>(
        string outputPath,
        bool overwrite,
        Func<string, Task<T>> writeTemporaryOutputAsync,
        Func<T, bool> succeeded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(writeTemporaryOutputAsync);
        ArgumentNullException.ThrowIfNull(succeeded);

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryOutputPath = Path.Combine(
            outputDirectory,
            $".doc2md-{Guid.NewGuid():N}.tmp");

        try
        {
            var result = await writeTemporaryOutputAsync(temporaryOutputPath);
            if (!succeeded(result))
            {
                return result;
            }

            if (!File.Exists(temporaryOutputPath))
            {
                throw new IOException(
                    "The converter reported success without producing its temporary output file.");
            }

            File.Move(temporaryOutputPath, fullOutputPath, overwrite);
            return result;
        }
        finally
        {
            TryDelete(temporaryOutputPath);
        }
    }

    /// <summary>
    /// Removes a failed conversion's temporary output without hiding its primary result.
    /// </summary>
    /// <param name="path">The temporary output path.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort and must not replace the conversion result.
        }
    }
}
