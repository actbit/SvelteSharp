using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SvelteSharp.Tests;

public sealed class SvelteViewResultTests
{
    [Theory]
    [InlineData("")]
    [InlineData("../Home")]
    [InlineData("bad:name")]
    public void ValidatesViewNameAtConstruction(string viewName)
        => Assert.Throws<ArgumentException>(() => new SvelteViewResult(viewName, null));

    [Fact]
    public void NormalizesViewName()
        => Assert.Equal("Products/Detail", new SvelteViewResult("Products\\Detail.svelte", null).ViewName);

    [Fact]
    public void ViewFactoryPreservesModeAndMetadata()
    {
        var metadata = new PageMetadata("Title");

        var result = Svelte.View("Home", new { value = 1 }, SvelteRenderMode.Client, metadata);

        Assert.Equal(SvelteRenderMode.Client, result.Mode);
        Assert.Same(metadata, result.Metadata);
    }

    [Fact]
    public async Task ExecuteAsyncThrowsWhenTheRendererIsNotRegistered()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new SvelteViewResult("Home", null).ExecuteAsync(context));
    }
}
