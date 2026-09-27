using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.Engine.Jint;
using SvelteSharp.JavaScript;
using SvelteSharp.Models;
using SvelteSharp.Rendering;
using SvelteSharp;
using System.Reflection;

var arguments = ParseArguments(args);
if (arguments.ContainsKey("restore-toolchain"))
{
    try
    {
        var toolchainPath = arguments.GetValueOrDefault("toolchain")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "tools", "svelte");
        await new SvelteToolchainInstaller().InstallAsync(
            toolchainPath,
            versions: GetToolchainVersions(arguments));
        Console.WriteLine($"SvelteSharp: restored the external Svelte toolchain into '{Path.GetFullPath(toolchainPath)}'.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

if (!arguments.TryGetValue("views", out var viewsPath)
    || !arguments.TryGetValue("output", out var outputRoot))
{
    Console.Error.WriteLine("Usage: SvelteSharp.Build --views <path> --output <path> [--compiler Jint] [--lock-hash <hash>] [--toolchain <path>] [--svelte-version <version>] [--esbuild-version <version>] [--typescript-version <version>]");
    return 2;
}

if (!Directory.Exists(viewsPath))
{
    Console.WriteLine($"SvelteSharp: no Views directory at '{viewsPath}'.");
    return 0;
}

var compilerName = arguments.GetValueOrDefault("compiler");
if (!string.IsNullOrWhiteSpace(compilerName)
    && !string.Equals(compilerName, nameof(SvelteJavaScriptEngine.Jint), StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("SvelteSharp.Build currently authenticates the Jint compiler adapter only.");
    return 1;
}

try
{
    var root = Path.GetFullPath(viewsPath);
    var sources = Directory.EnumerateFiles(root, "*.svelte", SearchOption.AllDirectories)
        .Where(path => !path.EndsWith(".svelte.ts", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path =>
        {
            var viewName = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/')[..^".svelte".Length];
            return new SvelteSourceFile(viewName, File.ReadAllText(path), path);
        })
        .ToArray();
    if (sources.Length == 0)
    {
        Console.WriteLine($"SvelteSharp: no .svelte views found under '{viewsPath}'.");
        return 0;
    }

    var moduleProvider = new PhysicalSvelteViewSourceProvider(root);
    var versions = GetToolchainVersions(arguments);
    var toolchain = SvelteToolchain.Resolve(arguments.GetValueOrDefault("toolchain"));
    var compiler = new SvelteCompiler(new JintJsonRuntimeFactory(), moduleProvider, toolchain);
    var bundler = new NativeSvelteGraphBundler(
        moduleProvider,
        new NativeEsbuildOptions
        {
            ExecutablePath = arguments.GetValueOrDefault("esbuild"),
            ToolchainPath = toolchain.RootPath
        });
    var pipeline = new SvelteBuildPipeline(compiler, bundler);
    var modelRegistry = LoadModelRegistry(arguments.GetValueOrDefault("assembly"));
    var request = new SvelteBuildRequest(
        sources,
        new SvelteCompilerOptions
        {
            SvelteVersion = versions.SvelteVersion,
            CompilerVersion = versions.SvelteVersion,
            Engine = SvelteJavaScriptEngine.Jint
        },
        arguments.GetValueOrDefault("lock-hash") ?? "none",
        "msbuild")
    {
        ModelRegistry = modelRegistry
    };
    var output = await pipeline.BuildAsync(request);
    var published = await new AtomicSvelteBuildPublisher().PublishAsync(output, outputRoot);
    Console.WriteLine($"SvelteSharp: compiled {sources.Length} view(s) into '{published}' (buildId={output.Manifest.BuildId}).");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var key = args[index].TrimStart('-');
        if (string.IsNullOrWhiteSpace(key))
        {
            continue;
        }

        if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            result[key] = args[++index];
        }
        else
        {
            result[key] = "true";
        }
    }

    return result;
}

static SvelteModelRegistry? LoadModelRegistry(string? assemblyPath)
{
    if (string.IsNullOrWhiteSpace(assemblyPath))
    {
        return null;
    }

    var fullPath = Path.GetFullPath(assemblyPath);
    if (!File.Exists(fullPath))
    {
        throw new FileNotFoundException(
            $"The application assembly used for SvelteSharp model discovery was not found: '{fullPath}'.",
            fullPath);
    }

    var assembly = Assembly.LoadFrom(fullPath);
    var registry = new SvelteModelRegistry();
    var count = registry.RegisterAttributedModels(assembly);
    Console.WriteLine($"SvelteSharp: discovered {count} attributed model(s) from '{Path.GetFileName(fullPath)}'.");
    return registry;
}

static SvelteToolchainVersions GetToolchainVersions(IReadOnlyDictionary<string, string> arguments)
{
    var defaults = SvelteToolchainVersions.Default;
    var versions = new SvelteToolchainVersions(
        arguments.GetValueOrDefault("svelte-version") ?? defaults.SvelteVersion,
        arguments.GetValueOrDefault("esbuild-version") ?? defaults.EsbuildVersion,
        arguments.GetValueOrDefault("typescript-version") ?? defaults.TypeScriptVersion);
    versions.Validate();
    return versions;
}
