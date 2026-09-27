using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.Rendering;

namespace SvelteSharp;

/// <summary>A validated endpoint result that renders a Svelte view.</summary>
public sealed class SvelteViewResult : IResult
{
    /// <summary>Creates a view result.</summary>
    public SvelteViewResult(
        string viewName,
        object? model,
        SvelteRenderMode mode = SvelteRenderMode.Server,
        PageMetadata? metadata = null)
    {
        ViewName = SvelteViewName.Validate(viewName);
        Model = model;
        Mode = mode;
        Metadata = metadata;
    }

    /// <summary>The logical view name, for example <c>Products/Detail</c>.</summary>
    public string ViewName { get; }

    /// <summary>The CLR model supplied by the endpoint.</summary>
    public object? Model { get; }

    /// <summary>The requested rendering mode.</summary>
    public SvelteRenderMode Mode { get; }

    /// <summary>Metadata used by client-only views.</summary>
    public PageMetadata? Metadata { get; }

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var renderer = httpContext.RequestServices.GetService<ISvelteRenderer>()
            ?? throw new InvalidOperationException(
                "SvelteSharp is not registered. Call services.AddSvelteSharp() before mapping endpoints.");

        var rendered = await renderer.RenderAsync(
            new SvelteViewRequest(ViewName, Model, Mode, Metadata, httpContext),
            httpContext.RequestAborted);

        httpContext.Response.ContentType = "text/html; charset=utf-8";
        httpContext.Response.Headers.CacheControl = rendered.CacheControl;
        await httpContext.Response.WriteAsync(rendered.Document, httpContext.RequestAborted);
    }
}

/// <summary>Creates Svelte endpoint results.</summary>
public static class Svelte
{
    /// <summary>Creates a result for a Svelte view.</summary>
    public static SvelteViewResult View(
        string viewName,
        object? model = null,
        SvelteRenderMode mode = SvelteRenderMode.Server,
        PageMetadata? metadata = null)
        => new(viewName, model, mode, metadata);
}

/// <summary>Validates logical view names before they reach a file provider.</summary>
public static class SvelteViewName
{
    /// <summary>Validates and returns a normalized logical view name.</summary>
    public static string Validate(string viewName)
    {
        if (string.IsNullOrWhiteSpace(viewName))
        {
            throw new ArgumentException("A view name is required.", nameof(viewName));
        }

        var normalized = viewName.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("A view name must not contain path traversal segments.", nameof(viewName));
        }

        if (normalized.Any(char.IsControl) || normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A view name contains an invalid character.", nameof(viewName));
        }

        return normalized.EndsWith(".svelte", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^".svelte".Length]
            : normalized;
    }
}
