using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SvelteSharp.Compiler;
using SvelteSharp.JavaScript;
using SvelteSharp.Rendering;

namespace SvelteSharp.Engine.Jint;

/// <summary>Executes a bundled Svelte server graph in an isolated Jint runtime.</summary>
public sealed class JintSvelteSsrRenderer : ISvelteSsrRenderer
{
    private readonly ISsrJavaScriptRuntimeFactory _runtimeFactory;
    private readonly ISvelteGraphBundler _bundler;

    /// <summary>Creates a Jint SSR renderer.</summary>
    public JintSvelteSsrRenderer(
        ISsrJavaScriptRuntimeFactory runtimeFactory,
        ISvelteGraphBundler bundler)
    {
        _runtimeFactory = runtimeFactory;
        _bundler = bundler;
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
        var bundle = await _bundler.BundleAsync(compilation, viewName, SvelteGraphKind.Server, cancellationToken);
        var snapshotLiteral = JsonSerializer.Serialize(modelSnapshot);
        var script = $"{bundle.Code}\nglobalThis.__svelteSharpRendered = await SvelteSharpView.renderSnapshot({snapshotLiteral});\nglobalThis.__svelteSharpResult = JSON.stringify({{ body: globalThis.__svelteSharpRendered.body ?? globalThis.__svelteSharpRendered.html ?? '', head: globalThis.__svelteSharpRendered.head ?? '' }});";
        var limits = JavaScriptExecutionLimits.Default with
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxInputBytes = 32 * 1024 * 1024,
            MaxOutputBytes = 16 * 1024 * 1024
        };
        await using var runtime = _runtimeFactory.Create(new JavaScriptExecutionContext(limits, cancellationToken));
        using var document = await runtime.EvaluateJsonAsync(
            script,
            "globalThis.__svelteSharpResult",
            new JavaScriptExecutionContext(limits, cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("body", out _) && !root.TryGetProperty("html", out _))
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Jint,
                "ssr-render",
                $"Svelte server render returned an unexpected value: {root.GetRawText()}");
        }
        return new SvelteSsrOutput(
            root.GetProperty("body").GetString() ?? root.GetProperty("html").GetString() ?? string.Empty,
            root.GetProperty("head").GetString() ?? string.Empty);
    }
}
