using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SvelteSharp.Compiler;

/// <summary>Exact package versions used to create one SvelteSharp toolchain.</summary>
public sealed record SvelteToolchainVersions(
    string SvelteVersion,
    string EsbuildVersion,
    string TypeScriptVersion)
{
    private static readonly Regex ExactSemVer = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The versions used when the consuming project does not override them.</summary>
    public static SvelteToolchainVersions Default { get; } = new("5.57.1", "0.28.1", "5.7.3");

    /// <summary>Validates that every package version is an exact semantic version.</summary>
    public void Validate()
    {
        ValidateVersion(nameof(SvelteVersion), SvelteVersion);
        ValidateVersion(nameof(EsbuildVersion), EsbuildVersion);
        ValidateVersion(nameof(TypeScriptVersion), TypeScriptVersion);
    }

    private static void ValidateVersion(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !ExactSemVer.IsMatch(value))
        {
            throw new ArgumentException(
                $"'{name}' must be an exact semantic version such as '5.57.1'.",
                name);
        }
    }
}

/// <summary>
/// Describes the Svelte toolchain installed by the application setup step.
/// Svelte, TypeScript, and esbuild are deliberately not embedded in SvelteSharp.
/// </summary>
public sealed class SvelteToolchain
{
    /// <summary>The supported Svelte distribution version.</summary>
    public const string SvelteVersion = "5.57.1";

    /// <summary>The supported esbuild distribution version.</summary>
    public const string EsbuildVersion = "0.28.1";

    /// <summary>The supported TypeScript distribution version.</summary>
    public const string TypeScriptVersion = "5.7.3";

    /// <summary>Creates a toolchain rooted at an installed setup directory.</summary>
    public SvelteToolchain(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("A Svelte toolchain directory is required.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);
    }

    /// <summary>Directory populated by the SvelteSharp.Build NuGet setup target.</summary>
    public string RootPath { get; }

    /// <summary>Generated compiler bundle consumed by Jint/Okojo.</summary>
    public string CompilerBundlePath => Path.Combine(RootPath, "generated", "svelte-compiler.js");

    /// <summary>Generated runtime files consumed by the native graph bundler.</summary>
    public string GeneratedPath => Path.Combine(RootPath, "generated");

    /// <summary>Installed native esbuild executable.</summary>
    public string EsbuildPath
    {
        get
        {
            var expected = Path.Combine(RootPath, "node_modules", "@esbuild", "win32-x64", "esbuild.exe");
            if (File.Exists(expected))
            {
                return expected;
            }

            var generatedPath = Path.Combine(GeneratedPath, "esbuild.path");
            if (File.Exists(generatedPath))
            {
                var configured = File.ReadAllText(generatedPath, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    var absolute = Path.IsPathRooted(configured)
                        ? configured
                        : Path.GetFullPath(Path.Combine(RootPath, configured));
                    if (File.Exists(absolute)
                        && (!OperatingSystem.IsWindows() || string.Equals(Path.GetExtension(absolute), ".exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        return absolute;
                    }
                }
            }

            var nodeModules = Path.Combine(RootPath, "node_modules");
            if (Directory.Exists(nodeModules))
            {
                var discovered = Directory.EnumerateFiles(nodeModules, "esbuild.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (discovered is not null)
                {
                    return discovered;
                }
            }

            return expected;
        }
    }

    /// <summary>Creates a toolchain from an explicit path or the setup environment.</summary>
    public static SvelteToolchain Resolve(string? rootPath = null)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, rootPath);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("SVELTESHARP_TOOLCHAIN"));

        foreach (var basePath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            foreach (var ancestor in EnumerateAncestors(basePath))
            {
                AddCandidate(candidates, Path.Combine(ancestor, "tools", "svelte"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var toolchain = new SvelteToolchain(candidate);
            if (toolchain.IsInstalled())
            {
                return toolchain;
            }
        }

        var searched = string.Join(", ", candidates.Where(path => !string.IsNullOrWhiteSpace(path)));
        throw new InvalidOperationException(
            "The Svelte toolchain is not installed. Run 'dotnet build' once with the SvelteSharp.Build package " +
            $"or configure SvelteToolchainPath if it is outside the default locations. Searched: {searched}");
    }

    private static void AddCandidate(ICollection<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            candidates.Add(Path.GetFullPath(candidate));
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

    /// <summary>Returns whether setup generated all runtime inputs required by SvelteSharp.</summary>
    public bool IsInstalled()
        => File.Exists(CompilerBundlePath)
            && File.Exists(EsbuildPath)
            && RequiredRuntimeFiles.All(file => File.Exists(Path.Combine(GeneratedPath, file)));

    /// <summary>Validates the generated files and returns the compiler bundle.</summary>
    public string LoadCompilerBundle()
    {
        EnsureInstalled();
        return File.ReadAllText(CompilerBundlePath, Encoding.UTF8);
    }

    /// <summary>Loads one generated runtime file.</summary>
    public ValueTask<string> LoadRuntimeAsync(string fileName)
    {
        EnsureInstalled();
        if (!RequiredRuntimeFiles.Contains(fileName, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown generated Svelte runtime '{fileName}'.", nameof(fileName));
        }

        return ValueTask.FromResult(File.ReadAllText(Path.Combine(GeneratedPath, fileName), Encoding.UTF8));
    }

    /// <summary>Throws a setup diagnostic if the external toolchain is incomplete.</summary>
    public void EnsureInstalled()
    {
        if (!IsInstalled())
        {
            throw new InvalidOperationException(
                $"The Svelte toolchain at '{RootPath}' is incomplete. Run 'dotnet build' to restore it.");
        }
    }

    /// <summary>Runtime files generated from the installed Svelte package.</summary>
    public static IReadOnlyList<string> RequiredRuntimeFiles { get; } =
    [
        "svelte-server.js",
        "svelte-client.js",
        "svelte-internal-server.js",
        "svelte-internal-client.js",
        "svelte-internal-flags-async.js",
        "svelte-internal-flags-legacy.js"
    ];

    /// <summary>Reads setup metadata without exposing package source to the runtime.</summary>
    public JsonDocument ReadMetadata()
    {
        EnsureInstalled();
        var path = Path.Combine(RootPath, "generated", "toolchain.json");
        return JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Reads the exact package versions used to create this toolchain.</summary>
    public SvelteToolchainVersions ReadVersions()
    {
        using var metadata = ReadMetadata();
        var root = metadata.RootElement;
        var versions = new SvelteToolchainVersions(
            root.GetProperty("svelte").GetString() ?? string.Empty,
            root.GetProperty("esbuild").GetString() ?? string.Empty,
            root.GetProperty("typescript").GetString() ?? string.Empty);
        versions.Validate();
        return versions;
    }
}

/// <summary>Compatibility facade for callers that need the pinned Svelte version.</summary>
public static class SvelteCompilerBundle
{
    /// <summary>The Svelte version installed by setup.</summary>
    public const string Version = SvelteToolchain.SvelteVersion;

    /// <summary>Loads the external compiler bundle.</summary>
    public static string Load(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadCompilerBundle();
}
