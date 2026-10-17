using SvelteSharp.Compiler;

namespace SvelteSharp.Tests;

public sealed class ManagedSvelteCompilerTests
{
    [Theory]
    [InlineData("{@html raw}", "SVE1001")]
    [InlineData("<button on:click={go}>x</button>", "SVE1002")]
    [InlineData("<script>let x = 1;", "SVE1003")]
    [InlineData("{#if ok}<p>y</p>{/if}", "SVE1004")]
    public async Task ReportsManagedBaselineDiagnostics(string source, string code)
    {
        var result = await CompileAsync(source);

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code && diagnostic.Severity == SvelteDiagnosticSeverity.Error);
        Assert.False(result.IsSuccessful);
    }

    [Fact]
    public async Task ExtractsHeadStyleAndBody()
    {
        var result = await CompileAsync(
            """
            <script>let x = 1;</script>
            <svelte:head><title>Hello</title></svelte:head>
            <style>h1 { color: red; }</style>
            <h1>Body</h1>
            """);

        Assert.True(result.IsSuccessful, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.Equal("<title>Hello</title>", result.Template.Head);
        Assert.Contains("h1 { color: red; }", result.Template.Css);
        Assert.Contains("<h1>Body</h1>", result.Template.Body);
        Assert.DoesNotContain("<script", result.Template.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HonorsGraphGenerationFlags()
    {
        var serverOnly = await CompileAsync(
            "<h1>x</h1>",
            new SvelteCompilerOptions { GenerateServer = true, GenerateClient = false });
        Assert.False(string.IsNullOrEmpty(serverOnly.ServerGraph));
        Assert.Equal(string.Empty, serverOnly.ClientGraph);

        var clientOnly = await CompileAsync(
            "<h1>x</h1>",
            new SvelteCompilerOptions { GenerateServer = false, GenerateClient = true });
        Assert.Equal(string.Empty, clientOnly.ServerGraph);
        Assert.False(string.IsNullOrEmpty(clientOnly.ClientGraph));
    }

    [Fact]
    public async Task RejectsMissingSource()
        => await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await new ManagedSvelteCompiler().CompileAsync(
                new SvelteSourceFile("Home", null!),
                new SvelteCompilerOptions()));

    [Fact]
    public async Task RejectsOptionsWithoutAnyOutput()
        => await Assert.ThrowsAsync<ArgumentException>(async () =>
            await CompileAsync("<h1>x</h1>", new SvelteCompilerOptions { GenerateServer = false, GenerateClient = false }));

    private static ValueTask<SvelteCompilationResult> CompileAsync(string source, SvelteCompilerOptions? options = null)
        => new ManagedSvelteCompiler().CompileAsync(
            new SvelteSourceFile("Home", source),
            options ?? new SvelteCompilerOptions());
}
