using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SvelteSharp.Compiler;
using SvelteSharp.JavaScript;
using SvelteSharp.Rendering;

namespace SvelteSharp.Engine.Jint;

/// <summary>Executes a bundled Svelte server graph through Jint.</summary>
public sealed class JintSvelteSsrRenderer : ISvelteSsrRenderer
{
    private readonly ISsrJavaScriptRuntimeFactory _runtimeFactory;
    private readonly ISvelteGraphBundler _bundler;
    private readonly JintSsrRuntimePool? _runtimePool;
    private readonly ConcurrentDictionary<string, CachedBundle> _bundleCache = new(StringComparer.Ordinal);

    /// <summary>Creates a Jint SSR renderer.</summary>
    public JintSvelteSsrRenderer(
        ISsrJavaScriptRuntimeFactory runtimeFactory,
        ISvelteGraphBundler bundler,
        JintSsrRuntimePool? runtimePool = null)
    {
        _runtimeFactory = runtimeFactory;
        _bundler = bundler;
        _runtimePool = runtimePool;
    }

    /// <inheritdoc />
    public async ValueTask<SvelteSsrOutput> RenderAsync(
        SvelteCompilationResult compilation,
        string modelSnapshot,
        object? managedModel,
        string viewName,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var bundle = await GetBundleAsync(compilation, viewName, cancellationToken);
        var limits = JavaScriptExecutionLimits.Default with
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxInputBytes = 32 * 1024 * 1024,
            MaxOutputBytes = 16 * 1024 * 1024
        };

        if (_runtimePool is not null && _runtimeFactory.Engine == SvelteJavaScriptEngine.Jint)
        {
            return await _runtimePool.RenderAsync(
                bundle.Code,
                modelSnapshot,
                compilation.RequiresAsyncRender,
                new JavaScriptExecutionContext(limits, cancellationToken));
        }

        var snapshotLiteral = JsonSerializer.Serialize(modelSnapshot);
        var script = $"{bundle.Code}\nglobalThis.__svelteSharpRendered = await SvelteSharpView.renderSnapshot({snapshotLiteral});\nglobalThis.__svelteSharpResult = JSON.stringify({{ body: globalThis.__svelteSharpRendered.body ?? globalThis.__svelteSharpRendered.html ?? '', head: globalThis.__svelteSharpRendered.head ?? '' }});";
        await using var runtime = _runtimeFactory.Create(new JavaScriptExecutionContext(limits, cancellationToken));
        using var document = await runtime.EvaluateJsonAsync(
            script,
            "globalThis.__svelteSharpResult",
            new JavaScriptExecutionContext(limits, cancellationToken));
        return ParseOutput(document);
    }

    private static SvelteSsrOutput ParseOutput(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("body", out var body)
            && !root.TryGetProperty("html", out body))
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Jint,
                "ssr-render",
                $"Svelte server render returned an unexpected value: {root.GetRawText()}");
        }

        return new SvelteSsrOutput(
            body.GetString() ?? string.Empty,
            root.TryGetProperty("head", out var head) ? head.GetString() ?? string.Empty : string.Empty);
    }

    private async ValueTask<SvelteGraphBundle> GetBundleAsync(
        SvelteCompilationResult compilation,
        string viewName,
        CancellationToken cancellationToken)
    {
        if (_bundleCache.TryGetValue(viewName, out var cached)
            && ReferenceEquals(cached.Compilation, compilation))
        {
            return cached.Bundle;
        }

        var bundle = await _bundler.BundleAsync(compilation, viewName, SvelteGraphKind.Server, cancellationToken);
        _bundleCache[viewName] = new CachedBundle(compilation, bundle);
        return bundle;
    }

    private sealed record CachedBundle(SvelteCompilationResult Compilation, SvelteGraphBundle Bundle);
}
