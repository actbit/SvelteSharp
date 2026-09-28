using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using SvelteSharp.AspNetCore;
using SvelteSharp.Build;
using SvelteSharp.Compiler;
using SvelteSharp.Models;

namespace SvelteSharp.Rendering;

/// <summary>Default renderer for the managed compiler baseline.</summary>
public sealed class SvelteRenderer : ISvelteRenderer
{
    private readonly SvelteSharpOptions _options;
    private readonly ISvelteViewSourceProvider _sourceProvider;
    private readonly ISvelteCompiler _compiler;
    private readonly SvelteManifestStore _manifestStore;
    private readonly SvelteAssetStore _assetStore;
    private readonly ISvelteGraphBundler _graphBundler;
    private readonly ISvelteSsrRenderer _ssrRenderer;
    private readonly SvelteToolchain _toolchain;
    private readonly ConcurrentDictionary<string, CompiledView> _compiledViews = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private SvelteBuildManifest? _publishedManifest;
    private int _compiledViewsVersion;
    private int _publishedManifestVersion = -1;

    /// <summary>Creates a renderer.</summary>
    public SvelteRenderer(
        SvelteSharpOptions options,
        ISvelteViewSourceProvider sourceProvider,
        ISvelteCompiler compiler,
        SvelteManifestStore manifestStore,
        SvelteAssetStore assetStore,
        ISvelteGraphBundler graphBundler,
        ISvelteSsrRenderer ssrRenderer,
        SvelteToolchain? toolchain = null)
    {
        _options = options;
        _sourceProvider = sourceProvider;
        _compiler = compiler;
        _manifestStore = manifestStore;
        _assetStore = assetStore;
        _graphBundler = graphBundler;
        _ssrRenderer = ssrRenderer;
        _toolchain = toolchain ?? SvelteToolchain.Resolve(options.SvelteToolchainPath);
    }

    /// <inheritdoc />
    public async ValueTask<RenderedSvelteView> RenderAsync(
        SvelteViewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewName = SvelteViewName.Validate(request.ViewName);
        cancellationToken.ThrowIfCancellationRequested();

        var source = await _sourceProvider.GetAsync(viewName, cancellationToken);
        if (source is null)
        {
            throw new SvelteViewException(
                viewName,
                _manifestStore.Current?.BuildId,
                "SVE2002",
                $"The Svelte view '{viewName}' was not found.");
        }

        var compiled = await CompileIfNeededAsync(source, cancellationToken);
        if (!compiled.Result.IsSuccessful)
        {
            throw new SvelteViewException(
                viewName,
                _manifestStore.Current?.BuildId,
                "SVE1000",
                string.Join(" ", compiled.Result.Diagnostics.Where(d => d.Severity == SvelteDiagnosticSeverity.Error).Select(d => d.Message)));
        }

        var manifest = await PublishManifestAsync(cancellationToken);
        var entry = manifest.GetView(viewName);
        if (!entry.Supports(request.Mode))
        {
            throw new SvelteViewException(
                viewName,
                manifest.BuildId,
                "SVE2003",
                $"View '{viewName}' does not support render mode '{request.Mode}'.");
        }

        var modelType = request.Model?.GetType() ?? typeof(object);
        var snapshot = ModelSnapshotSerializer.Serialize(request.Model, modelType, _options.ModelJson);
        var serverRendered = request.Mode is SvelteRenderMode.Server or SvelteRenderMode.Hybrid;
        var browserRendered = request.Mode is SvelteRenderMode.Client or SvelteRenderMode.Hybrid;
        var ssr = serverRendered
            ? await _ssrRenderer.RenderAsync(compiled.Result, snapshot, request.Model, viewName, request.HttpContext, cancellationToken)
            : new SvelteSsrOutput(string.Empty, string.Empty);
        var body = ssr.Body;
        var head = serverRendered ? ssr.Head : RenderMetadata(request.Metadata);
        var modeAttribute = browserRendered
            ? $" data-svelte-mode=\"{(request.Mode == SvelteRenderMode.Hybrid ? "hybrid" : "client")}\""
            : string.Empty;
        var root = $"<div data-svelte-root=\"{Encode(viewName)}\" data-svelte-build=\"{Encode(manifest.BuildId)}\"{modeAttribute}>{body}</div>";
        var snapshotScript = browserRendered
            ? $"<script type=\"application/json\" id=\"svelte-model\">{snapshot}</script>"
            : string.Empty;
        var clientScript = browserRendered
            ? CreateClientScript(request.HttpContext, manifest.BuildId, entry.ClientEntry!)
            : string.Empty;
        var css = entry.CssAsset is null
            ? string.Empty
            : $"<link rel=\"stylesheet\" href=\"{Encode(SvelteAssetUrl.Create(manifest.BuildId, entry.CssAsset))}\" />";

        var document = $"<!doctype html><html><head>{head}{css}</head><body>{root}{snapshotScript}{clientScript}</body></html>";
        return new RenderedSvelteView(document, manifest.BuildId, browserRendered ? entry.ClientEntry : null, _options.CacheControl);
    }

    private async ValueTask<CompiledView> CompileIfNeededAsync(
        SvelteSourceFile source,
        CancellationToken cancellationToken)
    {
        var existing = _compiledViews.GetValueOrDefault(source.ViewName);
        if (existing is not null && existing.SourceHash == ComputeHash(source.Source))
        {
            return existing;
        }

        var compilerOptions = new SvelteCompilerOptions
        {
            SvelteVersion = EffectiveSvelteVersion,
            CompilerVersion = EffectiveCompilerVersion,
            Engine = _options.CompilerEngine,
            GenerateServer = true,
            GenerateClient = true,
            ExtractCss = _options.Compiler.ExtractCss,
            Dev = _options.Compiler.Dev,
            ExperimentalAsync = _options.Compiler.ExperimentalAsync,
            GenerateSourceMap = _options.Compiler.GenerateSourceMap
        };

        var result = await _compiler.CompileAsync(source, compilerOptions, cancellationToken);
        var compiled = new CompiledView(result.SourceHash, result);
        _compiledViews[source.ViewName] = compiled;
        Interlocked.Increment(ref _compiledViewsVersion);
        Volatile.Write(ref _publishedManifestVersion, -1);
        return compiled;
    }

    private async ValueTask<SvelteBuildManifest> PublishManifestAsync(CancellationToken cancellationToken)
    {
        var currentVersion = Volatile.Read(ref _compiledViewsVersion);
        var published = Volatile.Read(ref _publishedManifest);
        if (published is not null && Volatile.Read(ref _publishedManifestVersion) == currentVersion)
        {
            return published;
        }

        await _manifestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            currentVersion = Volatile.Read(ref _compiledViewsVersion);
            published = Volatile.Read(ref _publishedManifest);
            if (published is not null && Volatile.Read(ref _publishedManifestVersion) == currentVersion)
            {
                return published;
            }

            var bundledViews = new List<BundledView>();
            foreach (var view in _compiledViews.Values.OrderBy(view => view.Result.ViewName, StringComparer.Ordinal))
            {
                var server = await _graphBundler.BundleAsync(view.Result, view.Result.ViewName, SvelteGraphKind.Server, cancellationToken);
                var client = await _graphBundler.BundleAsync(view.Result, view.Result.ViewName, SvelteGraphKind.Client, cancellationToken);
                bundledViews.Add(new BundledView(view, server, client));
            }

            var generated = bundledViews.SelectMany(view => new[]
            {
                (Path: view.Result.ViewName + ".server.js", ContentHash: Hash(view.Server.Code)),
                (Path: view.Result.ViewName + ".client.js", ContentHash: Hash(view.Client.Code)),
                (Path: view.Result.ViewName + ".css", ContentHash: Hash(view.Result.Css))
            });
            var buildId = BuildIdCalculator.Calculate(
                SvelteSharpAssembly.ContractVersion,
                EffectiveCompilerVersion,
                _options.DependencyLockHash,
                generated,
                $"compiler={_options.CompilerEngine};ssr={_options.SsrEngine}");

            var entries = new List<SvelteViewManifestEntry>();
            var assets = new List<SveltePublicAsset>();
            foreach (var view in bundledViews)
            {
                var clientPath = $"views/{view.Result.ViewName.Replace('/', '_')}.js";
                var cssPath = string.IsNullOrWhiteSpace(view.Result.Css)
                    ? null
                    : $"views/{view.Result.ViewName.Replace('/', '_')}.css";
                _assetStore.Put(buildId, clientPath, view.Client.Code, "text/javascript; charset=utf-8");
                assets.Add(new SveltePublicAsset(clientPath, "text/javascript", Encoding.UTF8.GetByteCount(view.Client.Code)));
                if (cssPath is not null)
                {
                    _assetStore.Put(buildId, cssPath, view.Result.Css, "text/css; charset=utf-8");
                    assets.Add(new SveltePublicAsset(cssPath, "text/css", Encoding.UTF8.GetByteCount(view.Result.Css)));
                }

                entries.Add(new SvelteViewManifestEntry(
                    view.Result.ViewName,
                    buildId,
                    new HashSet<SvelteRenderMode> { SvelteRenderMode.Server, SvelteRenderMode.Client, SvelteRenderMode.Hybrid },
                    view.Server.Code,
                    clientPath,
                    cssPath,
                    view.Result.SourceHash,
                    EffectiveCompilerVersion));
            }

            var manifest = new SvelteBuildManifest(buildId, entries, assets);
            _manifestStore.Publish(manifest);
            Volatile.Write(ref _publishedManifest, manifest);
            Volatile.Write(ref _publishedManifestVersion, currentVersion);
            return manifest;
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private string CreateClientScript(HttpContext context, string buildId, string clientEntry)
    {
        var nonce = _options.CspNonceFactory?.Invoke(context);
        var nonceAttribute = string.IsNullOrWhiteSpace(nonce) ? string.Empty : $" nonce=\"{Encode(nonce)}\"";
        var url = SvelteAssetUrl.Create(buildId, clientEntry);
        return $"<script type=\"module\" src=\"{Encode(url)}\"{nonceAttribute}></script>";
    }

    private static string RenderMetadata(PageMetadata? metadata)
    {
        if (metadata is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            builder.Append("<title>").Append(Encode(metadata.Title)).Append("</title>");
        }

        if (metadata.Meta is not null)
        {
            foreach (var item in metadata.Meta.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                builder.Append("<meta name=\"").Append(Encode(item.Key)).Append("\" content=\"")
                    .Append(Encode(item.Value)).Append("\" />");
            }
        }

        return builder.ToString();
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private string EffectiveSvelteVersion
        => _options.Compiler.SvelteVersion == "5"
            ? InstalledToolchainVersions.SvelteVersion
            : _options.Compiler.SvelteVersion;

    private string EffectiveCompilerVersion
        => _options.Compiler.CompilerVersion == SvelteCompilerBundle.Version
            ? InstalledToolchainVersions.SvelteVersion
            : _options.Compiler.CompilerVersion;

    private SvelteToolchainVersions InstalledToolchainVersions => _toolchain.ReadVersions();

    private static string ComputeHash(string value) => Hash(value);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CompiledView(string SourceHash, SvelteCompilationResult Result);

    private sealed record BundledView(CompiledView View, SvelteGraphBundle Server, SvelteGraphBundle Client)
    {
        public SvelteCompilationResult Result => View.Result;
    }
}
