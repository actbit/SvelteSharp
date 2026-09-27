using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SvelteSharp.Compiler;
using SvelteSharp.Rendering;

namespace SvelteSharp.Build;

/// <summary>Options for the native esbuild process.</summary>
public sealed record NativeEsbuildOptions
{
    /// <summary>Optional absolute path to the setup-installed esbuild executable.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Optional setup-installed Svelte toolchain root.</summary>
    public string? ToolchainPath { get; init; }

    /// <summary>Whether output should be minified.</summary>
    public bool Minify { get; init; } = true;
}

/// <summary>Bundles Svelte graphs using the native esbuild executable, never its Node wrapper.</summary>
public sealed class NativeSvelteGraphBundler : ISvelteGraphBundler
{
    private readonly NativeEsbuildOptions _options;
    private readonly ISvelteModuleSourceProvider? _moduleSourceProvider;

    /// <summary>Creates a native graph bundler.</summary>
    public NativeSvelteGraphBundler(
        ISvelteModuleSourceProvider? moduleSourceProvider = null,
        NativeEsbuildOptions? options = null)
    {
        _moduleSourceProvider = moduleSourceProvider;
        _options = options ?? new NativeEsbuildOptions();
    }

    /// <inheritdoc />
    public async ValueTask<SvelteGraphBundle> BundleAsync(
        SvelteCompilationResult compilation,
        string viewName,
        SvelteGraphKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var graph = kind == SvelteGraphKind.Server ? compilation.ServerGraph : compilation.ClientGraph;
        if (string.IsNullOrWhiteSpace(graph))
        {
            throw new SvelteBuildException("SVE3101", $"The {kind} graph for '{viewName}' is empty.");
        }

        var toolchain = SvelteToolchain.Resolve(_options.ToolchainPath);
        var workDirectory = Path.Combine(Path.GetTempPath(), "SvelteSharp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "component.js"), graph, Encoding.UTF8, cancellationToken);
            foreach (var module in compilation.ModuleGraphs)
            {
                var modulePath = NormalizeModulePath(module.Key);
                var fullPath = Path.Combine(workDirectory, modulePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, module.Value, Encoding.UTF8, cancellationToken);
            }
            await WriteRuntimeAsync(workDirectory, "runtime-server.js", () => toolchain.LoadRuntimeAsync("svelte-server.js"), cancellationToken);
            await WriteRuntimeAsync(workDirectory, "runtime-client.js", () => toolchain.LoadRuntimeAsync("svelte-client.js"), cancellationToken);
            await WriteRuntimeAsync(workDirectory, "internal-server.js", () => toolchain.LoadRuntimeAsync("svelte-internal-server.js"), cancellationToken);
            await WriteRuntimeAsync(workDirectory, "internal-client.js", () => toolchain.LoadRuntimeAsync("svelte-internal-client.js"), cancellationToken);
            await WriteRuntimeAsync(workDirectory, "flags-async.js", () => toolchain.LoadRuntimeAsync("svelte-internal-flags-async.js"), cancellationToken);
            await WriteRuntimeAsync(workDirectory, "flags-legacy.js", () => toolchain.LoadRuntimeAsync("svelte-internal-flags-legacy.js"), cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(workDirectory, "disclose-version.js"),
                "export {};",
                Encoding.UTF8,
                cancellationToken);

            var entry = kind == SvelteGraphKind.Server
                ? CreateServerEntry(viewName)
                : CreateClientEntry(viewName);
            await File.WriteAllTextAsync(Path.Combine(workDirectory, "entry.js"), entry, Encoding.UTF8, cancellationToken);

            var output = Path.Combine(workDirectory, "bundle.js");
            var executable = ResolveExecutable(_options.ExecutablePath, toolchain);
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = workDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            AddArguments(process.StartInfo, kind, workDirectory, toolchain);
            if (!process.Start())
            {
                throw new SvelteBuildException("SVE3102", "The native esbuild process could not be started.");
            }

            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await standardError;
            _ = await standardOutput;
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                throw new SvelteBuildException(
                    "SVE3103",
                    $"Native esbuild failed for '{viewName}' ({kind}): {error.Trim()}");
            }

            var bundled = await File.ReadAllTextAsync(output, cancellationToken);
            return new SvelteGraphBundle(bundled);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }
        }
    }

    private static async ValueTask WriteRuntimeAsync(
        string directory,
        string fileName,
        Func<ValueTask<string>> loader,
        CancellationToken cancellationToken)
        => await File.WriteAllTextAsync(
            Path.Combine(directory, fileName),
            await loader(),
            Encoding.UTF8,
            cancellationToken);

    private static string CreateServerEntry(string viewName)
        => $$"""
            import { render } from './runtime-server.js';
            import Component from './component.js';
            export function renderSnapshot(snapshot) {
              return render(Component, { props: { model: JSON.parse(snapshot) } });
            }
            """;

    private static string CreateClientEntry(string viewName)
    {
        var viewLiteral = JsonSerializer.Serialize(viewName);
        return $$"""
            import { mount, hydrate } from 'svelte';
            import Component from './component.js';
            export function boot(target, snapshot, shouldHydrate) {
              const props = { model: JSON.parse(snapshot) };
              return shouldHydrate ? hydrate(Component, { target, props }) : mount(Component, { target, props });
            }
            const root = document.querySelector('[data-svelte-root="' + {{viewLiteral}} + '"]');
            const data = document.getElementById('svelte-model');
            if (root && data) boot(root, data.textContent || 'null', root.dataset.svelteMode === 'hybrid');
            """;
    }

    private void AddArguments(
        ProcessStartInfo startInfo,
        SvelteGraphKind kind,
        string workDirectory,
        SvelteToolchain toolchain)
    {
        startInfo.ArgumentList.Add("entry.js");
        startInfo.ArgumentList.Add("--bundle");
        startInfo.ArgumentList.Add("--outfile=bundle.js");
        startInfo.ArgumentList.Add(kind == SvelteGraphKind.Server ? "--format=iife" : "--format=esm");
        startInfo.ArgumentList.Add(kind == SvelteGraphKind.Server ? "--global-name=SvelteSharpView" : "--platform=browser");
        var svelteSource = Path.Combine(toolchain.RootPath, "node_modules", "svelte", "src");
        if (kind == SvelteGraphKind.Server)
        {
            startInfo.ArgumentList.Add($"--alias:svelte={Path.Combine(svelteSource, "index-server.js")}");
            startInfo.ArgumentList.Add($"--alias:svelte/server={Path.Combine(svelteSource, "server", "index.js")}");
            startInfo.ArgumentList.Add($"--alias:svelte/internal/server={Path.Combine(svelteSource, "internal", "server", "index.js")}");
        }
        else
        {
            // The public svelte entry point and the compiled component must resolve to
            // the same internal/client module. Using the pre-bundled public runtime
            // together with a separate internal/client bundle creates two Svelte
            // runtimes in one browser graph and breaks hydration.
            startInfo.ArgumentList.Add($"--alias:svelte={Path.Combine(svelteSource, "index-client.js")}");
            startInfo.ArgumentList.Add($"--alias:svelte/internal/client={Path.Combine(svelteSource, "internal", "client", "index.js")}");
        }
        startInfo.ArgumentList.Add($"--alias:svelte/internal/disclose-version={Path.Combine(svelteSource, "internal", "disclose-version.js")}");
        startInfo.ArgumentList.Add($"--alias:svelte/internal/flags/async={Path.Combine(svelteSource, "internal", "flags", "async.js")}");
        startInfo.ArgumentList.Add($"--alias:svelte/internal/flags/legacy={Path.Combine(svelteSource, "internal", "flags", "legacy.js")}");
        if (_options.Minify)
        {
            startInfo.ArgumentList.Add("--minify");
        }
    }

    private static string ResolveExecutable(string? configured, SvelteToolchain toolchain)
    {
        var path = configured;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = toolchain.EsbuildPath;
        }

        if (!File.Exists(path))
        {
            throw new SvelteBuildException("SVE3104", $"Native esbuild was not found at '{path}'.");
        }

        return path;
    }

    private static string NormalizeModulePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || !normalized.EndsWith(".svelte.ts", StringComparison.OrdinalIgnoreCase))
        {
            throw new SvelteBuildException("SVE3105", $"Unsafe Svelte module path '{path}'.");
        }

        return normalized;
    }
}

/// <summary>Loads setup-generated Svelte browser and server runtime bundles.</summary>
public static class SvelteRuntimeBundle
{
    /// <summary>Loads the public server runtime.</summary>
    public static ValueTask<string> LoadServerAsync(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadRuntimeAsync("svelte-server.js");

    /// <summary>Loads the public client runtime.</summary>
    public static ValueTask<string> LoadClientAsync(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadRuntimeAsync("svelte-client.js");

    /// <summary>Loads the server internal runtime.</summary>
    public static ValueTask<string> LoadInternalServerAsync(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadRuntimeAsync("svelte-internal-server.js");

    /// <summary>Loads the client internal runtime.</summary>
    public static ValueTask<string> LoadInternalClientAsync(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadRuntimeAsync("svelte-internal-client.js");

    /// <summary>Loads the fixed async-mode side-effect module.</summary>
    public static ValueTask<string> LoadAsyncFlagsAsync(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadRuntimeAsync("svelte-internal-flags-async.js");

    /// <summary>Loads the legacy-mode side-effect module.</summary>
    public static ValueTask<string> LoadLegacyFlagsAsync(string? toolchainPath = null)
        => SvelteToolchain.Resolve(toolchainPath).LoadRuntimeAsync("svelte-internal-flags-legacy.js");

}
