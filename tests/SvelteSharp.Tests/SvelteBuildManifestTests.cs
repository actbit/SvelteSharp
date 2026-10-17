using SvelteSharp.Build;

namespace SvelteSharp.Tests;

public sealed class SvelteBuildManifestTests
{
    [Fact]
    public void RejectsBlankBuildId()
        => Assert.Throws<ArgumentException>(() => new SvelteBuildManifest("  ", [Entry()]));

    [Fact]
    public void RejectsViewWithDifferentBuildId()
        => Assert.Throws<ArgumentException>(() => new SvelteBuildManifest("b1", [Entry(buildId: "b2")]));

    [Fact]
    public void RejectsViewWithoutModes()
        => Assert.Throws<ArgumentException>(() => new SvelteBuildManifest(
            "b1",
            [Entry(modes: new HashSet<SvelteRenderMode>())]));

    [Fact]
    public void RequiresServerGraphForServerModes()
        => Assert.Throws<ArgumentException>(() => new SvelteBuildManifest(
            "b1",
            [Entry(modes: new HashSet<SvelteRenderMode> { SvelteRenderMode.Server }, serverGraph: null, clientEntry: null)]));

    [Fact]
    public void RequiresClientEntryForClientModes()
        => Assert.Throws<ArgumentException>(() => new SvelteBuildManifest(
            "b1",
            [Entry(modes: new HashSet<SvelteRenderMode> { SvelteRenderMode.Client }, serverGraph: null, clientEntry: null)]));

    [Fact]
    public void GetViewThrowsDiagnosticForUnknownView()
    {
        var manifest = new SvelteBuildManifest("b1", [Entry()]);

        var exception = Assert.Throws<SvelteViewException>(() => manifest.GetView("Missing"));

        Assert.Equal("SVE2001", exception.Code);
    }

    [Fact]
    public void SupportsReflectsDeclaredModes()
    {
        var manifest = new SvelteBuildManifest(
            "b1",
            [Entry(modes: new HashSet<SvelteRenderMode> { SvelteRenderMode.Server }, serverGraph: "server", clientEntry: null)]);
        var view = manifest.GetView("Home");

        Assert.True(view.Supports(SvelteRenderMode.Server));
        Assert.False(view.Supports(SvelteRenderMode.Client));
        Assert.False(view.Supports(SvelteRenderMode.Hybrid));
    }

    [Fact]
    public void ToJsonContainsBuildIdAndAssets()
    {
        var manifest = new SvelteBuildManifest(
            "b1",
            [Entry()],
            [new SveltePublicAsset("views/Home.js", "text/javascript", 4)]);

        var json = manifest.ToJson();

        Assert.Contains("\"buildId\":\"b1\"", json);
        Assert.Contains("views/Home.js", json);
    }

    private static SvelteViewManifestEntry Entry(
        string view = "Home",
        string buildId = "b1",
        IReadOnlySet<SvelteRenderMode>? modes = null,
        string? serverGraph = "server",
        string? clientEntry = "views/Home.js")
        => new(
            view,
            buildId,
            modes ?? new HashSet<SvelteRenderMode>
            {
                SvelteRenderMode.Server,
                SvelteRenderMode.Client,
                SvelteRenderMode.Hybrid
            },
            serverGraph,
            clientEntry,
            CssAsset: null,
            SourceHash: "hash",
            CompilerVersion: "1.0.0");
}

public sealed class BuildIdCalculatorTests
{
    [Fact]
    public void IsDeterministicAndOrderIndependent()
    {
        var first = BuildIdCalculator.Calculate("0.1.0", "5.57.1", "none", [("a.js", "h1"), ("b.js", "h2")], "settings");
        var second = BuildIdCalculator.Calculate("0.1.0", "5.57.1", "none", [("b.js", "h2"), ("a.js", "h1")], "settings");

        Assert.Equal(first, second);
        Assert.Equal(16, first.Length);
    }

    [Fact]
    public void ChangesWhenAnyInputChanges()
    {
        var baseline = BuildIdCalculator.Calculate("0.1.0", "5.57.1", "none", [("a.js", "h1")], "settings");

        Assert.NotEqual(baseline, BuildIdCalculator.Calculate("0.1.1", "5.57.1", "none", [("a.js", "h1")], "settings"));
        Assert.NotEqual(baseline, BuildIdCalculator.Calculate("0.1.0", "5.57.2", "none", [("a.js", "h1")], "settings"));
        Assert.NotEqual(baseline, BuildIdCalculator.Calculate("0.1.0", "5.57.1", "lock", [("a.js", "h1")], "settings"));
        Assert.NotEqual(baseline, BuildIdCalculator.Calculate("0.1.0", "5.57.1", "none", [("a.js", "h2")], "settings"));
        Assert.NotEqual(baseline, BuildIdCalculator.Calculate("0.1.0", "5.57.1", "none", [("a.js", "h1")], "other"));
    }
}

public sealed class SvelteAssetUrlTests
{
    [Fact]
    public void CreatesImmutableUrl()
        => Assert.Equal("/_svelte/vb1/views/Home.js", SvelteAssetUrl.Create("b1", "views/Home.js"));

    [Fact]
    public void NormalizesSeparators()
        => Assert.Equal("/_svelte/vb1/views/Home.js", SvelteAssetUrl.Create("b1", "\\views\\Home.js"));

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("views/../x")]
    public void RejectsUnsafePaths(string path)
        => Assert.Throws<ArgumentException>(() => SvelteAssetUrl.Create("b1", path));
}

public sealed class SvelteManifestStoreTests
{
    [Fact]
    public void CurrentIsNullUntilPublished()
    {
        var store = new SvelteManifestStore();
        Assert.Null(store.Current);

        var manifest = new SvelteBuildManifest(
            "b1",
            [new SvelteViewManifestEntry(
                "Home",
                "b1",
                new HashSet<SvelteRenderMode> { SvelteRenderMode.Server },
                "server",
                null,
                null,
                "hash",
                "1.0.0")]);
        store.Publish(manifest);

        Assert.Same(manifest, store.Current);
    }
}
