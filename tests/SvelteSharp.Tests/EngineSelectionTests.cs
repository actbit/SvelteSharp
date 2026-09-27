using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.AspNetCore;
using SvelteSharp.Engine.Jint;

namespace SvelteSharp.Tests;

public sealed class EngineSelectionTests
{
    [Fact]
    public void SpecificFluentSelectionsOverrideOptionsLambda()
    {
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp(options =>
        {
            options.CompilerEngine = SvelteJavaScriptEngine.Okojo;
            options.SsrEngine = SvelteJavaScriptEngine.Okojo;
        });

        builder.UseJintForCompiler()
            .UseJintForSsr();

        Assert.Equal(SvelteJavaScriptEngine.Jint, builder.Options.CompilerEngine);
        Assert.Equal(SvelteJavaScriptEngine.Jint, builder.Options.SsrEngine);
    }

    [Fact]
    public void UseJintIsTheBothHostsShortcut()
    {
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp(options =>
        {
            options.CompilerEngine = SvelteJavaScriptEngine.Okojo;
            options.SsrEngine = SvelteJavaScriptEngine.Okojo;
        });

        builder.UseJint();

        Assert.Equal(SvelteJavaScriptEngine.Jint, builder.Options.CompilerEngine);
        Assert.Equal(SvelteJavaScriptEngine.Jint, builder.Options.SsrEngine);
    }
}
