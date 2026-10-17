using SvelteSharp.AspNetCore;
using SvelteSharp.JavaScript;
using SvelteSharp.Jint;
using SvelteSharp.Okojo;

namespace SvelteSharp.Tests;

public sealed class SsrRuntimeIsolationTests
{
    private const string CounterView =
        "<script>import { next } from './counter.svelte.ts'; let { model } = $props();</script><p>{next()}</p>";

    private const string StatefulModule = "let count = 0; export function next() { return ++count; }";

    private static Action<SvelteSharpBuilder> Engine(bool okojo)
        => okojo ? b => { b.UseOkojo(); } : b => { b.UseJint(); };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedModeDoesNotLeakModuleScopeState(bool okojo)
    {
        await using var host = SvelteTestHost.Create(
            options => options.SsrMaxPooledEnginesPerBundle = 0,
            Engine(okojo));
        host.Views.SetModule("counter.svelte.ts", StatefulModule);
        host.Views.Set("Counter", CounterView);

        var first = await host.RenderAsync("Counter", null, SvelteRenderMode.Server);
        var second = await host.RenderAsync("Counter", null, SvelteRenderMode.Server);

        Assert.Contains("<p>1</p>", first.Document);
        Assert.Contains("<p>1</p>", second.Document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PooledModeSharesModuleScopeStateForStatefulModules(bool okojo)
    {
        // Documents the pooling tradeoff: pooled runtimes are only safe for render-pure modules,
        // so stateful module scope must use isolation mode.
        await using var host = SvelteTestHost.Create(engines: Engine(okojo));
        host.Views.SetModule("counter.svelte.ts", StatefulModule);
        host.Views.Set("Counter", CounterView);

        var first = await host.RenderAsync("Counter", null, SvelteRenderMode.Server);
        var second = await host.RenderAsync("Counter", null, SvelteRenderMode.Server);

        Assert.Contains("<p>1</p>", first.Document);
        Assert.Contains("<p>2</p>", second.Document);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctModelsDoNotMixAcrossSequentialRequests(bool okojo)
    {
        await using var host = SvelteTestHost.Create(engines: Engine(okojo));
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        for (var index = 0; index < 8; index++)
        {
            var rendered = await host.RenderAsync("Home", new { title = $"Item{index}" }, SvelteRenderMode.Server);

            Assert.Contains($"<h1>Item{index}</h1>", rendered.Document);
            Assert.DoesNotContain($"Item{(index + 1) % 8}", rendered.Document, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DistinctModelsDoNotMixAcrossParallelRequests()
    {
        await using var host = SvelteTestHost.Create();
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var renders = Enumerable.Range(0, 16).Select(async index =>
        {
            var rendered = await host.RenderAsync("Home", new { title = $"Parallel{index}" }, SvelteRenderMode.Server);
            return (index, rendered.Document);
        });

        foreach (var (index, document) in await Task.WhenAll(renders))
        {
            Assert.Contains($"<h1>Parallel{index}</h1>", document);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRenderDoesNotPoisonLaterRequests(bool okojo)
    {
        await using var host = SvelteTestHost.Create(engines: Engine(okojo));
        host.Views.Set("Bomb", "<script>let { model } = $props();</script><p>{model.value.nested}</p>");
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        await Assert.ThrowsAnyAsync<JavaScriptEngineException>(async () =>
            await host.RenderAsync("Bomb", new { }, SvelteRenderMode.Server));

        var rendered = await host.RenderAsync("Home", new { title = "Recovered" }, SvelteRenderMode.Server);

        Assert.Contains("<h1>Recovered</h1>", rendered.Document);
    }
}
