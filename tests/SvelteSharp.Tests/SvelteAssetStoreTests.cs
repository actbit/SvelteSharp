using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class SvelteAssetStoreTests
{
    [Fact]
    public void StoresAndRetrievesOnlyForTheExactBuildId()
    {
        var store = new SvelteAssetStore();
        store.Put("v1", "views/Home.js", "code", "text/javascript");

        Assert.True(store.TryGet("v1", "views/Home.js", out var asset));
        Assert.NotNull(asset);
        Assert.Equal("code", asset.Content);
        Assert.Equal("text/javascript", asset.ContentType);
        Assert.False(store.TryGet("v2", "views/Home.js", out _));
    }

    [Fact]
    public void NormalizesSeparatorsAndSurroundingSlashes()
    {
        var store = new SvelteAssetStore();
        store.Put("v1", "\\views\\Home.js\\", "code", "text/javascript");

        Assert.True(store.TryGet("v1", "views/Home.js", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secret")]
    [InlineData("views/../secret")]
    public void RejectsUnsafePaths(string path)
    {
        var store = new SvelteAssetStore();

        Assert.Throws<ArgumentException>(() => store.Put("v1", path, "x", "text/plain"));
    }
}
