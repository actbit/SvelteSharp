using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.Build;
using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class SvelteRendererIntegrationTests
{
    [Fact]
    public async Task ClientModeRendersShellAndMetadataWithoutServerHtml()
    {
        await using var host = SvelteTestHost.Create();
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var rendered = await host.RenderAsync(
            "Home",
            new { title = "Phone" },
            SvelteRenderMode.Client,
            new PageMetadata("Client Title", new Dictionary<string, string> { ["description"] = "desc" }));

        Assert.Contains("data-svelte-mode=\"client\"", rendered.Document);
        Assert.Contains("<title>Client Title</title>", rendered.Document);
        Assert.Contains("<meta name=\"description\" content=\"desc\" />", rendered.Document);
        Assert.Contains("type=\"module\"", rendered.Document);
        Assert.DoesNotContain("<h1>", rendered.Document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HybridModeAddsTheCspNonceToTheModuleScript()
    {
        await using var host = SvelteTestHost.Create(options => options.CspNonceFactory = _ => "nonce-123");
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var rendered = await host.RenderAsync("Home", new { title = "Phone" }, SvelteRenderMode.Hybrid);

        Assert.Contains("nonce=\"nonce-123\"", rendered.Document);
    }

    [Fact]
    public async Task EmitsAndStoresExternalCss()
    {
        await using var host = SvelteTestHost.Create();
        host.Views.Set(
            "Home",
            "<script>let { model } = $props();</script><style>h1 { color: red; }</style><h1>{model.title}</h1>");

        var rendered = await host.RenderAsync("Home", new { title = "Phone" }, SvelteRenderMode.Server);

        Assert.Contains("<link rel=\"stylesheet\"", rendered.Document);
        Assert.Contains("views/Home.css", rendered.Document);

        var assets = host.Services.GetRequiredService<SvelteAssetStore>();
        Assert.True(assets.TryGet(rendered.BuildId, "views/Home.css", out var stored));
        Assert.NotNull(stored);
        Assert.Contains("text/css", stored.ContentType);
        Assert.Contains("color", stored.Content);
    }

    [Fact]
    public async Task RendersRealSvelteControlBlocksOnTheServer()
    {
        await using var host = SvelteTestHost.Create();
        host.Views.Set(
            "List",
            """
            <script>let { model } = $props();</script>
            {#if model.show}<p id="state">yes</p>{:else}<p id="state">no</p>{/if}
            <ul>{#each model.items as item}<li>{item}</li>{/each}</ul>
            """);

        var rendered = await host.RenderAsync(
            "List",
            new { show = true, items = new[] { "a", "b" } },
            SvelteRenderMode.Server);

        Assert.Contains(">yes</p>", rendered.Document);
        Assert.Contains("<li>a</li>", rendered.Document);
        Assert.Contains("<li>b</li>", rendered.Document);
    }

    [Fact]
    public async Task ManifestRebuildsWhenTheSourceChanges()
    {
        await using var host = SvelteTestHost.Create();
        host.Views.Set("Home", "<h1>one</h1>");
        var first = await host.RenderAsync("Home", null, SvelteRenderMode.Server);

        host.Views.Set("Home", "<h1>two</h1>");
        var second = await host.RenderAsync("Home", null, SvelteRenderMode.Server);

        Assert.NotEqual(first.BuildId, second.BuildId);
        Assert.Contains("<h1>two</h1>", second.Document);
    }

    [Fact]
    public async Task UnknownViewProducesSve2002()
    {
        await using var host = SvelteTestHost.Create();

        var exception = await Assert.ThrowsAsync<SvelteViewException>(
            async () => await host.RenderAsync("Missing", null, SvelteRenderMode.Server));

        Assert.Equal("SVE2002", exception.Code);
    }

    [Fact]
    public async Task ManagedCompilerErrorsProduceSve1000()
    {
        await using var host = SvelteTestHost.Create(engines: _ => { });
        host.Views.Set("Home", "{@html model.raw}");

        var exception = await Assert.ThrowsAsync<SvelteViewException>(
            async () => await host.RenderAsync("Home", new { raw = "<b>x</b>" }, SvelteRenderMode.Server));

        Assert.Equal("SVE1000", exception.Code);
    }

    [Fact]
    public async Task AppliesTheConfiguredCacheControlHeader()
    {
        await using var host = SvelteTestHost.Create(options => options.CacheControl = "public,max-age=60");
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var (context, body) = await host.ExecuteViewAsync("Home", new { title = "Phone" }, SvelteRenderMode.Server);

        Assert.Equal("public,max-age=60", context.Response.Headers.CacheControl.ToString());
        Assert.Contains("<h1>Phone</h1>", body);
    }
}
