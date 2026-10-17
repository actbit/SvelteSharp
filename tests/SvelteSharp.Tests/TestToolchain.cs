using SvelteSharp.Compiler;

namespace SvelteSharp.Tests;

/// <summary>Locates the setup-installed Svelte toolchain restored beside the tests.</summary>
internal static class TestToolchain
{
    /// <summary>Absolute path to the toolchain directory restored by the build.</summary>
    public static string Path { get; } = Find();

    /// <summary>Resolves the toolchain consumed by integration tests.</summary>
    public static SvelteToolchain Resolve() => SvelteToolchain.Resolve(Path);

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "tools", "svelte");
            if (File.Exists(System.IO.Path.Combine(candidate, "generated", "svelte-compiler.js")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The setup-installed Svelte toolchain could not be found.");
    }
}
