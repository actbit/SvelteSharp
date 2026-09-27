namespace SvelteSharp;

/// <summary>Metadata used when a view is rendered without server-side head output.</summary>
public sealed record PageMetadata(
    string? Title = null,
    IReadOnlyDictionary<string, string>? Meta = null);
