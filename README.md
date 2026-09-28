# SvelteSharp

日本語: [README.ja.md](README.ja.md)

SvelteSharp renders Svelte 5 components from ASP.NET Core applications using Server, Client, or Hybrid rendering. It targets .NET 10.

- Generate TypeScript contracts from C# models
- Run compilation and SSR in-process with Jint or Okojo
- No Node.js, npm, pnpm, or Vite at application runtime
- Generate separate server and client graphs at build time
- Hydrate server-rendered HTML in the browser with Hybrid rendering

## Installation

Reference the NuGet packages from the consuming application:

```xml
<ItemGroup>
  <PackageReference Include="SvelteSharp" Version="1.0.0" />
  <PackageReference Include="SvelteSharp.Engine.Jint" Version="1.0.0" />
  <PackageReference Include="SvelteSharp.Build" Version="1.0.0" />
</ItemGroup>
```

On the first `dotnet build`, the Svelte, TypeScript, and esbuild toolchain is restored externally. These binaries and TypeScript sources are not embedded in the SvelteSharp NuGet packages.

Override the pinned versions from the consuming project with exact SemVer values:

```xml
<PropertyGroup>
  <SvelteSharpSvelteVersion>5.57.1</SvelteSharpSvelteVersion>
  <SvelteSharpEsbuildVersion>0.28.1</SvelteSharpEsbuildVersion>
  <SvelteSharpTypeScriptVersion>5.7.3</SvelteSharpTypeScriptVersion>
</PropertyGroup>
```

`SvelteToolchainPath` and `BuildOutputPath` normally do not need to be set. SvelteSharp discovers the default locations automatically. Use the MSBuild properties or `SvelteSharpOptions` only for non-standard layouts.

## Minimal example

```csharp
using SvelteSharp;
using SvelteSharp.AspNetCore;
using SvelteSharp.Engine.Jint;

var builder = WebApplication.CreateBuilder(args);

var svelte = builder.Services.AddSvelteSharp();
svelte.UseJintForCompiler()
      .UseJintForSsr();

var app = builder.Build();
app.MapSvelteSharpAssets();

app.MapGet("/", () => Svelte.View(
    "Home",
    new HomePageModel("Hello SvelteSharp"),
    SvelteRenderMode.Hybrid));

app.Run();

public sealed record HomePageModel(string Title);
```

Define `Views/Home.svelte` as a regular Svelte component:

```svelte
<script lang="ts">
  import type { HomePageModel } from '@sveltesharp/models';

  let { model }: { model: HomePageModel } = $props();
</script>

<h1>{model.title}</h1>
```

## Rendering modes

| Mode | Behavior |
| --- | --- |
| `Server` | Render on the server without returning a client bundle |
| `Client` | Return an HTML shell and mount in the browser |
| `Hybrid` | Render HTML on the server and hydrate it in the browser |

Hybrid rendering shares one JSON snapshot of the C# model between SSR and the browser. Requests load the artifacts produced by `dotnet build`; Svelte compiler and esbuild are not started during request processing.

## Model binding

Apply `[SvelteModel]` to C# types that should be exposed as TypeScript contracts. SvelteSharp generates declarations under `@sveltesharp/models`.

```csharp
using SvelteSharp.Models;

[SvelteModel(Name = "ProductPage")]
public sealed record ProductPageModel(string Title, decimal Price);
```

```svelte
<script lang="ts">
  import type { ProductPage } from '@sveltesharp/models';

  let { model }: { model: ProductPage } = $props();
</script>

<h1>{model.title}</h1>
<p>{model.price}</p>
```

## JavaScript engine selection

The options lambda provides initial values. Fluent engine selectors called afterward take precedence. `UseJint()` and `UseOkojo()` are shortcuts that configure both the compiler and SSR hosts. Use the role-specific methods when they should differ:

```csharp
var svelte = builder.Services.AddSvelteSharp();
svelte.UseJintForCompiler()
      .UseOkojoForSsr();
```

To use Okojo, reference the `SvelteSharp.Engine.Okojo` package as well.

Jint SSR uses a bounded engine pool. Each precompiled server bundle is parsed once per pooled
engine, and the request-specific global state is restored before the engine is returned. The
bundle is trusted generated code; request models still cross the JavaScript boundary as JSON.

## Samples

The repository contains one independent ASP.NET Core project for each rendering mode:

```powershell
dotnet run --project samples/SvelteSharp.Sample.Hybrid/SvelteSharp.Sample.Hybrid.csproj
dotnet run --project samples/SvelteSharp.Sample.Client/SvelteSharp.Sample.Client.csproj
dotnet run --project samples/SvelteSharp.Sample.Server/SvelteSharp.Sample.Server.csproj
```

See [samples/README.md](samples/README.md) for the Hybrid flow and the differences between the projects.

## Development

```powershell
dotnet test tests/SvelteSharp.Tests/SvelteSharp.Tests.csproj
```

### CI and performance comparison

The GitHub Actions `CI` workflow restores, builds, and tests the repository on .NET 10. Manual or scheduled runs also execute a BenchmarkDotNet comparison of pooled and isolated SvelteSharp (Jint SSR) against JsxCore (Preact SSR) using equivalent precompiled views and upload the results as an artifact.

Run the comparison locally with:

```powershell
dotnet run --project benchmarks/SvelteSharp.Benchmarks/SvelteSharp.Benchmarks.csproj -c Release -- --filter '*SsrRenderingBenchmarks*'
```

The benchmark excludes initial compilation, esbuild, toolchain restoration, and file discovery. It measures steady-state SSR of precompiled views only; treat the result as a relative comparison from the same machine and runtime, not an absolute claim across environments.

More documentation:

- [Setup](docs/setup.md)
- [Architecture](docs/architecture.md)
- [Samples](samples/README.md)
