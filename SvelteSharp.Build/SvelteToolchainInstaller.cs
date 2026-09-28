using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SvelteSharp.Compiler;

namespace SvelteSharp.Build;

/// <summary>
/// Restores the external Svelte toolchain during the NuGet/MSBuild setup step.
/// Package source is never embedded in SvelteSharp or its NuGet package.
/// </summary>
public sealed class SvelteToolchainInstaller
{
    private const string Registry = "https://registry.npmjs.org/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly Regex AsyncHooksBoundary = new(
        "var (?<storage>[A-Za-z_$][\\w$]*)=null,(?<promise>[A-Za-z_$][\\w$]*)=null;function (?<init>[A-Za-z_$][\\w$]*)\\(\\)\\{.*?\\}function (?<next>[A-Za-z_$][\\w$]*)\\(\\)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>Installs packages and generates all SvelteSharp runtime inputs.</summary>
    public async Task<SvelteToolchain> InstallAsync(
        string rootPath,
        CancellationToken cancellationToken = default,
        SvelteToolchainVersions? versions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        versions ??= SvelteToolchainVersions.Default;
        versions.Validate();

        var root = Path.GetFullPath(rootPath);
        var toolchain = new SvelteToolchain(root);
        var metadataPath = Path.Combine(toolchain.GeneratedPath, "toolchain.json");
        if (toolchain.IsInstalled()
            && File.Exists(metadataPath)
            && MetadataMatches(metadataPath, versions))
        {
            return toolchain;
        }

        Directory.CreateDirectory(root);
        foreach (var package in GetPackages(versions))
        {
            await InstallPackageAsync(root, package.Name, package.Version, cancellationToken);
        }

        var compilerSource = Path.Combine(root, "node_modules", "svelte", "compiler", "index.js");
        if (!File.Exists(compilerSource))
        {
            throw new SvelteBuildException("SVE3201", $"The Svelte compiler entry was not found at '{compilerSource}'.");
        }

        Directory.CreateDirectory(toolchain.GeneratedPath);
        File.Copy(compilerSource, toolchain.CompilerBundlePath, overwrite: true);

        await BundleAsync(root, toolchain, "svelte/server", "svelte-server.js", "neutral", cancellationToken);
        await BundleAsync(root, toolchain, "svelte", "svelte-client.js", "browser", cancellationToken);
        await BundleAsync(root, toolchain, "svelte/internal/server", "svelte-internal-server.js", "neutral", cancellationToken);
        await BundleAsync(root, toolchain, "svelte/internal/client", "svelte-internal-client.js", "browser", cancellationToken);
        await BundleAsync(root, toolchain, "svelte/internal/flags/async", "svelte-internal-flags-async.js", "neutral", cancellationToken);
        await BundleAsync(root, toolchain, "svelte/internal/flags/legacy", "svelte-internal-flags-legacy.js", "neutral", cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(toolchain.GeneratedPath, "esbuild.path"),
            Path.GetRelativePath(root, toolchain.EsbuildPath),
            Encoding.UTF8,
            cancellationToken);
        await File.WriteAllTextAsync(
            metadataPath,
            $$"""
            {
              "svelte": "{{versions.SvelteVersion}}",
              "esbuild": "{{versions.EsbuildVersion}}",
              "typescript": "{{versions.TypeScriptVersion}}",
              "installer": "SvelteSharp.Build",
              "packages": "verified from registry metadata"
            }
            """,
            Encoding.UTF8,
            cancellationToken);
        toolchain.EnsureInstalled();
        return toolchain;
    }

    private static bool MetadataMatches(string metadataPath, SvelteToolchainVersions versions)
    {
        try
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(metadataPath, Encoding.UTF8));
            var root = metadata.RootElement;
            return string.Equals(root.GetProperty("svelte").GetString(), versions.SvelteVersion, StringComparison.Ordinal)
                && string.Equals(root.GetProperty("esbuild").GetString(), versions.EsbuildVersion, StringComparison.Ordinal)
                && string.Equals(root.GetProperty("typescript").GetString(), versions.TypeScriptVersion, StringComparison.Ordinal);
        }
        catch (Exception) when (File.Exists(metadataPath))
        {
            return false;
        }
    }

    private static async Task InstallPackageAsync(
        string root,
        string packageName,
        string version,
        CancellationToken cancellationToken)
    {
        var packageRoot = Path.Combine(root, "node_modules", packageName.Replace('/', Path.DirectorySeparatorChar));
        var packageJson = Path.Combine(packageRoot, "package.json");
        if (File.Exists(packageJson))
        {
            using var existing = JsonDocument.Parse(await File.ReadAllTextAsync(packageJson, cancellationToken));
            if (existing.RootElement.TryGetProperty("version", out var existingVersion)
                && string.Equals(existingVersion.GetString(), version, StringComparison.Ordinal))
            {
                return;
            }

            var attributes = File.GetAttributes(packageRoot);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new SvelteBuildException("SVE3212", $"The npm package path '{packageRoot}' is a reparse point.");
            }

            Directory.Delete(packageRoot, recursive: true);
        }

        var encodedName = Uri.EscapeDataString(packageName).Replace("%2F", "%2f", StringComparison.OrdinalIgnoreCase);
        using var metadataResponse = await Http.GetAsync(Registry + encodedName, cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();
        using var metadata = await metadataResponse.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new SvelteBuildException("SVE3202", $"npm metadata for '{packageName}' was empty.");
        var versionMetadata = metadata.RootElement.GetProperty("versions").GetProperty(version);
        var dist = versionMetadata.GetProperty("dist");
        var tarball = dist.GetProperty("tarball").GetString()
            ?? throw new SvelteBuildException("SVE3203", $"npm metadata for '{packageName}@{version}' has no tarball.");
        var integrity = dist.GetProperty("integrity").GetString()
            ?? throw new SvelteBuildException("SVE3204", $"npm metadata for '{packageName}@{version}' has no integrity.");

        var bytes = await Http.GetByteArrayAsync(tarball, cancellationToken);
        VerifyIntegrity(packageName, version, bytes, integrity);
        ExtractPackage(bytes, packageRoot);
    }

    private static void VerifyIntegrity(string packageName, string version, byte[] bytes, string integrity)
    {
        const string prefix = "sha512-";
        if (!integrity.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new SvelteBuildException("SVE3205", $"Unsupported integrity algorithm for '{packageName}@{version}'.");
        }

        var expected = Convert.FromBase64String(integrity[prefix.Length..]);
        var actual = SHA512.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new SvelteBuildException("SVE3206", $"Integrity verification failed for '{packageName}@{version}'.");
        }
    }

    private static void ExtractPackage(byte[] bytes, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        using var compressed = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            {
                throw new SvelteBuildException("SVE3207", "npm packages containing links are not accepted.");
            }

            var name = entry.Name.Replace('\\', '/');
            if (!name.StartsWith("package/", StringComparison.Ordinal))
            {
                continue;
            }

            var relative = name["package/".Length..];
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var destination = Path.GetFullPath(Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
                ? destinationRoot
                : destinationRoot + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new SvelteBuildException("SVE3208", $"The npm package contained an unsafe path '{entry.Name}'.");
            }

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var output = File.Create(destination);
            entry.DataStream?.CopyTo(output);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, entry.Mode);
            }
        }
    }

    private static async Task BundleAsync(
        string root,
        SvelteToolchain toolchain,
        string entryPoint,
        string outputFile,
        string platform,
        CancellationToken cancellationToken)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = toolchain.EsbuildPath,
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(entryPoint);
        process.StartInfo.ArgumentList.Add("--bundle");
        process.StartInfo.ArgumentList.Add("--format=esm");
        process.StartInfo.ArgumentList.Add($"--platform={platform}");
        process.StartInfo.ArgumentList.Add("--target=es2022");
        process.StartInfo.ArgumentList.Add("--external:node:*");
        process.StartInfo.ArgumentList.Add("--minify");
        process.StartInfo.ArgumentList.Add($"--outfile={Path.Combine("generated", outputFile)}");
        if (!process.Start())
        {
            throw new SvelteBuildException("SVE3209", "The setup-installed esbuild process could not be started.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new SvelteBuildException("SVE3210", $"esbuild failed while setting up '{entryPoint}': {error.Trim()}");
        }

        if (outputFile == "svelte-server.js")
        {
            var path = Path.Combine(toolchain.GeneratedPath, outputFile);
            var source = await File.ReadAllTextAsync(path, cancellationToken);
            var match = AsyncHooksBoundary.Match(source);
            if (!match.Success)
            {
                throw new SvelteBuildException("SVE3211", "The installed Svelte server runtime changed its async_hooks boundary.");
            }

            var replacement =
                $"var {match.Groups["storage"].Value}={{current:null,getStore(){{return this.current}},run(e,t){{var r=this.current;this.current=e;try{{var n=t();return n&&typeof n.then===\"function\"?n.finally(()=>{{this.current=r}}):(this.current=r,n)}}catch(o){{throw this.current=r,o}}}}}};" +
                $"function {match.Groups["init"].Value}(){{return Promise.resolve()}}function {match.Groups["next"].Value}()";
            await File.WriteAllTextAsync(path, source.Replace(match.Value, replacement, StringComparison.Ordinal), Encoding.UTF8, cancellationToken);
        }
    }

    private static IReadOnlyList<PackageSpec> GetPackages(SvelteToolchainVersions versions)
    {
        var platform = OperatingSystem.IsWindows()
            ? (Environment.Is64BitOperatingSystem ? "@esbuild/win32-x64" : "@esbuild/win32-ia32")
            : OperatingSystem.IsLinux() ? "@esbuild/linux-x64" : "@esbuild/darwin-x64";

        return
        [
            new("svelte", versions.SvelteVersion),
            new("esbuild", versions.EsbuildVersion),
            new(platform, versions.EsbuildVersion),
            new("typescript", versions.TypeScriptVersion),
            new("@jridgewell/gen-mapping", "0.3.13"),
            new("@jridgewell/remapping", "2.3.5"),
            new("@jridgewell/resolve-uri", "3.1.2"),
            new("@jridgewell/sourcemap-codec", "1.6.0"),
            new("@jridgewell/trace-mapping", "0.3.31"),
            new("@sveltejs/acorn-typescript", "1.0.13"),
            new("@types/estree", "1.0.9"),
            new("acorn", "8.18.0"),
            new("aria-query", "5.3.1"),
            new("axobject-query", "4.1.0"),
            new("clsx", "2.1.1"),
            new("devalue", "5.9.4"),
            new("esm-env", "1.2.2"),
            new("esrap", "2.3.14"),
            new("is-reference", "3.0.3"),
            new("locate-character", "3.0.0"),
            new("magic-string", "0.30.21"),
            new("zimmerframe", "1.1.5")
        ];
    }

    private sealed record PackageSpec(string Name, string Version);
}
