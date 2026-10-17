using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SvelteSharp.Compiler;
using SvelteSharp.Rendering;

namespace SvelteSharp.Build;

/// <summary>One build-time Svelte artifact loaded by the in-process runtime.</summary>
public sealed record SveltePrebuiltView(
    string ViewName,
    string BuildId,
    string? ServerGraph,
    string? ClientGraph,
    string Css,
    string SourceHash,
    string CompilerVersion);

/// <summary>
/// Loads the immutable output produced by <see cref="AtomicSvelteBuildPublisher"/>.
/// </summary>
public sealed class SveltePrebuiltArtifactStore
{
    private readonly IReadOnlyDictionary<string, SveltePrebuiltView> _views;

    /// <summary>Creates a store from a build output root.</summary>
    public SveltePrebuiltArtifactStore(string? outputRoot)
    {
        RootPath = string.IsNullOrWhiteSpace(outputRoot)
            ? ResolveDefaultRoot()
            : Path.GetFullPath(outputRoot);
        _views = LoadLatest(RootPath);
    }

    /// <summary>Root containing versioned SvelteSharp build directories.</summary>
    public string? RootPath { get; }

    /// <summary>Whether a complete prebuilt manifest was found.</summary>
    public bool IsAvailable => _views.Count > 0;

    /// <summary>Gets the prebuilt view for one logical view name.</summary>
    public bool TryGet(string viewName, out SveltePrebuiltView? view)
        => _views.TryGetValue(SvelteViewName.Validate(viewName), out view);

    /// <summary>
    /// Finds the build output beside the application or in an ancestor project's
    /// output tree. The MSBuild target uses <c>&lt;TargetDir&gt;SvelteSharp</c> by default.
    /// </summary>
    public static string ResolveDefaultRoot()
    {
        foreach (var basePath in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            foreach (var ancestor in EnumerateAncestors(basePath))
            {
                var candidate = Path.Combine(ancestor, "SvelteSharp");
                if (ContainsManifest(candidate))
                {
                    return candidate;
                }
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "SvelteSharp");
    }

    private static bool ContainsManifest(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return false;
        }

        try
        {
            // A repository root may also contain unrelated sample outputs. The
            // build target writes each immutable artifact under one immediate
            // version directory, so only accept that shape during discovery.
            return Directory.EnumerateDirectories(rootPath)
                .Any(directory => File.Exists(Path.Combine(directory, "manifest.json")));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }

    private static IReadOnlyDictionary<string, SveltePrebuiltView> LoadLatest(string? rootPath)
    {
        if (rootPath is null || !Directory.Exists(rootPath))
        {
            return new Dictionary<string, SveltePrebuiltView>(StringComparer.Ordinal);
        }

        var manifests = Directory.EnumerateFiles(rootPath, "manifest.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        foreach (var manifestPath in manifests)
        {
            var loaded = TryLoadManifest(manifestPath);
            if (loaded.Count > 0)
            {
                return loaded;
            }
        }

        return new Dictionary<string, SveltePrebuiltView>(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, SveltePrebuiltView> TryLoadManifest(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
            var root = document.RootElement;
            var buildId = root.GetProperty("buildId").GetString()
                ?? throw new SvelteBuildException("SVE3301", $"Manifest '{manifestPath}' has no build ID.");
            var directory = Path.GetDirectoryName(manifestPath)
                ?? throw new SvelteBuildException("SVE3302", $"Manifest '{manifestPath}' has no parent directory.");
            var views = new Dictionary<string, SveltePrebuiltView>(StringComparer.Ordinal);
            foreach (var property in root.GetProperty("views").EnumerateObject())
            {
                var element = property.Value;
                var viewName = SvelteViewName.Validate(
                    GetNullableString(element, "viewName") ?? property.Name);
                var serverGraph = GetNullableString(element, "serverGraph");
                var clientEntry = GetNullableString(element, "clientEntry");
                var cssAsset = GetNullableString(element, "cssAsset");
                var clientGraph = clientEntry is null ? null : ReadRelativeFile(directory, clientEntry);
                var css = cssAsset is null ? string.Empty : ReadRelativeFile(directory, cssAsset);
                views[viewName] = new SveltePrebuiltView(
                    viewName,
                    buildId,
                    serverGraph,
                    clientGraph,
                    css,
                    element.GetProperty("sourceHash").GetString() ?? string.Empty,
                    element.GetProperty("compilerVersion").GetString() ?? string.Empty);
            }

            return views;
        }
        catch (SvelteBuildException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or KeyNotFoundException)
        {
            throw new SvelteBuildException(
                "SVE3303",
                $"The prebuilt SvelteSharp manifest '{manifestPath}' could not be loaded: {exception.Message}");
        }
    }

    private static string? GetNullableString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private static string ReadRelativeFile(string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new SvelteBuildException("SVE3304", $"The prebuilt artifact path '{relativePath}' is unsafe.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new SvelteBuildException("SVE3305", $"The prebuilt artifact path '{relativePath}' escapes its build directory.");
        }

        if (!File.Exists(fullPath))
        {
            throw new SvelteBuildException("SVE3306", $"The prebuilt artifact '{fullPath}' was not found.");
        }

        return File.ReadAllText(fullPath, Encoding.UTF8);
    }
}

/// <summary>Uses build output as the compiler result without executing a compiler at request time.</summary>
public sealed class PrebuiltSvelteCompiler : ISvelteCompiler
{
    private readonly SveltePrebuiltArtifactStore _store;

    /// <summary>Creates a prebuilt compiler.</summary>
    public PrebuiltSvelteCompiler(SveltePrebuiltArtifactStore store) => _store = store;

    /// <inheritdoc />
    public ValueTask<SvelteCompilationResult> CompileAsync(
        SvelteSourceFile source,
        SvelteCompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        source.Validate();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryGet(source.ViewName, out var view) || view is null)
        {
            throw new SvelteBuildException(
                "SVE3307",
                $"No prebuilt artifact exists for '{source.ViewName}'. Run 'dotnet build' before starting the application.");
        }

        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Source))).ToLowerInvariant();
        if (!string.Equals(sourceHash, view.SourceHash, StringComparison.Ordinal))
        {
            throw new SvelteBuildException(
                "SVE3308",
                $"The prebuilt artifact for '{source.ViewName}' is stale. Run 'dotnet build' before starting the application.");
        }

        var result = new SvelteCompilationResult(
            source.ViewName,
            new SvelteViewTemplate(string.Empty, string.Empty, view.Css),
            view.ServerGraph ?? string.Empty,
            view.ClientGraph ?? string.Empty,
            view.Css,
            [],
            sourceHash)
        {
            CompilerVersion = view.CompilerVersion,
            RequiresAsyncRender = (view.ServerGraph ?? string.Empty).Contains("async", StringComparison.Ordinal)
        };
        return ValueTask.FromResult(result);
    }
}

/// <summary>Returns prebuilt graphs without starting esbuild during a request.</summary>
public sealed class PrebuiltSvelteGraphBundler : ISvelteGraphBundler
{
    private readonly SveltePrebuiltArtifactStore _store;

    /// <summary>Creates a prebuilt graph bundler.</summary>
    public PrebuiltSvelteGraphBundler(SveltePrebuiltArtifactStore store) => _store = store;

    /// <inheritdoc />
    public ValueTask<SvelteGraphBundle> BundleAsync(
        SvelteCompilationResult compilation,
        string viewName,
        SvelteGraphKind kind,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_store.TryGet(viewName, out var view) || view is null)
        {
            throw new SvelteBuildException("SVE3309", $"No prebuilt graph exists for '{viewName}'.");
        }

        var code = kind == SvelteGraphKind.Server ? view.ServerGraph : view.ClientGraph;
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new SvelteBuildException("SVE3310", $"The prebuilt {kind} graph for '{viewName}' is empty.");
        }

        return ValueTask.FromResult(new SvelteGraphBundle(code));
    }
}
