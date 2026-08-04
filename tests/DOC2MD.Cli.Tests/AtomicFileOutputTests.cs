using System.Text.RegularExpressions;
using Xunit;

public sealed class AtomicFileOutputTests
{
    [Fact]
    public async Task WriteAsync_PublishesSuccessfulOutputFromAsciiTemporaryPath()
    {
        using var folder = new TemporaryFolder();
        var outputPath = Path.Combine(folder.Path, "zażółć.md");
        string? temporaryPath = null;

        var result = await AtomicFileOutput.WriteAsync(
            outputPath,
            overwrite: false,
            path =>
            {
                temporaryPath = path;
                File.WriteAllText(path, "complete");
                return Task.FromResult(true);
            },
            succeeded => succeeded);

        Assert.True(result);
        Assert.NotNull(temporaryPath);
        Assert.Matches(
            new Regex("^\\.doc2md-[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant),
            Path.GetFileName(temporaryPath));
        Assert.Equal("complete", File.ReadAllText(outputPath));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public async Task WriteAsync_RemovesFailedTemporaryOutputWithoutPublishingFinalFile()
    {
        using var folder = new TemporaryFolder();
        var outputPath = Path.Combine(folder.Path, "failed.md");

        var result = await AtomicFileOutput.WriteAsync(
            outputPath,
            overwrite: false,
            path =>
            {
                File.WriteAllText(path, "partial");
                return Task.FromResult(false);
            },
            succeeded => succeeded);

        Assert.False(result);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.EnumerateFiles(folder.Path, ".doc2md-*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_CleansTemporaryOutputWhenFinalMoveFails()
    {
        using var folder = new TemporaryFolder();
        var outputPath = Path.Combine(folder.Path, "existing.md");
        File.WriteAllText(outputPath, "original");

        await Assert.ThrowsAsync<IOException>(() => AtomicFileOutput.WriteAsync(
            outputPath,
            overwrite: false,
            path =>
            {
                File.WriteAllText(path, "replacement");
                return Task.FromResult(true);
            },
            succeeded => succeeded));

        Assert.Equal("original", File.ReadAllText(outputPath));
        Assert.Empty(Directory.EnumerateFiles(folder.Path, ".doc2md-*.tmp"));
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"DOC2MD.Cli.Tests.{Guid.NewGuid():N}"))
                .FullName;
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
