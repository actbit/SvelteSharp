using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.AspNetCore;
using SvelteSharp.Compiler;
using SvelteSharp.Jint;
using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

/// <summary>Builds an ASP.NET Core service provider that renders views through a real engine.</summary>
internal sealed class SvelteTestHost : IAsyncDisposable
{
    private SvelteTestHost(ServiceProvider services, InMemorySvelteViewSourceProvider views)
    {
        Services = services;
        Views = views;
    }

    public ServiceProvider Services { get; }

    public InMemorySvelteViewSourceProvider Views { get; }

    /// <summary>Creates a host that renders views from in-memory sources.</summary>
    public static SvelteTestHost Create(
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
        return new SvelteTestHost(services.BuildServiceProvider(), views);
    }

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
