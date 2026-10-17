using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class InMemorySvelteViewSourceProviderTests
{
    [Fact]
    public async Task ReturnsRegisteredSourceAndNullForUnknown()
    {
        var provider = new InMemorySvelteViewSourceProvider();
        provider.Set("Products/Detail", "<h1>hi</h1>");

        var found = await provider.GetAsync("Products/Detail");

        Assert.NotNull(found);
        Assert.Equal("<h1>hi</h1>", found.Source);
        Assert.Null(await provider.GetAsync("Missing"));
    }

    [Fact]
    public void SetModuleRequiresSvelteTsExtension()
        => Assert.Throws<ArgumentException>(
            () => new InMemorySvelteViewSourceProvider().SetModule("format.ts", "x"));

    [Fact]
    public async Task ResolvesRelativeModulesAndRejectsNonRelativeSpecifiers()
    {
        var provider = new InMemorySvelteViewSourceProvider();
        provider.SetModule("Products/format.svelte.ts", "export const x = 1;");

        var module = await provider.GetAsync("Products/Detail", "./format.svelte.ts");

        Assert.NotNull(module);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.GetAsync("Products/Detail", "format.svelte.ts"));
    }

    [Fact]
    public async Task RejectsModulesEscapingTheViewRoot()
    {
        var provider = new InMemorySvelteViewSourceProvider();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await provider.GetAsync("Detail", "../escape.svelte.ts"));
    }
}

public sealed class PhysicalSvelteViewSourceProviderTests
{
    [Fact]
    public async Task LoadsViewsAndOnlyServesSvelteModules()
    {
        var root = Directory.CreateTempSubdirectory("svelte-views").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "Home.svelte"), "<h1>hi</h1>");
            var products = Directory.CreateDirectory(Path.Combine(root, "Products")).FullName;
            await File.WriteAllTextAsync(Path.Combine(products, "Detail.svelte"), "<h1>detail</h1>");
            await File.WriteAllTextAsync(Path.Combine(products, "format.svelte.ts"), "export const x = 1;");
            await File.WriteAllTextAsync(Path.Combine(products, "plain.ts"), "export const y = 2;");

            var provider = new PhysicalSvelteViewSourceProvider(root);

            var home = await provider.GetAsync("Home");
            Assert.NotNull(home);
            Assert.Equal("<h1>hi</h1>", home.Source);
            Assert.Null(await provider.GetAsync("Missing"));

            var module = await provider.GetAsync("Products/Detail", "./format.svelte.ts");
            Assert.NotNull(module);
            Assert.True(module.IsModule);

            Assert.Null(await provider.GetAsync("Products/Detail", "./plain.ts"));
            Assert.Null(await provider.GetAsync("Products/Detail", "format.svelte.ts"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
