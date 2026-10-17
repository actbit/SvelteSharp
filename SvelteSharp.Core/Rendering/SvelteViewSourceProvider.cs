using SvelteSharp.Compiler;

namespace SvelteSharp.Rendering;

/// <summary>Loads logical Svelte views.</summary>
public interface ISvelteViewSourceProvider
{
    /// <summary>Loads a view, or returns <see langword="null" /> when it does not exist.</summary>
    ValueTask<SvelteSourceFile?> GetAsync(string viewName, CancellationToken cancellationToken = default);
}

/// <summary>Loads views from an in-memory registry, useful for tests and generated views.</summary>
public sealed class InMemorySvelteViewSourceProvider : ISvelteViewSourceProvider, ISvelteModuleSourceProvider
{
    private readonly Dictionary<string, SvelteSourceFile> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SvelteSourceFile> _modules = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Registers or replaces a source.</summary>
    public void Set(string viewName, string source, string? physicalPath = null)
    {
        var normalized = SvelteViewName.Validate(viewName);
        lock (_gate)
        {
            _sources[normalized] = new SvelteSourceFile(normalized, source, physicalPath);
        }
    }

    /// <summary>Registers a build-local <c>.svelte.ts</c> module.</summary>
    public void SetModule(string moduleName, string source, string? physicalPath = null)
    {
        var normalized = NormalizeModuleName(moduleName);
        lock (_gate)
        {
            _modules[normalized] = new SvelteSourceFile(normalized, source, physicalPath) { IsModule = true };
        }
    }

    /// <inheritdoc />
    public ValueTask<SvelteSourceFile?> GetAsync(string viewName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = SvelteViewName.Validate(viewName);
        lock (_gate)
        {
            return ValueTask.FromResult(_sources.GetValueOrDefault(normalized));
        }
    }

    /// <inheritdoc />
    public ValueTask<SvelteSourceFile?> GetAsync(
        string importerViewName,
        string specifier,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = ResolveModuleName(importerViewName, specifier);
        lock (_gate)
        {
            return ValueTask.FromResult(_modules.GetValueOrDefault(normalized));
        }
    }

    private static string NormalizeModuleName(string moduleName)
    {
        var normalized = moduleName.Replace('\\', '/').Trim('/');
        if (!normalized.EndsWith(".svelte.ts", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only .svelte.ts modules are supported.", nameof(moduleName));
        }

        return CanonicalizeModulePath(normalized);
    }

    private static string ResolveModuleName(string importerViewName, string specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier) || !specifier.StartsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException("Svelte module imports must be relative.", nameof(specifier));
        }

        var importerDirectory = Path.GetDirectoryName(importerViewName.Replace('/', Path.DirectorySeparatorChar))
            ?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        var combined = string.IsNullOrEmpty(importerDirectory)
            ? specifier
            : importerDirectory + "/" + specifier;
        return CanonicalizeModulePath(combined);
    }

    private static string CanonicalizeModulePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "." || part.Length == 0)
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new ArgumentException("Svelte module imports must remain under the view root.", nameof(path));
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return string.Join('/', parts);
    }
}

/// <summary>Loads <c>.svelte</c> files under one configured root.</summary>
public sealed class PhysicalSvelteViewSourceProvider : ISvelteViewSourceProvider, ISvelteModuleSourceProvider
{
    private readonly string _root;

    /// <summary>Creates a provider rooted at <paramref name="root" />.</summary>
    public PhysicalSvelteViewSourceProvider(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("A view root is required.", nameof(root));
        }

        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public async ValueTask<SvelteSourceFile?> GetAsync(
        string viewName,
        CancellationToken cancellationToken = default)
    {
        var normalized = SvelteViewName.Validate(viewName);
        var relativePath = normalized.Replace('/', Path.DirectorySeparatorChar) + ".svelte";
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!IsUnderRoot(path) || !File.Exists(path))
        {
            return null;
        }

        var source = await File.ReadAllTextAsync(path, cancellationToken);
        return new SvelteSourceFile(normalized, source, path);
    }

    /// <inheritdoc />
    public async ValueTask<SvelteSourceFile?> GetAsync(
        string importerViewName,
        string specifier,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(specifier) || !specifier.StartsWith(".", StringComparison.Ordinal))
        {
            return null;
        }

        var importer = SvelteViewName.Validate(importerViewName);
        var importerDirectory = Path.GetDirectoryName(importer.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var relative = Path.GetFullPath(Path.Combine(
            _root,
            importerDirectory,
            specifier.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(relative))
        {
            return null;
        }

        string[] candidates = Path.HasExtension(relative)
            ? [relative]
            : [relative + ".svelte.ts", relative + ".ts", relative + ".js"];
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null || !path.EndsWith(".svelte.ts", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var source = await File.ReadAllTextAsync(path, cancellationToken);
        var logical = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
        return new SvelteSourceFile(logical, source, path) { IsModule = true };
    }

    private bool IsUnderRoot(string path)
    {
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
