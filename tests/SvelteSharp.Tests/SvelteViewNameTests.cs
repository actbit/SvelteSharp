namespace SvelteSharp.Tests;

public sealed class SvelteViewNameTests
{
    [Theory]
    [InlineData("Home", "Home")]
    [InlineData("Products/Detail", "Products/Detail")]
    [InlineData("Products\\Detail", "Products/Detail")]
    [InlineData("/Views/Home/", "Views/Home")]
    [InlineData("Home.svelte", "Home")]
    [InlineData("Views/Home.svelte", "Views/Home")]
    public void NormalizesLogicalViewNames(string input, string expected)
        => Assert.Equal(expected, SvelteViewName.Validate(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../Home")]
    [InlineData("a/../b")]
    [InlineData("C:/Views/Home")]
    [InlineData("bad\tname")]
    public void RejectsUnsafeViewNames(string input)
        => Assert.Throws<ArgumentException>(() => SvelteViewName.Validate(input));
}
