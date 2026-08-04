using System.Text.RegularExpressions;
using Xunit;

public sealed class PdfConversionTemporaryPathTests
{
    [Fact]
    public void CreateOcrTemporaryImagePath_UsesGuidOnlyAsciiFilename()
    {
        var path = DocumentConversion.CreateOcrTemporaryImagePath(pageNumber: 17);

        Assert.Equal(Path.GetTempPath(), Path.GetDirectoryName(path) + Path.DirectorySeparatorChar);
        Assert.Matches(
            new Regex(
                "^doc2md-[0-9a-f]{32}-page-17\\.png$",
                RegexOptions.CultureInvariant),
            Path.GetFileName(path));
    }
}
