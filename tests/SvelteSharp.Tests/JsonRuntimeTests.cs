using SvelteSharp.Engine.Jint;
using SvelteSharp.Engine.Okojo;
using SvelteSharp.JavaScript;

namespace SvelteSharp.Tests;

public sealed class JsonRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundTripsJsonThroughTheSelectedEngine(bool okojo)
    {
        IJavaScriptJsonRuntimeFactory factory = okojo ? new OkojoJsonRuntimeFactory() : new JintJsonRuntimeFactory();
        var context = new JavaScriptExecutionContext(JavaScriptExecutionLimits.Default);
        await using var runtime = factory.Create(context);

        using var document = await runtime.EvaluateJsonAsync(
            "globalThis.__result = JSON.stringify({ name: 'svelte', count: 3, items: ['a', 'b'] });",
            "globalThis.__result",
            context);

        var root = document.RootElement;
        Assert.Equal("svelte", root.GetProperty("name").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapsInvalidJsonAsAnEngineException(bool okojo)
    {
        IJavaScriptJsonRuntimeFactory factory = okojo ? new OkojoJsonRuntimeFactory() : new JintJsonRuntimeFactory();
        var context = new JavaScriptExecutionContext(JavaScriptExecutionLimits.Default);
        await using var runtime = factory.Create(context);

        await Assert.ThrowsAsync<JavaScriptEngineException>(async () =>
            await runtime.EvaluateJsonAsync("globalThis.__result = 'not json';", "globalThis.__result", context));
    }

    [Fact]
    public void FactoriesReportTheSelectedEngine()
    {
        Assert.Equal(SvelteJavaScriptEngine.Jint, new JintJsonRuntimeFactory().Engine);
        Assert.Equal(SvelteJavaScriptEngine.Okojo, new OkojoJsonRuntimeFactory().Engine);
    }
}
