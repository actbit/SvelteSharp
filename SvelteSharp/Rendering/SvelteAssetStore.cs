using System.Collections.Concurrent;

namespace SvelteSharp.Rendering;

/// <summary>A generated browser asset held by the current process.</summary>
public sealed record SvelteStoredAsset(string Content, string ContentType);

/// <summary>Stores only allowlisted generated browser assets.</summary>
public sealed class SvelteAssetStore
{
    private readonly ConcurrentDictionary<(string BuildId, string Path), SvelteStoredAsset> _assets = new();

    /// <summary>Stores a generated public asset.</summary>
    public void Put(string buildId, string relativePath, string content, string contentType)
    {
        var path = Normalize(relativePath);
        _assets[(buildId, path)] = new SvelteStoredAsset(content, contentType);
    }

    /// <summary>Looks up an asset from an exact build ID and relative path.</summary>
    public bool TryGet(string buildId, string relativePath, out SvelteStoredAsset? asset)
        => _assets.TryGetValue((buildId, Normalize(relativePath)), out asset);

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Asset paths must be relative and must not contain traversal segments.", nameof(path));
        }

        return normalized;
    }
}
