using SvelteSharp.Build;
using SvelteSharp.Compiler;

namespace SvelteSharp.Tests;

public sealed class SvelteBuildPipelineTests
{
    [Fact]
    public async Task ThrowsWhenNoViewsAreProvided()
    {
        var pipeline = new SvelteBuildPipeline(new StubCompiler());

        var exception = await Assert.ThrowsAsync<SvelteBuildException>(async () =>
            await pipeline.BuildAsync(new SvelteBuildRequest([], new SvelteCompilerOptions())));

        Assert.Equal("SVE3001", exception.Code);
    }

    [Fact]
    public async Task ThrowsWhenTheCompilerReturnsErrors()
    {
        var pipeline = new SvelteBuildPipeline(new StubCompiler
        {
            Diagnostics = [new SvelteCompilerDiagnostic("x", "boom", SvelteDiagnosticSeverity.Error)]
        });

        var exception = await Assert.ThrowsAsync<SvelteBuildException>(async () =>
            await pipeline.BuildAsync(new SvelteBuildRequest(
                [new SvelteSourceFile("Home", "<h1>x</h1>")],
                new SvelteCompilerOptions())));

        Assert.Equal("SVE3002", exception.Code);
        Assert.Single(exception.Diagnostics);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ProducesTheConfiguredModeMatrix(bool server, bool client)
    {
        var pipeline = new SvelteBuildPipeline(new StubCompiler());

        var output = await pipeline.BuildAsync(new SvelteBuildRequest(
            [new SvelteSourceFile("Home", "<h1>x</h1>")],
            new SvelteCompilerOptions { GenerateServer = server, GenerateClient = client }));

        var modes = output.Manifest.GetView("Home").SupportedModes;
        Assert.Equal(server, modes.Contains(SvelteRenderMode.Server));
        Assert.Equal(client, modes.Contains(SvelteRenderMode.Client));
        Assert.Equal(server && client, modes.Contains(SvelteRenderMode.Hybrid));
    }

    [Fact]
    public async Task BuildIdIsStableAcrossRuns()
    {
        var pipeline = new SvelteBuildPipeline(new StubCompiler());
        var request = new SvelteBuildRequest([new SvelteSourceFile("Home", "<h1>x</h1>")], new SvelteCompilerOptions());

        var first = await pipeline.BuildAsync(request);
        var second = await pipeline.BuildAsync(request);

        Assert.Equal(first.Manifest.BuildId, second.Manifest.BuildId);
    }

    private sealed class StubCompiler : ISvelteCompiler
    {
        public IReadOnlyList<SvelteCompilerDiagnostic> Diagnostics { get; set; } = [];

        public ValueTask<SvelteCompilationResult> CompileAsync(
            SvelteSourceFile source,
            SvelteCompilerOptions options,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SvelteCompilationResult(
                source.ViewName,
                new SvelteViewTemplate("<h1>x</h1>", string.Empty, string.Empty),
                options.GenerateServer ? "// server" : string.Empty,
                options.GenerateClient ? "// client" : string.Empty,
                string.Empty,
                Diagnostics,
                "hash"));
    }
}
