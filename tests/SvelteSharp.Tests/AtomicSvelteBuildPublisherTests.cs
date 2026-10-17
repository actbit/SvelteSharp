using SvelteSharp.Build;

namespace SvelteSharp.Tests;

public sealed class AtomicSvelteBuildPublisherTests
{
    [Fact]
    public async Task PublishesAndIsIdempotentForACompleteBuild()
    {
        var root = Directory.CreateTempSubdirectory("svelte-publisher").FullName;
        try
        {
            var publisher = new AtomicSvelteBuildPublisher();
            var output = CreateOutput();

            var first = await publisher.PublishAsync(output, root);
            var second = await publisher.PublishAsync(output, root);

            Assert.Equal(first, second);
            Assert.True(File.Exists(Path.Combine(first, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(first, "views", "Home.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RebuildsAnIncompleteExistingDirectory()
    {
        var root = Directory.CreateTempSubdirectory("svelte-publisher").FullName;
        try
        {
            var finalDirectory = Path.Combine(root, "vb1");
            Directory.CreateDirectory(finalDirectory);
            await File.WriteAllTextAsync(Path.Combine(finalDirectory, "manifest.json"), "{}");

            var published = await new AtomicSvelteBuildPublisher().PublishAsync(CreateOutput(), root);

            Assert.Equal(finalDirectory, published);
            Assert.True(File.Exists(Path.Combine(published, "views", "Home.js")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsUnsafeArtifactPaths()
    {
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
        var output = new SvelteBuildOutput(
            manifest,
            [new SvelteBuildArtifact("../escape.js", "x", "text/javascript", false)]);

        var root = Directory.CreateTempSubdirectory("svelte-publisher").FullName;
        try
        {
            var exception = await Assert.ThrowsAsync<SvelteBuildException>(async () =>
                await new AtomicSvelteBuildPublisher().PublishAsync(output, root));

            Assert.Equal("SVE3003", exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SvelteBuildOutput CreateOutput()
    {
        var manifest = new SvelteBuildManifest(
            "b1",
            [new SvelteViewManifestEntry(
                "Home",
                "b1",
                new HashSet<SvelteRenderMode>
                {
                    SvelteRenderMode.Server,
                    SvelteRenderMode.Client,
                    SvelteRenderMode.Hybrid
                },
                "server",
                "views/Home.js",
                null,
                "hash",
                "1.0.0")],
            [new SveltePublicAsset("views/Home.js", "text/javascript", 6)]);
        return new SvelteBuildOutput(
            manifest,
            [
                new SvelteBuildArtifact("server/Home.js", "server", "application/javascript", false),
                new SvelteBuildArtifact("views/Home.js", "client", "text/javascript", true)
            ]);
    }
}
