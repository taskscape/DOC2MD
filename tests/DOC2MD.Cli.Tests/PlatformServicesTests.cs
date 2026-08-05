using Xunit;

[Collection("Process environment")]
public sealed class PlatformServicesTests
{
    [Fact]
    public void ResourceRoot_UsesExplicitPlatformNeutralOverride()
    {
        var original = Environment.GetEnvironmentVariable(ApplicationPaths.ResourceRootEnvironmentVariable);
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"doc2md-resources-{Guid.NewGuid():N}"));

        try
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ResourceRootEnvironmentVariable, expected);

            Assert.Equal(expected, ApplicationPaths.ResourceRoot);
            Assert.Equal(Path.Combine(expected, "markitdown", "src"), ApplicationPaths.MarkItDownSourceRoot);
            Assert.Equal(Path.Combine(expected, "tessdata"), ApplicationPaths.BundledTessdataRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApplicationPaths.ResourceRootEnvironmentVariable, original);
        }
    }

    [Fact]
    public void VirtualEnvironmentPython_UsesCurrentPlatformLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "doc2md-venv");

        var expected = OperatingSystem.IsWindows()
            ? Path.Combine(root, "Scripts", "python.exe")
            : Path.Combine(root, "bin", "python3");

        Assert.Equal(expected, ApplicationPaths.GetVirtualEnvironmentPython(root));
    }

    [Fact]
    public void SecretStoreFactory_SelectsOperatingSystemImplementation()
    {
        var store = SecretStoreFactory.Create();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsDpapiSecretStore>(store);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacOsKeychainSecretStore>(store);
        }
        else
        {
            Assert.IsType<UnsupportedSecretStore>(store);
        }
    }
}

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;
