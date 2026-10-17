using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.AspNetCore;
using SvelteSharp.Engine.Okojo;

namespace SvelteSharp.Tests;

public sealed class EngineAdapterTests
{
    [Fact]
    public void UseOkojoOverridesBothRolesAndRegistersTheAdapter()
    {
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp(options =>
        {
            options.CompilerEngine = SvelteJavaScriptEngine.Jint;
            options.SsrEngine = SvelteJavaScriptEngine.Jint;
        });

        var returned = builder.UseOkojo();

        Assert.Same(builder, returned);
        Assert.Equal(SvelteJavaScriptEngine.Okojo, builder.Options.CompilerEngine);
        Assert.Equal(SvelteJavaScriptEngine.Okojo, builder.Options.SsrEngine);
        Assert.False(builder.Options.UseManagedCompilerBaseline);
        Assert.False(builder.Options.UseManagedSsrBaseline);
    }

    [Fact]
    public void RoleSpecificOkojoSelectorsChangeOnlyTheirRole()
    {
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp();

        builder.UseOkojoForCompiler();
        Assert.Equal(SvelteJavaScriptEngine.Okojo, builder.Options.CompilerEngine);
        Assert.Equal(SvelteJavaScriptEngine.Jint, builder.Options.SsrEngine);

        builder.UseOkojoForSsr();
        Assert.Equal(SvelteJavaScriptEngine.Okojo, builder.Options.SsrEngine);
    }

    [Fact]
    public void AddModelRegistersTheType()
    {
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp();

        builder.AddModel<EngineModel>();

        Assert.Contains(typeof(EngineModel), builder.ModelRegistry.GetTypes());
    }

    [SvelteModel]
    private sealed class EngineModel
    {
    }
}
