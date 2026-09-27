using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.AspNetCore;
using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.JavaScript;
using SvelteSharp.Rendering;

namespace SvelteSharp.Engine.Jint;

/// <summary>Registers the Jint compiler and SSR adapter.</summary>
public static class JintSvelteSharpExtensions
{
    /// <summary>Registration hook used by the engine-neutral fluent selector.</summary>
    public static SvelteSharpBuilder RegisterSsr(SvelteSharpBuilder builder)
        => ConfigureSsr(builder);

    /// <summary>Registration hook used by the engine-neutral fluent selector.</summary>
    public static SvelteSharpBuilder RegisterCompiler(SvelteSharpBuilder builder)
        => ConfigureCompiler(builder);

    /// <summary>
    /// Uses Jint for both compiler and SSR hosts. This is the short form of
    /// <c>UseJintForCompiler().UseJintForSsr()</c> and overrides both engine
    /// selections made earlier in the registration chain.
    /// </summary>
    public static SvelteSharpBuilder UseJint(this SvelteSharpBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Options.CompilerEngine = SvelteJavaScriptEngine.Jint;
        builder.Options.SsrEngine = SvelteJavaScriptEngine.Jint;
        builder.Options.UseManagedCompilerBaseline = false;
        builder.Options.UseManagedSsrBaseline = false;
        ConfigureCompiler(builder);
        return ConfigureSsr(builder);
    }

    /// <summary>Uses Jint only for the compiler host.</summary>
    public static SvelteSharpBuilder UseJintForCompilerAdapter(this SvelteSharpBuilder builder)
    {
        return ConfigureCompiler(builder);
    }

    private static SvelteSharpBuilder ConfigureCompiler(SvelteSharpBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Options.CompilerEngine = SvelteJavaScriptEngine.Jint;
        builder.Options.UseManagedCompilerBaseline = false;
        builder.Services.AddSingleton<JintJsonRuntimeFactory>();
        builder.Services.AddSingleton<ICompilerJavaScriptRuntimeFactory>(sp => sp.GetRequiredService<JintJsonRuntimeFactory>());
        builder.Services.AddSingleton<ISvelteCompiler>(sp =>
        {
            var prebuilt = sp.GetRequiredService<SveltePrebuiltArtifactStore>();
            var options = sp.GetRequiredService<SvelteSharpOptions>();
            if (!prebuilt.IsAvailable && !options.AllowRuntimeBuildFallback)
            {
                throw new InvalidOperationException(
                    "No prebuilt SvelteSharp artifact was found. Run 'dotnet build' before starting the application, " +
                    "or explicitly set AllowRuntimeBuildFallback for development only.");
            }

            return prebuilt.IsAvailable
                ? new PrebuiltSvelteCompiler(prebuilt)
                : new SvelteCompiler(
                    sp.GetRequiredService<ICompilerJavaScriptRuntimeFactory>(),
                    sp.GetService<ISvelteModuleSourceProvider>(),
                    sp.GetRequiredService<SvelteToolchain>());
        });
        builder.Services.AddSingleton<ISvelteGraphBundler>(sp =>
        {
            var prebuilt = sp.GetRequiredService<SveltePrebuiltArtifactStore>();
            var options = sp.GetRequiredService<SvelteSharpOptions>();
            if (!prebuilt.IsAvailable && !options.AllowRuntimeBuildFallback)
            {
                throw new InvalidOperationException(
                    "No prebuilt SvelteSharp artifact was found. Run 'dotnet build' before starting the application, " +
                    "or explicitly set AllowRuntimeBuildFallback for development only.");
            }

            return prebuilt.IsAvailable
                ? new PrebuiltSvelteGraphBundler(prebuilt)
                : new NativeSvelteGraphBundler(
                    sp.GetService<ISvelteModuleSourceProvider>(),
                    new NativeEsbuildOptions { ToolchainPath = sp.GetRequiredService<SvelteToolchain>().RootPath });
        });
        return builder;
    }

    private static SvelteSharpBuilder ConfigureSsr(SvelteSharpBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Options.SsrEngine = SvelteJavaScriptEngine.Jint;
        builder.Options.UseManagedSsrBaseline = false;
        builder.Services.AddSingleton<JintJsonRuntimeFactory>();
        builder.Services.AddSingleton<ISsrJavaScriptRuntimeFactory>(sp => sp.GetRequiredService<JintJsonRuntimeFactory>());
        builder.Services.AddSingleton<ISvelteGraphBundler>(sp =>
        {
            var prebuilt = sp.GetRequiredService<SveltePrebuiltArtifactStore>();
            return prebuilt.IsAvailable
                ? new PrebuiltSvelteGraphBundler(prebuilt)
                : new NativeSvelteGraphBundler(
                    sp.GetService<ISvelteModuleSourceProvider>(),
                    new NativeEsbuildOptions { ToolchainPath = sp.GetRequiredService<SvelteToolchain>().RootPath });
        });
        builder.Services.AddSingleton<ISvelteSsrRenderer, JintSvelteSsrRenderer>();
        return builder;
    }
}
