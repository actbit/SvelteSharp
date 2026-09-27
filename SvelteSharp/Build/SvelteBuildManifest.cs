using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SvelteSharp.Build;

/// <summary>An asset that is safe to expose through the public SvelteSharp URL space.</summary>
public sealed record SveltePublicAsset(string RelativePath, string ContentType, long Length);

/// <summary>Manifest information for one logical Svelte view.</summary>
public sealed record SvelteViewManifestEntry(
    string ViewName,
    string BuildId,
    IReadOnlySet<SvelteRenderMode> SupportedModes,
    string? ServerGraph,
    string? ClientEntry,
    string? CssAsset,
    string SourceHash,
    string CompilerVersion)
{
    /// <summary>Returns whether the view supports a requested mode.</summary>
    public bool Supports(SvelteRenderMode mode) => SupportedModes.Contains(mode);
}

/// <summary>Immutable manifest used to keep HTML and assets on the same build ID.</summary>
public sealed class SvelteBuildManifest
{
    /// <summary>Creates a validated manifest snapshot.</summary>
    public SvelteBuildManifest(
        string buildId,
        IEnumerable<SvelteViewManifestEntry> views,
        IEnumerable<SveltePublicAsset>? assets = null)
    {
        if (string.IsNullOrWhiteSpace(buildId) || buildId.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A non-empty build ID without whitespace is required.", nameof(buildId));
        }

        BuildId = buildId;
        Views = views.ToFrozenDictionary(
            view => SvelteViewName.Validate(view.ViewName),
            StringComparer.Ordinal);
        Assets = (assets ?? []).ToFrozenDictionary(asset => asset.RelativePath, StringComparer.Ordinal);
        Validate();
    }

    /// <summary>Build ID used in all public asset URLs.</summary>
    public string BuildId { get; }

    /// <summary>View entries in this snapshot.</summary>
    public IReadOnlyDictionary<string, SvelteViewManifestEntry> Views { get; }

    /// <summary>Allowlisted browser assets.</summary>
    public IReadOnlyDictionary<string, SveltePublicAsset> Assets { get; }

    /// <summary>Returns a view or throws a diagnostic exception.</summary>
    public SvelteViewManifestEntry GetView(string viewName)
    {
        var normalized = SvelteViewName.Validate(viewName);
        if (!Views.TryGetValue(normalized, out var view))
        {
            throw new SvelteViewException(
                normalized,
                BuildId,
                "SVE2001",
                $"The Svelte view '{normalized}' is not present in the build manifest.");
        }

        return view;
    }

    /// <summary>Creates the immutable manifest JSON used by diagnostics and tooling.</summary>
    public string ToJson()
        => JsonSerializer.Serialize(new
        {
            buildId = BuildId,
            views = Views,
            assets = Assets
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private void Validate()
    {
        foreach (var view in Views.Values)
        {
            if (!string.Equals(view.BuildId, BuildId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"View '{view.ViewName}' has a different build ID.", nameof(Views));
            }

            if (view.SupportedModes.Count == 0)
            {
                throw new ArgumentException($"View '{view.ViewName}' has no supported rendering modes.", nameof(Views));
            }

            if (view.SupportedModes.Contains(SvelteRenderMode.Server)
                || view.SupportedModes.Contains(SvelteRenderMode.Hybrid))
            {
                if (string.IsNullOrWhiteSpace(view.ServerGraph))
                {
                    throw new ArgumentException($"View '{view.ViewName}' has no server graph.", nameof(Views));
                }
            }

            if (view.SupportedModes.Contains(SvelteRenderMode.Client)
                || view.SupportedModes.Contains(SvelteRenderMode.Hybrid))
            {
                if (string.IsNullOrWhiteSpace(view.ClientEntry))
                {
                    throw new ArgumentException($"View '{view.ViewName}' has no client entry.", nameof(Views));
                }
            }
        }
    }
}

/// <summary>Thread-safe holder for atomically replacing a build snapshot.</summary>
public sealed class SvelteManifestStore
{
    private SvelteBuildManifest? _current;

    /// <summary>Gets the current manifest, if a build has been published.</summary>
    public SvelteBuildManifest? Current => Volatile.Read(ref _current);

    /// <summary>Publishes a fully validated manifest as one atomic operation.</summary>
    public void Publish(SvelteBuildManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Interlocked.Exchange(ref _current, manifest);
    }
}

/// <summary>Calculates stable content-addressed build IDs.</summary>
public static class BuildIdCalculator
{
    /// <summary>Calculates a short stable build ID from all output-affecting inputs.</summary>
    public static string Calculate(
        string svelteSharpVersion,
        string compilerVersion,
        string lockfileHash,
        IEnumerable<(string Path, string ContentHash)> generatedFiles,
        string buildSettings)
    {
        var canonical = new StringBuilder()
            .AppendLine(svelteSharpVersion)
            .AppendLine(compilerVersion)
            .AppendLine(lockfileHash)
            .AppendLine(buildSettings);

        foreach (var file in generatedFiles.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            canonical.Append(file.Path).Append('\0').Append(file.ContentHash).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16].ToLowerInvariant();
    }
}

/// <summary>Produces immutable public asset URLs.</summary>
public static class SvelteAssetUrl
{
    /// <summary>Creates a public URL for an allowlisted asset.</summary>
    public static string Create(string buildId, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(buildId) || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Both a build ID and an asset path are required.");
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Contains("..", StringComparison.Ordinal) || normalized.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("An asset path must be relative and must not contain traversal segments.", nameof(relativePath));
        }

        return $"/_svelte/v{Uri.EscapeDataString(buildId)}/{string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString))}";
    }
}
