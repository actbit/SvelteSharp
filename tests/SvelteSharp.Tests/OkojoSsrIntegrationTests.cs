using SvelteSharp.Okojo;

namespace SvelteSharp.Tests;

public sealed class OkojoSsrIntegrationTests
{
    [Fact]
    public async Task RendersServerAndHybridThroughDependencyInjection()
    {
        await using var host = SvelteTestHost.Create(engines: b => { b.UseOkojo(); });
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var server = await host.RenderAsync("Home", new { title = "Phone" }, SvelteRenderMode.Server);
        Assert.Contains("<h1>Phone</h1>", server.Document);

        var hybrid = await host.RenderAsync("Home", new { title = "Tablet" }, SvelteRenderMode.Hybrid);
        Assert.Contains("<h1>Tablet</h1>", hybrid.Document);
        Assert.Contains("data-svelte-mode=\"hybrid\"", hybrid.Document);
    }

    [Fact]
    public async Task PooledRuntimesDoNotLeakModelsAcrossRequests()
    {
        await using var host = SvelteTestHost.Create(engines: b => { b.UseOkojo(); });
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var first = await host.RenderAsync("Home", new { title = "First" }, SvelteRenderMode.Server);
        var second = await host.RenderAsync("Home", new { title = "Second" }, SvelteRenderMode.Server);

        Assert.Contains("<h1>First</h1>", first.Document);
        Assert.Contains("<h1>Second</h1>", second.Document);
        Assert.DoesNotContain("First", second.Document, StringComparison.Ordinal);
    }
}
