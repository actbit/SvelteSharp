# SvelteSharp setup-time toolchain

This directory is populated automatically by the `SvelteSharp.Build` NuGet
target on the first `dotnet build`. The NuGet package downloads the default
pinned Svelte, TypeScript, and esbuild packages from the npm registry, verifies
their integrity, and generates the runtime bundles consumed by SvelteSharp.
The consuming project can override the exact versions with
`SvelteSharpSvelteVersion`, `SvelteSharpTypeScriptVersion`, and
`SvelteSharpEsbuildVersion`.

The installed packages and generated files are intentionally not part of the
SvelteSharp source tree or NuGet package. Node/npm and a PowerShell setup step
are not required.
