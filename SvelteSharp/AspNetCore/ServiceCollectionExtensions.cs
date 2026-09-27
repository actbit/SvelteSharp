using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SvelteSharp.Compiler;
using SvelteSharp.Build;
using SvelteSharp.Models;
using SvelteSharp.Rendering;

namespace SvelteSharp.AspNetCore;

/// <summary>Registers SvelteSharp with ASP.NET Core.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the SvelteSharp endpoint, compiler, manifest, and rendering services.</summary>
    public static SvelteSharpBuilder AddSvelteSharp(
        this IServiceCollection services,
        Action<SvelteSharpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SvelteSharpOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(_ => SvelteToolchain.Resolve(options.SvelteToolchainPath));
        services.AddSingleton(_ => new SveltePrebuiltArtifactStore(options.BuildOutputPath));
        services.AddSingleton<SvelteManifestStore>();
        services.AddSingleton<SvelteAssetStore>();
        var modelRegistry = new SvelteModelRegistry();
        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly is not null)
        {
            modelRegistry.RegisterAttributedModels(entryAssembly);
        }

        services.AddSingleton(modelRegistry);
        services.AddSingleton<ISvelteCompiler, ManagedSvelteCompiler>();
        services.AddSingleton<ISvelteGraphBundler, ManagedSvelteGraphBundler>();
        services.AddSingleton<ISvelteSsrRenderer, ManagedSvelteSsrRenderer>();
        services.AddSingleton<SvelteBuildPipeline>();
        services.TryAddSingleton<ISvelteViewSourceProvider>(serviceProvider =>
        {
            var host = serviceProvider.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            var root = Path.Combine(host?.ContentRootPath ?? Directory.GetCurrentDirectory(), options.ViewsPath);
            return new PhysicalSvelteViewSourceProvider(root);
        });
        services.TryAddSingleton<ISvelteModuleSourceProvider>(serviceProvider =>
            serviceProvider.GetService<ISvelteViewSourceProvider>() as ISvelteModuleSourceProvider
            ?? new PhysicalSvelteViewSourceProvider(Path.Combine(
                serviceProvider.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>()?.ContentRootPath
                    ?? Directory.GetCurrentDirectory(),
                options.ViewsPath)));
        services.AddSingleton<ISvelteRenderer, SvelteRenderer>();
        return new SvelteSharpBuilder(services, options, modelRegistry);
    }
}
