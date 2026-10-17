using Microsoft.AspNetCore.Http;
using SvelteSharp.Compiler;

namespace SvelteSharp.Rendering;

/// <summary>Input to a Svelte renderer.</summary>
public sealed record SvelteViewRequest(
    string ViewName,
    object? Model,
    SvelteRenderMode Mode,
    PageMetadata? Metadata,
    HttpContext HttpContext);

/// <summary>Result of producing one complete HTML document.</summary>
public sealed record RenderedSvelteView(
    string Document,
    string BuildId,
    string? ClientEntry,
    string CacheControl = "no-store");

/// <summary>Renders Svelte views into ASP.NET Core responses.</summary>
public interface ISvelteRenderer
{
    /// <summary>Renders one request without changing the selected engine.</summary>
    ValueTask<RenderedSvelteView> RenderAsync(SvelteViewRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Identifies the graph that is being bundled.</summary>
public enum SvelteGraphKind
{
    /// <summary>Private server graph.</summary>
    Server,
    /// <summary>Public browser graph.</summary>
    Client
}

/// <summary>A graph after module resolution and native bundling.</summary>
public sealed record SvelteGraphBundle(string Code, string? SourceMap = null);

/// <summary>Bundles a compiled graph without invoking Node.</summary>
public interface ISvelteGraphBundler
{
    /// <summary>Creates a private server graph or public browser graph.</summary>
    ValueTask<SvelteGraphBundle> BundleAsync(
        SvelteCompilationResult compilation,
        string viewName,
        SvelteGraphKind kind,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of one Svelte server render.</summary>
public sealed record SvelteSsrOutput(string Body, string Head);

/// <summary>Renders one request through the selected server JavaScript host.</summary>
public interface ISvelteSsrRenderer
{
    /// <summary>Renders from the same JSON snapshot used by the browser.</summary>
    ValueTask<SvelteSsrOutput> RenderAsync(
        SvelteCompilationResult compilation,
        string modelSnapshot,
        object? managedModel,
        string viewName,
        HttpContext httpContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Node-free fallback used only when no engine adapter is registered.</summary>
public sealed class ManagedSvelteGraphBundler : ISvelteGraphBundler
{
    /// <inheritdoc />
    public ValueTask<SvelteGraphBundle> BundleAsync(
        SvelteCompilationResult compilation,
        string viewName,
        SvelteGraphKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SvelteGraphBundle(
            kind == SvelteGraphKind.Server ? compilation.ServerGraph : compilation.ClientGraph));
    }
}

/// <summary>Managed renderer retained for diagnostics and local baseline use.</summary>
public sealed class ManagedSvelteSsrRenderer : ISvelteSsrRenderer
{
    /// <inheritdoc />
    public ValueTask<SvelteSsrOutput> RenderAsync(
        SvelteCompilationResult compilation,
        string modelSnapshot,
        object? managedModel,
        string viewName,
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SvelteSsrOutput(
            compilation.Template.Render(managedModel),
            compilation.Template.RenderHead(managedModel)));
    }
}
