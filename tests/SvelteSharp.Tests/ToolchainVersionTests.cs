using SvelteSharp.Compiler;

namespace SvelteSharp.Tests;

public sealed class ToolchainVersionTests
{
    [Fact]
    public void UsesPinnedDefaultsWhenTheConsumerDoesNotOverrideVersions()
    {
        var versions = SvelteToolchainVersions.Default;

        versions.Validate();

        Assert.Equal("5.57.1", versions.SvelteVersion);
        Assert.Equal("0.28.1", versions.EsbuildVersion);
        Assert.Equal("5.7.3", versions.TypeScriptVersion);
    }

    [Fact]
    public void AcceptsExactPrereleaseVersionsForConsumerOverrides()
    {
        var versions = new SvelteToolchainVersions("5.58.0-next.1", "0.28.1", "5.8.0-beta.1");

        versions.Validate();
    }

    [Theory]
    [InlineData("5")]
    [InlineData("latest")]
    [InlineData("5.57")]
    public void RejectsFloatingOrIncompleteVersions(string version)
    {
        var versions = new SvelteToolchainVersions(version, "0.28.1", "5.7.3");

        Assert.Throws<ArgumentException>(() => versions.Validate());
    }
}
