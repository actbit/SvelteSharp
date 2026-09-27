using System.Security.Cryptography;
using System.Text;
using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class PrebuiltRuntimeTests
{
    [Fact]
    public async Task LoadsBuildArtifactsWithoutInvokingANativeBundler()
    {
        var root = Path.Combine(Path.GetTempPath(), "SvelteSharpTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string source = "<h1>hello</h1>";
            var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
            var manifest = new SvelteBuildManifest(
                "prebuilt-test",
                [new SvelteViewManifestEntry(
                    "Home",
                    "prebuilt-test",
                    new HashSet<SvelteRenderMode> { SvelteRenderMode.Server, SvelteRenderMode.Client, SvelteRenderMode.Hybrid },
                    "server graph",
                    "views/Home.js",
                    "views/Home.css",
                    sourceHash,
                    "5.57.1")],
                [new SveltePublicAsset("views/Home.js", "text/javascript", 13)]);
            var output = new SvelteBuildOutput(
                manifest,
                [
                    new SvelteBuildArtifact("server/Home.js", "server graph", "application/javascript", false),
                    new SvelteBuildArtifact("views/Home.js", "client graph", "text/javascript", true),
                    new SvelteBuildArtifact("views/Home.css", "h1{}", "text/css", true)
                ]);

            await new AtomicSvelteBuildPublisher().PublishAsync(output, root);

            var store = new SveltePrebuiltArtifactStore(root);
            Assert.True(store.IsAvailable);
            var compiler = new PrebuiltSvelteCompiler(store);
            var result = await compiler.CompileAsync(new SvelteSourceFile("Home", source), new SvelteCompilerOptions
            {
                CompilerVersion = "5.57.1"
            });
            var bundler = new PrebuiltSvelteGraphBundler(store);

            Assert.Equal("server graph", (await bundler.BundleAsync(result, "Home", SvelteGraphKind.Server)).Code);
            Assert.Equal("client graph", (await bundler.BundleAsync(result, "Home", SvelteGraphKind.Client)).Code);
            Assert.Equal("h1{}", result.Css);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
