using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.AspNetCore;
using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.Engine.Jint;
using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class SvelteRendererIntegrationTests
{
    [Fact]
    public async Task ClientModeRendersShellAndMetadataWithoutServerHtml()
    {
        await using var host = CreateHost();
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
        await using var host = CreateHost(options => options.CspNonceFactory = _ => "nonce-123");
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var rendered = await host.RenderAsync("Home", new { title = "Phone" }, SvelteRenderMode.Hybrid);

        Assert.Contains("nonce=\"nonce-123\"", rendered.Document);
    }

    [Fact]
    public async Task EmitsAndStoresExternalCss()
    {
        await using var host = CreateHost();
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
        await using var host = CreateHost();
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
        await using var host = CreateHost();
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
        await using var host = CreateHost();

        var exception = await Assert.ThrowsAsync<SvelteViewException>(
            async () => await host.RenderAsync("Missing", null, SvelteRenderMode.Server));

        Assert.Equal("SVE2002", exception.Code);
    }

    [Fact]
    public async Task ManagedCompilerErrorsProduceSve1000()
    {
        await using var host = CreateHost(engines: _ => { });
        host.Views.Set("Home", "{@html model.raw}");

        var exception = await Assert.ThrowsAsync<SvelteViewException>(
            async () => await host.RenderAsync("Home", new { raw = "<b>x</b>" }, SvelteRenderMode.Server));

        Assert.Equal("SVE1000", exception.Code);
    }

    [Fact]
    public async Task AppliesTheConfiguredCacheControlHeader()
    {
        await using var host = CreateHost(options => options.CacheControl = "public,max-age=60");
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var (context, body) = await host.ExecuteViewAsync("Home", new { title = "Phone" }, SvelteRenderMode.Server);

        Assert.Equal("public,max-age=60", context.Response.Headers.CacheControl.ToString());
        Assert.Contains("<h1>Phone</h1>", body);
    }

    private static TestSvelteHost CreateHost(
        Action<SvelteSharpOptions>? configure = null,
        Action<SvelteSharpBuilder>? engines = null)
    {
        var views = new InMemorySvelteViewSourceProvider();
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp(options =>
        {
            options.SvelteToolchainPath = TestToolchain.Path;
            options.AllowRuntimeBuildFallback = true;
            configure?.Invoke(options);
        });
        (engines ?? (b => { b.UseJint(); })).Invoke(builder);
        services.AddSingleton<ISvelteViewSourceProvider>(views);
        services.AddSingleton<ISvelteModuleSourceProvider>(views);
        return new TestSvelteHost(services.BuildServiceProvider(), views);
    }

    private sealed class TestSvelteHost(ServiceProvider services, InMemorySvelteViewSourceProvider views) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public InMemorySvelteViewSourceProvider Views { get; } = views;

        public async Task<RenderedSvelteView> RenderAsync(
            string view,
            object? model,
            SvelteRenderMode mode,
            PageMetadata? metadata = null)
        {
            var renderer = Services.GetRequiredService<ISvelteRenderer>();
            return await renderer.RenderAsync(new SvelteViewRequest(view, model, mode, metadata, CreateContext()));
        }

        public async Task<(HttpContext Context, string Body)> ExecuteViewAsync(
            string view,
            object? model,
            SvelteRenderMode mode,
            PageMetadata? metadata = null)
        {
            var context = CreateContext();
            await using var body = new MemoryStream();
            context.Response.Body = body;
            await Svelte.View(view, model, mode, metadata).ExecuteAsync(context);
            body.Position = 0;
            using var reader = new StreamReader(body);
            return (context, await reader.ReadToEndAsync());
        }

        public ValueTask DisposeAsync() => Services.DisposeAsync();

        private DefaultHttpContext CreateContext()
        {
            var context = new DefaultHttpContext { RequestServices = Services };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("example.test");
            return context;
        }
    }
}
