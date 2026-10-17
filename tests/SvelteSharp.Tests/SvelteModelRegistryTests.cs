using SvelteSharp.Models;

namespace SvelteSharp.Tests;

public sealed class SvelteModelRegistryTests
{
    [Fact]
    public void DeduplicatesRegistrations()
    {
        var registry = new SvelteModelRegistry();
        registry.Register<SimpleModel>();
        registry.Register(typeof(SimpleModel));

        Assert.Single(registry.GetTypes());
    }

    [Fact]
    public void SortsTypesByFullName()
    {
        var registry = new SvelteModelRegistry();
        registry.Register(typeof(ZModel));
        registry.Register(typeof(AModel));

        Assert.Equal(new[] { typeof(AModel), typeof(ZModel) }, registry.GetTypes());
    }

    [Fact]
    public void DiscoversAttributedModelsButSkipsGenericDefinitions()
    {
        var registry = new SvelteModelRegistry();

        var discovered = registry.RegisterAttributedModels(typeof(SvelteModelRegistryTests).Assembly);

        Assert.True(discovered > 0);
        Assert.DoesNotContain(registry.GetTypes(), type => type.IsGenericTypeDefinition);
        Assert.Contains(typeof(SimpleModel), registry.GetTypes());
    }

    [SvelteModel]
    private sealed class SimpleModel
    {
    }

    [SvelteModel]
    private sealed class AModel
    {
    }

    [SvelteModel]
    private sealed class ZModel
    {
    }

    [SvelteModel]
    private sealed class GenericModel<T>
    {
    }
}
