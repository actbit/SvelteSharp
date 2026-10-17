using SvelteSharp.Compiler;

namespace SvelteSharp.Tests;

public sealed class SvelteToolchainTests
{
    [Fact]
    public void ReportsNotInstalledForAnEmptyDirectory()
    {
        var root = Directory.CreateTempSubdirectory("svelte-empty-toolchain").FullName;
        try
        {
            var toolchain = new SvelteToolchain(root);

            Assert.False(toolchain.IsInstalled());
            Assert.Throws<InvalidOperationException>(() => toolchain.EnsureInstalled());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsThePinnedVersionsFromTheInstalledToolchain()
    {
        var versions = TestToolchain.Resolve().ReadVersions();

        Assert.Equal(SvelteToolchain.SvelteVersion, versions.SvelteVersion);
        Assert.Equal(SvelteToolchain.EsbuildVersion, versions.EsbuildVersion);
        Assert.Equal(SvelteToolchain.TypeScriptVersion, versions.TypeScriptVersion);
    }
}
