using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using JsxCore;
using JsxCore.Compilation;
using JsxCore.Compilation.Assets;
using JsxCore.Compilation.Modules;
using JsxCore.Compilation.Provisioning;
using JsxCore.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.Engine.Jint;
using SvelteSharp.Engine.Okojo;
using SvelteSharp.JavaScript;
using SvelteSharp.Models;
using SvelteSharp.Rendering;

namespace SvelteSharp.Benchmarks;

/// <summary>
/// Compares steady-state, server-side rendering of equivalent precompiled views.
/// Build, compiler startup, esbuild, file discovery and toolchain restoration are intentionally
/// outside the measured methods.
/// </summary>
[MemoryDiagnoser]
[MarkdownExporter]
public class SsrRenderingBenchmarks
{
    private const string ViewName = "Benchmark";

    private readonly BenchmarkModel _model = new(
        "Performance comparison",
        Enumerable.Range(1, 20).Select(index => $"Feature {index}").ToArray());
    private readonly JsonSerializerOptions _modelJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Default
    };
    private readonly IReadOnlyDictionary<string, object?> _context =
        new Dictionary<string, object?> { ["path"] = "/benchmark" };

    private JintSvelteSsrRenderer _svelteRenderer = null!;
    private JintSvelteSsrRenderer _isolatedSvelteRenderer = null!;
    private OkojoSvelteSsrRenderer _okojoRenderer = null!;
    private JintSsrRuntimePool _jintPool = null!;
    private OkojoSsrRuntimePool _okojoPool = null!;
    private SvelteCompilationResult _svelteCompilation = null!;
    private JsxServerRenderer _jsxRenderer = null!;
    private LocatedView _jsxView = null!;
    private IServiceProvider _services = null!;
    private HttpContext _svelteContext = null!;
    private string _lastOutput = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var projectRoot = FindProjectRoot();

        var svelteRoot = Path.Combine(AppContext.BaseDirectory, "SvelteSharp");
        var svelteStore = new SveltePrebuiltArtifactStore(svelteRoot);
        if (!svelteStore.TryGet(ViewName, out var svelteView) || svelteView is null)
        {
            throw new InvalidOperationException(
                $"SvelteSharp prebuilt output for '{ViewName}' was not found under '{svelteRoot}'. " +
                "Build the benchmark project before running it.");
        }

        var svelteSourcePath = Path.Combine(projectRoot, "Views", ViewName + ".svelte");
        var svelteSource = new SvelteSourceFile(ViewName, await File.ReadAllTextAsync(svelteSourcePath), svelteSourcePath);
        var svelteCompiler = new PrebuiltSvelteCompiler(svelteStore);
        _svelteCompilation = await svelteCompiler.CompileAsync(
            svelteSource,
            new SvelteCompilerOptions
            {
                CompilerVersion = svelteView.CompilerVersion,
                Engine = SvelteJavaScriptEngine.Jint,
                GenerateServer = true,
                GenerateClient = false
            });
        _jintPool = new JintSsrRuntimePool();
        _svelteRenderer = new JintSvelteSsrRenderer(
            new JintJsonRuntimeFactory(),
            new PrebuiltSvelteGraphBundler(svelteStore),
            _jintPool);
        _isolatedSvelteRenderer = new JintSvelteSsrRenderer(
            new JintJsonRuntimeFactory(),
            new PrebuiltSvelteGraphBundler(svelteStore));
        _okojoPool = new OkojoSsrRuntimePool();
        _okojoRenderer = new OkojoSvelteSsrRenderer(
            new OkojoJsonRuntimeFactory(),
            new PrebuiltSvelteGraphBundler(svelteStore),
            _okojoPool);

        var jsxOptions = new JsxCoreOptions
        {
            ViewsDirectory = "Views",
            WorkingDirectory = Path.Combine("obj", "JsxCore"),
            PrecompiledOnly = true,
            CompileOnStartup = false,
            WatchForChanges = false,
            HotReload = false,
            AutoInstallDependencies = DependencyInstallMode.Never,
            TypeChecking = TypeCheckingMode.Off,
            DefaultRenderMode = RenderMode.Server
        };
        var jsxLayout = CompilationLayout.Create(jsxOptions, projectRoot);
        var jsxNodeModules = NodeModulesLayout.For(projectRoot, jsxOptions.AdditionalToolchainSearchPaths);
        var jsxPreact = new PreactVendorStager(
            jsxLayout,
            jsxNodeModules,
            NullLogger<PreactVendorStager>.Instance);
        jsxPreact.Stage();

        var jsxCompilation = new JsxCompilationService(
            jsxOptions,
            jsxLayout,
            toolchain: null,
            NullLogger<JsxCompilationService>.Instance,
            jsxPreact);
        await jsxCompilation.InitialiseAsync();
        _jsxRenderer = new JsxServerRenderer(
            jsxOptions,
            jsxCompilation,
            JsxRuntimeLayout.Preact(jsxPreact, jsxOptions.EnableReactCompatibility));
        _jsxView = new LocatedView(
            ViewName,
            Path.Combine(projectRoot, "Views", ViewName + ".tsx"),
            ViewName);
        _services = new ServiceCollection().BuildServiceProvider();
        _svelteContext = new DefaultHttpContext { RequestServices = _services };

        // Warm each engine once so the reported values describe steady-state rendering rather than
        // module parsing and first-use initialization.
        var svelteOutput = await RenderSvelteAsync(_svelteRenderer, _svelteContext);
        var isolatedSvelteOutput = await RenderSvelteAsync(_isolatedSvelteRenderer, _svelteContext);
        var okojoOutput = await RenderSvelteAsync(_okojoRenderer, _svelteContext);
        var jsxOutput = (await _jsxRenderer.RenderAsync(_jsxView, _model, _context, _services)).Html;
        ValidateOutput(svelteOutput, "SvelteSharp");
        ValidateOutput(isolatedSvelteOutput, "SvelteSharp isolated");
        ValidateOutput(okojoOutput, "SvelteSharp Okojo");
        ValidateOutput(jsxOutput, "JsxCore");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_jintPool is not null)
        {
            await _jintPool.DisposeAsync();
        }

        if (_okojoPool is not null)
        {
            await _okojoPool.DisposeAsync();
        }
    }

    [Benchmark(Baseline = true, Description = "SvelteSharp Jint SSR (pooled)")]
    public async Task<string> SvelteSharpJintSsr()
    {
        _lastOutput = await RenderSvelteAsync(_svelteRenderer, _svelteContext);
        return _lastOutput;
    }

    [Benchmark(Description = "SvelteSharp Jint SSR (isolated)")]
    public async Task<string> SvelteSharpJintIsolatedSsr()
    {
        _lastOutput = await RenderSvelteAsync(_isolatedSvelteRenderer, _svelteContext);
        return _lastOutput;
    }

    [Benchmark(Description = "SvelteSharp Okojo SSR (pooled)")]
    public async Task<string> SvelteSharpOkojoSsr()
    {
        _lastOutput = await RenderSvelteAsync(_okojoRenderer, _svelteContext);
        return _lastOutput;
    }

    [Benchmark(Description = "JsxCore Preact SSR")]
    public async Task<string> JsxCorePreactSsr()
    {
        var result = await _jsxRenderer.RenderAsync(_jsxView, _model, _context, _services);
        _lastOutput = result.Html;
        return _lastOutput;
    }

    private async Task<string> RenderSvelteAsync(ISvelteSsrRenderer renderer, HttpContext context)
    {
        // SvelteSharp's SSR adapter accepts the shared snapshot because the same JSON is also
        // embedded for hydration. Serialize inside the measured method so the comparison includes
        // the equivalent model crossing performed by JsxCore.RenderAsync.
        var snapshot = ModelSnapshotSerializer.Serialize(_model, typeof(BenchmarkModel), _modelJson);
        var result = await renderer.RenderAsync(
            _svelteCompilation,
            snapshot,
            _model,
            ViewName,
            context);
        return result.Body;
    }

    private static void ValidateOutput(string html, string framework)
    {
        if (!html.Contains("Performance comparison", StringComparison.Ordinal)
            || html.Split("<li>", StringSplitOptions.None).Length - 1 != 20)
        {
            throw new InvalidOperationException($"{framework} benchmark view did not render the expected markup.");
        }
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Views", ViewName + ".svelte"))
                && File.Exists(Path.Combine(directory.FullName, "Views", ViewName + ".tsx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The benchmark project root could not be located from the benchmark output directory.");
    }

    public sealed record BenchmarkModel(string Title, IReadOnlyList<string> Items);
}
