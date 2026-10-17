using System.Security.Cryptography;
using System.Text;
using SvelteSharp.Compiler;
using SvelteSharp.Rendering;

namespace SvelteSharp.Build;

/// <summary>Inputs that affect a reproducible SvelteSharp build.</summary>
public sealed record SvelteBuildRequest(
    IReadOnlyList<SvelteSourceFile> Views,
    SvelteCompilerOptions Compiler,
    string DependencyLockHash = "none",
    string BuildSettings = "default",
    string SvelteSharpVersion = SvelteSharpAssembly.ContractVersion)
{
    /// <summary>Explicit CLR models whose declarations participate in this build.</summary>
    public SvelteSharp.Models.SvelteModelRegistry? ModelRegistry { get; init; }
}

/// <summary>A generated artifact, classified before it is written to disk.</summary>
public sealed record SvelteBuildArtifact(
    string RelativePath,
    string Content,
    string ContentType,
    bool IsPublic);

/// <summary>Complete output of a deterministic build.</summary>
public sealed record SvelteBuildOutput(
    SvelteBuildManifest Manifest,
    IReadOnlyList<SvelteBuildArtifact> Artifacts);

/// <summary>Builds all views without invoking a JavaScript package manager.</summary>
public sealed class SvelteBuildPipeline
{
    private readonly ISvelteCompiler _compiler;
    private readonly ISvelteGraphBundler? _bundler;

    /// <summary>Creates a build pipeline.</summary>
    public SvelteBuildPipeline(ISvelteCompiler compiler, ISvelteGraphBundler? bundler = null)
    {
        _compiler = compiler;
        _bundler = bundler;
    }

    /// <summary>Compiles and validates a complete view set.</summary>
    public async ValueTask<SvelteBuildOutput> BuildAsync(
        SvelteBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Compiler.Validate();
        if (request.Views.Count == 0)
        {
            throw new SvelteBuildException("SVE3001", "At least one Svelte view is required.");
        }

        var compiled = new List<(SvelteCompilationResult Result, SvelteGraphBundle? Server, SvelteGraphBundle? Client)>();
        foreach (var source in request.Views.OrderBy(view => view.ViewName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _compiler.CompileAsync(source, request.Compiler, cancellationToken);
            var errors = result.Diagnostics.Where(diagnostic => diagnostic.Severity == SvelteDiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                throw new SvelteBuildException(
                    "SVE3002",
                    $"View '{source.ViewName}' produced compiler errors.",
                    errors);
            }

            var server = request.Compiler.GenerateServer && _bundler is not null
                ? await _bundler.BundleAsync(result, source.ViewName, SvelteGraphKind.Server, cancellationToken)
                : null;
            var client = request.Compiler.GenerateClient && _bundler is not null
                ? await _bundler.BundleAsync(result, source.ViewName, SvelteGraphKind.Client, cancellationToken)
                : null;
            compiled.Add((result, server, client));
        }

        var declarations = request.ModelRegistry?.GenerateDeclarations() ?? new Dictionary<Type, string>();
        var modelDeclaration = string.Join(
            Environment.NewLine + Environment.NewLine,
            declarations.OrderBy(item => item.Key.FullName, StringComparer.Ordinal).Select(item => item.Value));
        var generatedFiles = compiled.SelectMany(item => new[]
        {
            (Path: $"{item.Result.ViewName}.server.js", ContentHash: Hash(item.Server?.Code ?? item.Result.ServerGraph)),
            (Path: $"{item.Result.ViewName}.client.js", ContentHash: Hash(item.Client?.Code ?? item.Result.ClientGraph)),
            (Path: $"{item.Result.ViewName}.css", ContentHash: Hash(item.Result.Css))
        }).Append((Path: "models/index.d.ts", ContentHash: Hash(modelDeclaration)));
        var buildId = BuildIdCalculator.Calculate(
            request.SvelteSharpVersion,
            request.Compiler.CompilerVersion,
            request.DependencyLockHash,
            generatedFiles,
            request.BuildSettings);

        var entries = new List<SvelteViewManifestEntry>();
        var assets = new List<SveltePublicAsset>();
        var artifacts = new List<SvelteBuildArtifact>();
        if (modelDeclaration.Length > 0)
        {
            artifacts.Add(new SvelteBuildArtifact(
                "models/index.d.ts",
                modelDeclaration,
                "text/typescript",
                false));
        }
        foreach (var item in compiled)
        {
            var result = item.Result;
            var serverGraph = item.Server?.Code ?? result.ServerGraph;
            var clientGraph = item.Client?.Code ?? result.ClientGraph;
            var clientPath = $"views/{result.ViewName.Replace('/', '_')}.js";
            var cssPath = string.IsNullOrWhiteSpace(result.Css)
                ? null
                : $"views/{result.ViewName.Replace('/', '_')}.css";
            var modes = BuildModes(request.Compiler);
            entries.Add(new SvelteViewManifestEntry(
                result.ViewName,
                buildId,
                modes,
                request.Compiler.GenerateServer ? serverGraph : null,
                request.Compiler.GenerateClient ? clientPath : null,
                cssPath,
                result.SourceHash,
                request.Compiler.CompilerVersion));

            if (request.Compiler.GenerateServer)
            {
                artifacts.Add(new SvelteBuildArtifact(
                    $"server/{result.ViewName.Replace('/', '_')}.js",
                    serverGraph,
                    "application/javascript",
                    false));
            }

            if (request.Compiler.GenerateClient)
            {
                artifacts.Add(new SvelteBuildArtifact(clientPath, clientGraph, "text/javascript", true));
                assets.Add(new SveltePublicAsset(clientPath, "text/javascript", Encoding.UTF8.GetByteCount(clientGraph)));
            }

            if (cssPath is not null)
            {
                artifacts.Add(new SvelteBuildArtifact(cssPath, result.Css, "text/css", true));
                assets.Add(new SveltePublicAsset(cssPath, "text/css", Encoding.UTF8.GetByteCount(result.Css)));
            }
        }

        return new SvelteBuildOutput(new SvelteBuildManifest(buildId, entries, assets), artifacts);
    }

    private static IReadOnlySet<SvelteRenderMode> BuildModes(SvelteCompilerOptions options)
    {
        var modes = new HashSet<SvelteRenderMode>();
        if (options.GenerateServer) modes.Add(SvelteRenderMode.Server);
        if (options.GenerateClient) modes.Add(SvelteRenderMode.Client);
        if (options.GenerateServer && options.GenerateClient) modes.Add(SvelteRenderMode.Hybrid);
        return modes;
    }

    private static string Hash(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>Raised when a build cannot produce a valid manifest.</summary>
public sealed class SvelteBuildException : Exception
{
    /// <summary>Creates a build exception.</summary>
    public SvelteBuildException(
        string code,
        string message,
        IReadOnlyList<SvelteCompilerDiagnostic>? diagnostics = null)
        : base(message)
    {
        Code = code;
        Diagnostics = diagnostics ?? [];
    }

    /// <summary>Stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Compiler diagnostics associated with this failure.</summary>
    public IReadOnlyList<SvelteCompilerDiagnostic> Diagnostics { get; }
}

/// <summary>Publishes a build into a new immutable directory without replacing old assets.</summary>
public sealed class AtomicSvelteBuildPublisher
{
    /// <summary>Publishes the build and returns its final directory.</summary>
    public async ValueTask<string> PublishAsync(
        SvelteBuildOutput output,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("An output root is required.", nameof(outputRoot));
        }

        var root = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(root);
        var finalDirectory = Path.Combine(root, $"v{output.Manifest.BuildId}");
        if (Directory.Exists(finalDirectory))
        {
            if (IsComplete(finalDirectory, output))
            {
                return finalDirectory;
            }

            Directory.Delete(finalDirectory, recursive: true);
        }

        var temporaryDirectory = Path.Combine(root, $".tmp-{output.Manifest.BuildId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var artifact in output.Artifacts)
            {
                var relative = NormalizeRelativePath(artifact.RelativePath);
                var path = Path.Combine(temporaryDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, artifact.Content, Encoding.UTF8, cancellationToken);
            }

            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, "manifest.json"),
                output.Manifest.ToJson(),
                Encoding.UTF8,
                cancellationToken);
            Directory.Move(temporaryDirectory, finalDirectory);
            return finalDirectory;
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }

            throw;
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new SvelteBuildException("SVE3003", $"Unsafe artifact path '{path}'.");
        }

        return normalized;
    }

    private static bool IsComplete(string finalDirectory, SvelteBuildOutput output)
    {
        if (!File.Exists(Path.Combine(finalDirectory, "manifest.json")))
        {
            return false;
        }

        return output.Artifacts.All(artifact =>
        {
            var relative = NormalizeRelativePath(artifact.RelativePath);
            return File.Exists(Path.Combine(finalDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        });
    }
}
