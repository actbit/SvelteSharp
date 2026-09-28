# SvelteSharp

English: [README.md](README.md)

SvelteSharp は、ASP.NET Core から Svelte 5 のコンポーネントを Server、Client、Hybrid で描画する .NET 10 向けライブラリです。

- C# の Model を Svelte/TypeScript の型契約へ変換
- Jint または Okojo によるプロセス内の compiler/SSR 実行
- 実行時の Node.js、npm、pnpm、Vite 不要
- Server graph と Client graph をビルド時に生成
- Hybrid では SSR 済みHTMLをブラウザー側で hydrate

## インストール

アプリ側ではNuGetパッケージを参照します。

```xml
<ItemGroup>
  <PackageReference Include="SvelteSharp" Version="1.0.0" />
  <PackageReference Include="SvelteSharp.Engine.Jint" Version="1.0.0" />
  <PackageReference Include="SvelteSharp.Build" Version="1.0.0" />
</ItemGroup>
```

初回の `dotnet build` 時に、Svelte、TypeScript、esbuild が外部ツールチェーンとして復元されます。これらのバイナリやTypeScriptソースはSvelteSharpのNuGetパッケージには含まれません。

既定版を変更する場合は、アプリのプロジェクトで完全なSemVerを指定します。

```xml
<PropertyGroup>
  <SvelteSharpSvelteVersion>5.57.1</SvelteSharpSvelteVersion>
  <SvelteSharpEsbuildVersion>0.28.1</SvelteSharpEsbuildVersion>
  <SvelteSharpTypeScriptVersion>5.7.3</SvelteSharpTypeScriptVersion>
</PropertyGroup>
```

`SvelteToolchainPath` と `BuildOutputPath` は通常指定不要です。SvelteSharpが既定配置を自動検出します。非標準配置の場合だけ、MSBuildプロパティまたは `SvelteSharpOptions` で上書きできます。

## 最小構成

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

`Views/Home.svelte` は通常のSvelteコンポーネントとして記述します。

```svelte
<script lang="ts">
  import type { HomePageModel } from '@sveltesharp/models';

  let { model }: { model: HomePageModel } = $props();
</script>

<h1>{model.title}</h1>
```

## 描画モード

| モード | 動作 |
| --- | --- |
| `Server` | サーバーでSSRし、Client bundleを返さない |
| `Client` | HTML shellを返し、ブラウザーでmountする |
| `Hybrid` | サーバーのHTMLをブラウザー側でhydrateする |

Hybridでは、C# Modelから作ったJSON snapshotをSSRとブラウザーで共有します。リクエスト時にSvelte compilerやesbuildを起動するのではなく、`dotnet build` で生成したartifactを読み込みます。

## Model binding

`[SvelteModel]` を付けたC#型から、`@sveltesharp/models` のTypeScript宣言が生成されます。

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

## JavaScript engineの選択

options lambdaは初期値を設定し、その後に呼ぶfluent APIが優先されます。`UseJint()` と `UseOkojo()` はcompilerとSSRの両方を設定する短縮形です。役割を分ける場合は `ForCompiler` / `ForSsr` を使います。

```csharp
var svelte = builder.Services.AddSvelteSharp();
svelte.UseJintForCompiler()
      .UseOkojoForSsr();
```

Okojoを使う場合は `SvelteSharp.Engine.Okojo` パッケージも参照してください。

Jint の SSR では上限付きのエンジンプールを使用します。事前コンパイル済みの server
bundle はプール内の各エンジンで一度だけ解析し、リクエストごとのグローバル状態を復元してから返却します。bundle は信頼できる生成コードであることを前提とし、リクエストの Model は引き続き JSON として JavaScript 境界を越えます。

## サンプル

描画モードごとに独立したサンプルがあります。

```powershell
dotnet run --project samples/SvelteSharp.Sample.Hybrid/SvelteSharp.Sample.Hybrid.csproj
dotnet run --project samples/SvelteSharp.Sample.Client/SvelteSharp.Sample.Client.csproj
dotnet run --project samples/SvelteSharp.Sample.Server/SvelteSharp.Sample.Server.csproj
```

詳細は [samples/README.md](samples/README.md) を参照してください。

## 開発

```powershell
dotnet test tests/SvelteSharp.Tests/SvelteSharp.Tests.csproj
```

### CI とパフォーマンス比較

GitHub Actions の `CI` workflow は .NET 10 の復元、ビルド、テストを実行します。手動実行またはスケジュール実行では、事前コンパイル済みの同等ビューを使った SvelteSharp (Jint SSR、プールあり/なし) と JsxCore (Preact SSR) の BenchmarkDotNet 比較も実行し、結果をアーティファクトとして保存します。

ローカルで比較する場合:

```powershell
dotnet run --project benchmarks/SvelteSharp.Benchmarks/SvelteSharp.Benchmarks.csproj -c Release -- --filter '*SsrRenderingBenchmarks*'
```

この比較には初回コンパイル、esbuild、ツールチェーン復元、ファイル探索は含めず、事前コンパイル済みビューの定常状態 SSR だけを含めます。実行環境や Jint のエンジン・プール方式が異なるため、結果は絶対値ではなく同一環境での相対値として扱ってください。

設計とセットアップの詳細:

- [セットアップ](docs/setup.md)
- [アーキテクチャ](docs/architecture.md)
- [サンプル](samples/README.md)
