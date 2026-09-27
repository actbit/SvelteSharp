# SvelteSharp samples

描画モードごとに独立したASP.NET Coreプロジェクトを用意しています。

| Project | Mode | 内容 |
| --- | --- | --- |
| `SvelteSharp.Sample.Hybrid` | Hybrid | SSRしたHTMLをブラウザーでhydrate |
| `SvelteSharp.Sample.Client` | Client | HTML shellを返してブラウザーで描画 |
| `SvelteSharp.Sample.Server` | Server | SSRだけを実行 |

## 実行

リポジトリのルートで、確認したいプロジェクトを個別に起動します。

```powershell
dotnet run --project samples/SvelteSharp.Sample.Hybrid/SvelteSharp.Sample.Hybrid.csproj
dotnet run --project samples/SvelteSharp.Sample.Client/SvelteSharp.Sample.Client.csproj
dotnet run --project samples/SvelteSharp.Sample.Server/SvelteSharp.Sample.Server.csproj
```

各プロジェクトは独自の`Views/Home.svelte`、C# model、build artifactを持ちます。初回ビルドでは、`SvelteSharp.Build`のtargetがルートの`tools/svelte`へSvelte、TypeScript、esbuildを復元します。Node.js/npmは不要です。

ビルド時にbundleを生成し、実行時は`bin/.../SvelteSharp`のprebuilt artifactをJintが読み込みます。リクエスト処理でSvelte compilerやesbuildは起動しません。

## Hybridの仕組み

Hybridは「サーバーでHTMLを作る」だけではなく、サーバーが作ったHTMLをブラウザー側のSvelteが引き継ぐモードです。

```text
dotnet build
  ├─ server graph  ─┐
  └─ client graph  ─┘  build済みartifactとして保存

HTTP request
  C# model
      │
      └─ JSON snapshotを1回だけ生成
           ├─ Jint + server graph → 初期HTMLと<svelte:head>
           └─ HTML内の<script type="application/json"> → browser client graph
                                                        └─ hydrate(existing DOM)
```

`SvelteSharp.Sample.Hybrid`の画面では、次の2点でこの動きを確認できます。

- 最初のHTMLには`Server: initial HTML rendered`とモデル由来の見出し・リストが含まれる
- ブラウザーでhydrateが完了すると`Browser: hydrate completed`に変わり、`Hydrated clicks`ボタンが動作する

レスポンスの構造は概ね次のようになります。

```html
<div data-svelte-root="Home" data-svelte-mode="hybrid">
  <!-- Jint SSRが生成したHTML -->
</div>
<script type="application/json" id="svelte-model">
  {"title":"...","features":[...]}
</script>
<script type="module" src="/_svelte/v<buildId>/views/Home.js"></script>
```

client graphは空のrootへ`mount`するのではなく、既存のSSR DOMへ`hydrate`します。したがって初期表示はサーバーHTMLで速く開始でき、その後は通常のSvelteコンポーネントとしてイベントやstateが動作します。`Client`は初期HTMLを生成せず`mount`し、`Server`はclient scriptを返さない点が違いです。

## JsxCoreとの比較

JsxCoreのソースは参照せず、SvelteSharpの設計と公開APIの観点だけで比較しています。

| 観点 | SvelteSharp | JsxCoreに寄せている点 / 違い |
| --- | --- | --- |
| エンドポイント | `Svelte.View(view, model, mode)` | endpointがview・model・描画モードを決める形を採用 |
| コンポーネント | `.svelte` + `$props()` | JSXではなくSvelteのcomponent modelを使用 |
| Model | JSON snapshotをSSRとbrowserへ共有 | CLR objectを直接browserへ渡さず、契約を明示 |
| 型契約 | `[SvelteModel]`から`@sveltesharp/models`の`.d.ts`を生成 | C# modelをTypeScript側で型付け |
| SSR / Client | Server、Client、Hybridを別プロジェクトで確認可能 | 同じ描画モードの考え方を提供。ただしSvelteはserver/client graphを別生成 |
| JavaScript実行 | JintまたはOkojoを明示選択 | Node.jsを実行時依存にしない |
| 実行時bundle | ビルド済みgraphを読み込む | リクエスト時にnative esbuildを起動しない |
| ルーティング | ASP.NET Coreが担当 | SvelteKitのfile-based routingは対象外 |
| 依存取得 | NuGetのMSBuild targetが初回build時に復元 | Svelte/TypeScript/esbuildをNuGetへ同梱しない |
