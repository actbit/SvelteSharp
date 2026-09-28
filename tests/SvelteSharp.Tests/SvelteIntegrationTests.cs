using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SvelteSharp;
using SvelteSharp.AspNetCore;
using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.Engine.Jint;
using SvelteSharp.Engine.Okojo;
using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class SvelteIntegrationTests
{
    [Fact]
    public async Task JintCompilesBundlesAndRendersAViewWithTheModelSnapshot()
    {
        var toolchain = SvelteToolchain.Resolve(FindToolchainPath());
        var source = new SvelteSourceFile(
            "Products/Detail",
            """
            <script lang="ts">
              import type { ProductPage } from '@sveltesharp/models';
              let { model }: { model: ProductPage } = $props();
            </script>
            <svelte:head><title>{model.title}</title></svelte:head>
            <h1>{model.title}</h1>
            <p>{model.price}</p>
            """);
        var compiler = new SvelteCompiler(new JintJsonRuntimeFactory(), toolchain: toolchain);
        var compilerOptions = new SvelteCompilerOptions
        {
            CompilerVersion = SvelteCompilerBundle.Version,
            Engine = SvelteJavaScriptEngine.Jint
        };

        var compilation = await compiler.CompileAsync(source, compilerOptions);

        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
        Assert.Contains("title", compilation.ServerGraph, StringComparison.OrdinalIgnoreCase);

        var bundler = new NativeSvelteGraphBundler(
            options: new NativeEsbuildOptions { ToolchainPath = toolchain.RootPath });
        var server = await bundler.BundleAsync(compilation, source.ViewName, SvelteGraphKind.Server);
        var client = await bundler.BundleAsync(compilation, source.ViewName, SvelteGraphKind.Client);
        Assert.Contains("SvelteSharpView", server.Code);
        Assert.Contains("boot", client.Code);
        // The public svelte entry point and the compiled component must share one
        // internal/client graph. Two copies break hydration because their runtime
        // state and DOM getter initialization are independent.
        Assert.Equal(1, client.Code.Split("Object.getOwnPropertyDescriptor", StringSplitOptions.None).Length - 1);

        var views = new InMemorySvelteViewSourceProvider();
        views.Set(source.ViewName, source.Source);
        var services = new ServiceCollection();
        var builder = services.AddSvelteSharp(options =>
        {
            options.SvelteToolchainPath = toolchain.RootPath;
            options.AllowRuntimeBuildFallback = true;
        });
        builder.UseJint();
        services.AddSingleton<ISvelteViewSourceProvider>(views);
        services.AddSingleton<ISvelteModuleSourceProvider>(views);
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("example.test");
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await SvelteSharp.Svelte.View(
            source.ViewName,
            new { title = "Phone", price = 1200 },
            SvelteRenderMode.Hybrid).ExecuteAsync(context);

        body.Position = 0;
        using var reader = new StreamReader(body);
        var html = await reader.ReadToEndAsync();
        Assert.Contains("<title>Phone</title>", html);
        Assert.Contains("<h1>Phone</h1>", html);
        Assert.Contains("data-svelte-mode=\"hybrid\"", html);
        Assert.Contains("id=\"svelte-model\"", html);
        Assert.Contains("\"title\":\"Phone\"", html);
        Assert.Contains("type=\"module\"", html);

        var secondContext = new DefaultHttpContext { RequestServices = provider };
        secondContext.Request.Scheme = "https";
        secondContext.Request.Host = new HostString("example.test");
        await using var secondBody = new MemoryStream();
        secondContext.Response.Body = secondBody;

        await SvelteSharp.Svelte.View(
            source.ViewName,
            new { title = "Tablet", price = 800 },
            SvelteRenderMode.Server).ExecuteAsync(secondContext);

        secondBody.Position = 0;
        using var secondReader = new StreamReader(secondBody);
        var secondHtml = await secondReader.ReadToEndAsync();
        Assert.Contains("<title>Tablet</title>", secondHtml);
        Assert.Contains("<h1>Tablet</h1>", secondHtml);
        Assert.DoesNotContain("Phone", secondHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OkojoPooledSsrRendersRepeatedRequestsWithoutLeakingTheModel()
    {
        var toolchain = SvelteToolchain.Resolve(FindToolchainPath());
        var source = new SvelteSourceFile(
            "Products/OkojoDetail",
            """
            <script>
              let { model } = $props();
            </script>
            <h1>{model.title}</h1>
            """);
        var compiler = new SvelteCompiler(new OkojoJsonRuntimeFactory(), toolchain: toolchain);
        var compilation = await compiler.CompileAsync(
            source,
            new SvelteCompilerOptions
            {
                CompilerVersion = SvelteCompilerBundle.Version,
                Engine = SvelteJavaScriptEngine.Okojo
            });

        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
        var bundler = new NativeSvelteGraphBundler(
            options: new NativeEsbuildOptions { ToolchainPath = toolchain.RootPath });
        await using var pool = new OkojoSsrRuntimePool();
        var renderer = new OkojoSvelteSsrRenderer(
            new OkojoJsonRuntimeFactory(),
            bundler,
            pool);

        var first = await renderer.RenderAsync(
            compilation,
            "{\"title\":\"Phone\"}",
            new { title = "Phone" },
            source.ViewName,
            new DefaultHttpContext());
        var second = await renderer.RenderAsync(
            compilation,
            "{\"title\":\"Tablet\"}",
            new { title = "Tablet" },
            source.ViewName,
            new DefaultHttpContext());

        Assert.Contains("<h1>Phone</h1>", first.Body);
        Assert.Contains("<h1>Tablet</h1>", second.Body);
        Assert.DoesNotContain("Phone", second.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPipelinePublishesModelDeclarationsAsAPrivateArtifact()
    {
        var toolchain = SvelteToolchain.Resolve(FindToolchainPath());
        var source = new SvelteSourceFile("Index", "<h1>hello</h1>");
        var compiler = new SvelteCompiler(new JintJsonRuntimeFactory(), toolchain: toolchain);
        var bundler = new NativeSvelteGraphBundler(
            options: new NativeEsbuildOptions { ToolchainPath = toolchain.RootPath });
        var registry = new SvelteModelRegistry();
        registry.RegisterAttributedModels(typeof(SvelteIntegrationTests).Assembly);

        var output = await new SvelteBuildPipeline(compiler, bundler).BuildAsync(
            new SvelteBuildRequest(
                [source],
                new SvelteCompilerOptions
                {
                    CompilerVersion = SvelteCompilerBundle.Version,
                    Engine = SvelteJavaScriptEngine.Jint
                })
            {
                ModelRegistry = registry
            });

        var modelArtifact = Assert.Single(output.Artifacts, artifact => artifact.RelativePath == "models/index.d.ts");
        Assert.False(modelArtifact.IsPublic);
        Assert.Contains("export interface BindingModel", modelArtifact.Content);
    }

    [Fact]
    public void AssemblyDoesNotEmbedSvelteAssets()
    {
        var resources = typeof(SvelteCompiler).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain(resources, resource => resource.Contains("svelte", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindToolchainPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "svelte");
            if (File.Exists(Path.Combine(candidate, "generated", "svelte-compiler.js")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The setup-installed Svelte toolchain could not be found.");
    }

    [SvelteModel]
    private sealed class BindingModel
    {
        public string Value { get; init; } = string.Empty;
    }
}
