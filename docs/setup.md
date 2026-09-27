# セットアップ

アプリ側はNuGet参照だけでセットアップできます。Svelte、TypeScript、esbuildのファイルはSvelteSharpのリポジトリやNuGetパッケージには含まれません。

```xml
<ItemGroup>
  <PackageReference Include="SvelteSharp" Version="1.0.0" />
  <PackageReference Include="SvelteSharp.Engine.Jint" Version="1.0.0" />
  <PackageReference Include="SvelteSharp.Build" Version="1.0.0" />
</ItemGroup>
```

既定版を変更する場合は、NuGetを参照するアプリ側のプロジェクトでMSBuildプロパティを上書きできます。版はタグや範囲ではなく、再現可能な完全なSemVerを指定します。

```xml
<PropertyGroup>
  <SvelteSharpSvelteVersion>5.57.1</SvelteSharpSvelteVersion>
  <SvelteSharpEsbuildVersion>0.28.1</SvelteSharpEsbuildVersion>
  <SvelteSharpTypeScriptVersion>5.7.3</SvelteSharpTypeScriptVersion>
</PropertyGroup>
```

これらは初回復元だけでなく、既存の`tools/svelte`のメタデータとも比較されます。指定版が異なる場合は、その版でツールチェーンを再生成します。

初回の `dotnet build` で `SvelteSharp.Build` のMSBuild targetが.NET製復元処理を起動します。npm CLI、Node.js、PowerShellスクリプトは使いません。復元処理は固定版のnpm tarballを取得し、SHA-512 integrityと展開パスを検証して、プロジェクトの `tools/svelte` に生成ランタイムとnative esbuildを配置します。native esbuildはビルド時だけ使い、SSRはビルド済みartifactをJint/Okojoで実行します。

実行時の `SvelteToolchainPath` と `BuildOutputPath` は通常指定不要です。SvelteSharpは `tools/svelte` をアプリの作業ディレクトリ・実行ディレクトリとその親ディレクトリから探索し、build artifactは実行ディレクトリまたは親プロジェクトの `SvelteSharp` から自動検出します。

既定の配置先を変更する場合は、アプリのプロジェクトで次を指定します。

```xml
<PropertyGroup>
  <SvelteSharpToolchainPath>$(MSBuildProjectDirectory)\.svelte-toolchain</SvelteSharpToolchainPath>
  <SvelteSharpBuildOutput>$(MSBuildProjectDirectory)artifacts\svelte</SvelteSharpBuildOutput>
</PropertyGroup>
```

実行時APIで上書きする場合も同じく `SvelteSharpOptions.SvelteToolchainPath` と
`SvelteSharpOptions.BuildOutputPath` を指定できます。いずれも指定された場合だけ自動検出より優先されます。

初回復元にはnpm registryへのネットワーク接続が必要です。復元済みの成果物での通常build、publish、起動時にNode/npmや外部ネットワークは必要ありません。

## Model binding

Svelte側の型契約に含めるモデルには `[SvelteModel]` を付けます。NuGetのMSBuild targetがアプリのビルド済みアセンブリから属性付きモデルを発見し、非公開の `models/index.d.ts` を生成します。

```csharp
[SvelteModel(Name = "ProductPage")]
public sealed record ProductPageModel(string Title, decimal Price);
```

```svelte
<script lang="ts">
  import type { ProductPage } from '@sveltesharp/models';

  let { model }: { model: ProductPage } = $props();
</script>

<h1>{model.title}</h1>
```

起動時の自動発見を使わず、実行時だけ公開する型は `builder.AddModel<ProductPageModel>()` で登録できます。エンドポイントの形は `Svelte.View("Products/Detail", model, SvelteRenderMode.Hybrid)` です。

## Engineの選択

`AddSvelteSharp` の options lambda は初期値を設定し、続けて呼ぶ fluent API が対象エンジンを確定します。したがって、両方を指定した場合は fluent API が優先されます。通常は役割を明示できる次の形を使います。

```csharp
var svelte = builder.Services.AddSvelteSharp();
svelte.UseJintForCompiler()
      .UseJintForSsr();
```

`UseJint()` と `UseOkojo()` は compiler と SSR の両方を同時に設定する短縮形です。compiler と SSR を分ける場合は `ForCompiler` / `ForSsr` を使います。
