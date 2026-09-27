# SvelteSharp 設計書

状態: 設計案（実装・互換性検証前）  
対象: ASP.NET Core / .NET 10、Svelte 5 系の固定バージョン  
最重要制約: アプリのビルド、開発、本番実行に Node.js・npm・pnpm・Vite を要求しない

## 1. 目的と判断

SvelteSharp は、ASP.NET Core のエンドポイントが C# の Model と描画モードを決め、Svelte コンポーネントを Server、Client、Hybrid のいずれかで表示するライブラリである。SvelteKit の移植ではなく、ルーティング・認証・データ取得の責任は ASP.NET Core に置く。

JsxCore から、Node を使わないパッケージ取得、ネイティブツールによる変換、プロセス内 JavaScript 実行、C# Model の型生成、不変 URL のアセット配信を採用する。一方、JsxCore は同じ JS をサーバーとブラウザーで実行できるが、Svelte は同じ `.svelte` から **server 用と client 用を別々に生成**する。この違いは隠さず、両成果物のソース、コンパイラ版、Model 契約、ビルド ID を一致させる。

Node 不要は努力目標ではなく受け入れ条件である。互換性問題が起きても Node に自動・手動でフォールバックしない。必要な JavaScript 実行は .NET プロセスに組み込んだエンジンで行う。**Jint と Okojo を利用者が選べる**構成にする。初期既定値は Jint とし、Okojo は独立した adapter パッケージから明示的に選択する。いずれの選択でも Node は使わない。組み込み V8 は将来の第三の選択肢として残せるが、Jint/Okojo の暗黙の代替として起動しない。

## 2. 対象範囲

初期リリースで提供するもの:

- ASP.NET Core の通常のエンドポイントから View、Model、描画モードを指定する API。
- Svelte 5 のコンポーネントと、対応範囲を明示した `.svelte.ts` モジュール。
- Server（SSR のみ）、Client（ブラウザー描画のみ）、Hybrid（SSR + hydration）。
- C# Model から TypeScript 宣言の生成と、サポートする型の明示。
- Node を使わない依存取得、コンパイル、アセット生成、publish。
- Jint と Okojo の選択、選択したエンジンに対する事前の互換性診断。
- CSS、`<svelte:head>`、ソースマップ付き診断、開発時の再ビルドとページ再読込。

初期リリースの対象外:

- SvelteKit の file-based routing、load 関数、adapter、プラグイン体系との互換。
- npm lifecycle script、Node 組み込み API、任意の Vite プラグイン。
- 任意の JS プリプロセッサを無条件に実行する仕組み。
- 未検証の Svelte 機能や npm パッケージを「動作保証」と呼ぶこと。

## 3. 利用者 API

```csharp
app.MapGet("/products/{id:int}", async (int id, ProductService service) =>
{
    ProductPageModel model = await service.GetPageAsync(id);

    return Svelte.View(
        "Products/Detail",
        model,
        mode: SvelteRenderMode.Hybrid);
});
```

```svelte
<!-- Views/Products/Detail.svelte -->
<script lang="ts">
  import type { ProductPageModel } from '@sveltesharp/models';

  let { model }: { model: ProductPageModel } = $props();
</script>

<svelte:head>
  <title>{model.title}</title>
</svelte:head>

<h1>{model.title}</h1>
<p>{model.priceText}</p>
```

View 名からファイルへの対応、サポートする描画モード、必要な JS/CSS、Model 型はビルド manifest に記録する。エンドポイントが未対応モードを指定した場合は明示的なエラーにする。Server 専用 View は client 成果物を生成しなくてよい。Hybrid を許可した View は client-safe な依存グラフを必須とする。

## 4. コンポーネント構成

| コンポーネント | 責務 |
| --- | --- |
| `SvelteSharp.AspNetCore` | エンドポイント結果、HTML shell、公開アセット配信、CSP 対応 |
| `SvelteSharp.Build` | MSBuild 統合、依存復元、増分ビルド、manifest、publish |
| `SvelteSharp.Compiler` | Svelte compiler の実行、診断、client/server 出力 |
| `SvelteSharp.JavaScript` | エンジン非依存の module/Promise/host API 契約と実行制限 |
| `SvelteSharp.Engine.Jint` | Jint による compiler host と SSR host の adapter |
| `SvelteSharp.Engine.Okojo` | Okojo による compiler host と SSR host の adapter |
| `SvelteSharp.Rendering` | SSR、リクエストコンテキスト、Model 受け渡し |
| `SvelteSharp.Models` | C# 型から TypeScript 宣言への変換 |

パッケージ分割は実装時に調整できるが、公開 API とビルド処理、JS エンジン固有実装は分離する。compiler host と SSR host は権限が異なるため別の interface にする。両者は module source の供給、ホスト関数の登録、Promise の完了待ち、例外の診断化、破棄という共通契約を持つ。SvelteSharp のコアは Jint/Okojo の値型や realm 型を公開 API に漏らさない。

### 4.1. エンジンの選択方法

ビルド時に Svelte compiler を動かすエンジンと、本番 SSR に使うエンジンを分けて選べるようにする。たとえば compiler は Jint、SSR は Okojo という構成を許す。これは、Okojo の SSR は適合しても大きな compiler bundle の実行が未対応、という段階でも Okojo を選択可能にするためである。両方を Okojo にする構成も、それぞれの互換性 Gate を通れば使用できる。

```xml
<!-- アプリの .csproj。adapter の版は Directory.Packages.props に固定する。 -->
<PropertyGroup>
  <SvelteSharpCompilerEngine>Jint</SvelteSharpCompilerEngine>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="SvelteSharp.Engine.Okojo" />
</ItemGroup>
```

```csharp
// Program.cs。adapter が SSR 用 host を登録する。
builder.Services.AddSvelteSharp().UseOkojoForSsr();
```

`SvelteSharpCompilerEngine` は `Jint` / `Okojo` のいずれかで、未指定なら Jint。SSR も未指定なら Jint。engine adapter が参照されていない、または固定した Svelte 版との組合せが未認証なら、build または起動時に理由付きで失敗させる。SSR の選択はアプリ単位で固定し、リクエスト単位の切替や失敗時の自動フォールバックはしない。server 出力は両エンジンで実行できる共通形式を基本とし、エンジン固有変換が必要なら manifest と buildId に選択を含める。

実行時の選択は `AddSvelteSharp(options => ...)` の options lambda より後に呼んだ fluent API が優先される。`UseJint()` / `UseOkojo()` は compiler と SSR の両方を設定する短縮形で、片方だけを変える場合は `UseJintForCompiler()` / `UseJintForSsr()` または `UseOkojoForCompiler()` / `UseOkojoForSsr()` を使う。

Okojo は現時点で prerelease の .NET 10+ 向け OSS である。adapter はリポジトリが公開予定として挙げる `Okojo.JavaScript` / `Okojo.JavaScript.Embedding` の API を基本とし、必要な場合だけ `Okojo.Hosting` を追加する。実際の公開済み NuGet パッケージ名・版は adapter 実装時に確認し、未公開なら固定コミットのソース参照で PoC を行う。採用する Okojo と adapter の版を固定し、公開 API の変更は adapter 内に閉じ込める。`Okojo.Node` / `Okojo.Node.Cli` は使わない。`Okojo.Reflection` と `Okojo.WebPlatform` も既定では入れず、SSR に必要なホスト API だけを許可する。

## 5. NuGet 復元型のビルドパイプライン

```text
dotnet build / dotnet publish
  ├─ NuGet restore
  ├─ SvelteSharp.Build が .NET のレジストリクライアントで既定の固定版 JS 依存を復元
  │    ├─ Svelte / TypeScript / esbuild は NuGet に同梱しない
  │    ├─ npm registry の tarball integrity を検証
  │    └─ install/postinstall script と Node/npm は実行しない
  ├─ C# コンパイル → `[SvelteModel]` を付けた Model の型情報を自動収集
  ├─ TypeScript 宣言を生成
  ├─ 指定された組み込み JS エンジン（Jint / Okojo）で Svelte compiler を実行
  │    ├─ compile(..., generate: "server")
  │    ├─ compile(..., generate: "client")
  │    └─ 対応する .svelte.ts は compileModule
  ├─ ネイティブ esbuild で解決・変換・必要な圧縮
  │    ├─ 非公開 server graph
  │    └─ 公開 browser ESM graph + CSS
  ├─ 成果物・設定・ツール版から buildId を計算
  └─ manifest と成果物を検証し、原子的に配置
```

Svelte compiler は既定版を固定し、初回の NuGet/MSBuild セットアップ時に npm registry から復元する。NuGet利用側は `SvelteSharpSvelteVersion`、`SvelteSharpEsbuildVersion`、`SvelteSharpTypeScriptVersion` のMSBuildプロパティで完全なSemVerへ上書きできる。Svelte のソース・バイナリ・TypeScript は SvelteSharp のリポジトリや NuGet パッケージへ同梱しない。復元後のビルド・実行時に Node/npm へアクセスしない。コンパイラが要求するホスト API は検証で列挙し、必要最小限を .NET/Jint/Okojo 側で提供する。ソースに `process` などの名前があるだけで Node を導入するのではなく、実際に必要な挙動を調べる。実装不能な API があれば、対象バージョンまたは機能を制限して診断を出す。

esbuild は Node ラッパーではなくネイティブ実行ファイルをビルド時に直接呼ぶ。client graph はブラウザー向け ESM、server graph は組み込みエンジンが読み込める形式にする。生成したversioned artifactをアプリの出力先 `SvelteSharp` に配置し、実行時はその成果物を読み込む。`svelte/internal/server` 等の import は、この解決段階で固定した Svelte runtime に結び付ける。

依存メタデータは SvelteSharp.Build の復元マニフェストで固定する。復元時は registry metadata の integrity、依存の版、配布 URL を検証し、アーカイブのパストラバーサルと symbolic link を拒否する。install/postinstall script は実行しない。`node:` import やサーバー固有 API を要求するパッケージは、互換レイヤーを明示的に実装していない限りビルドエラーにする。Release build は復元済みの生成物を利用し、起動時に外部ネットワークへアクセスしない。

## 6. 描画モードとデータ契約

| モード | サーバーでコンポーネント実行 | 初期 HTML | ブラウザー JS | Model の扱い |
| --- | --- | --- | --- | --- |
| Server | する | SSR | 不要 | request scope の CLR facade を使用可能 |
| Client | しない | shell | `mount` | JSON snapshot を渡す |
| Hybrid | する | SSR | `hydrate` | 同一 JSON snapshot を双方へ渡す |

Hybrid は **C# オブジェクトを直接 SSR に渡してから別途 JSON 化しない**。endpoint の戻り値を一度だけ規定の `System.Text.Json` 設定で JSON snapshot にし、それをサーバー側 JS とブラウザー側 JS の両方でパースする。日付、数値、nullable、列挙型、polymorphism、カスタム converter の表現差を契約テストで確認する。循環参照や非対応の型は暗黙に変換せずエラーにする。

Hybrid の初期 DOM に影響する時刻、乱数、カルチャ、タイムゾーン、認証情報、機能フラグも Model に確定する。初期描画中の再フェッチや server-only API 呼び出しを前提にしない。ブラウザー専用 API は mount 後の処理へ置く。

Server 専用 View では、JsxCore と同様、request scope のサービスを JS へ直接渡す余地を設ける。ただし公開するのは登録した facade のみとし、DI container 全体や任意の CLR 型を公開しない。Hybrid/Client の依存グラフに server-only import が入ればビルドエラーにする。server-only View を後から Hybrid に変更する場合は Model 境界への移行が必要である。

## 7. SSR、head、HTML shell

SSR は `svelte/server` の `render` から body と head を受け取り、ASP.NET Core の HTML shell に組み込む。非同期出力を含むため、ホストは Promise の完了または失敗を正しく待てなければならない。待てないエンジン・機能の組合せを同期結果として扱わない。

Server/Hybrid では `<svelte:head>` の結果を一次情報とし、shell 側の title/meta と重複させない。Client ではコンポーネントをサーバー実行しないため、初回レスポンスに必要な title/meta を endpoint の `PageMetadata` で指定する。CSS はページの manifest から `<link>` を生成する。HTML shell は SSR body、JSON snapshot、client entry、CSS を manifest の同一 buildId から組み立てる。

SSR 失敗時に Client モードへ黙って切り替えない。診断可能なエラーを返し、観測情報に View 名・buildId・診断位置を残す。開発時のエラーページと本番の情報非開示を分ける。

## 8. JS エンジンとリクエスト分離

ビルド用 compiler host と本番 SSR host は用途・権限・制限を分ける。本番 SSR にはファイルシステム、ネットワーク、任意の CLR reflection を公開しない。タイムアウト、キャンセル、同時実行数の上限を設ける。ハードなメモリ上限をエンジンが提供しない場合は、その上限を保証したと表示せず、入力・出力サイズと同時実行数を制限する。

モジュールスコープの可変状態がリクエストをまたぐと情報漏れになる。初期実装では **SSR エンジンをリクエスト間で無条件に再利用しない**。コンパイル済み成果物と解析結果は共有できるが、実行コンテキストは分離する。エンジンプールは、モジュールキャッシュ・global・CLR facade が完全に隔離または再初期化できることをテストで証明してから導入する。

Okojo adapter では `JsRuntime` / `JsRealm` の module loader に非公開 server graph を与え、リクエスト専用の runtime で `render` を実行する。Okojo が提供する実行時間・命令数の制限を設定する。Promise の job queue を正しく進め、`render` が返す PromiseLike の完了・例外・キャンセルを ASP.NET Core の `Task` に橋渡しすることを必須とする。これらは Okojo の一般的な機能から自動的に保証されるわけではなく、Svelte SSR を使った adapter 試験で確認する。

Jint と Okojo の compiler/SSR を同じ固定版 Svelte のテスト corpus で比較する。生成物、SSR の body/head、hydration、診断、スループットを測定し、エンジン別の互換表を公開する。Okojo を選択して失敗したときに Jint や V8 へ黙って切り替えない。Okojo の prerelease 更新時は corpus を全件再実行してから対応版を上げる。

## 9. 型検査と診断

Model から `.d.ts` を作る際は JSON 上の名前・nullability・配列・辞書・列挙型を基準にする。`[SvelteModel]` を付けた型は ASP.NET Core 起動時と NuGet/MSBuild ビルド時に自動発見される。実行時だけ公開したい型は `AddModel<T>()` で明示登録できる。未対応 converter や不明な polymorphism は `any` に落とさず、明示的なマッピングを要求する。Svelte 側は生成型を `$props()` に使用する。

Svelte compiler の診断と TypeScript の型検査は別物である。初期リリースで「テンプレートまで型安全」と宣言するには、Svelte ソースを型検査可能な TS へ変換する処理（`svelte2tsx` 相当）を Node なしで動かし、ネイティブ TypeScript 検査器へ渡す必要がある。この経路が未完成なら、提供するのは Model 宣言生成とコンパイラ診断までと明記し、型検査済みとは表示しない。

Svelte が直接扱える TypeScript は基本的に type-only 構文である。値を生成する TS 構文は明示的なプリプロセス経路を通す。対応範囲、診断位置の sourcemap、`.svelte.ts` の挙動をそれぞれテストする。

## 10. アセット、キャッシュ、publish

公開 URL は `/_svelte/v{buildId}/...` とし、JS/CSS/画像の内容が変われば URL も変わる。buildId の入力は SvelteSharp 版、Svelte/compiler runtime 版、依存 lockfile、生成 JS/CSS、ビルド設定とする。manifest 自身や buildId を含む URL はハッシュ入力から外し、循環を避ける。

server graph は publish 出力の非公開領域に置き、静的ファイル middleware から到達できないようにする。公開ファイルは browser graph と必要な CSS/画像だけを allowlist で配信する。ビルドは一時領域で完了させ、検証後に snapshot を原子的に切り替える。旧 HTML が参照する旧 buildId は猶予期間中保持する。

`dotnet publish` はセットアップ済みの Svelte 依存を利用し、起動時の依存復元・Svelte コンパイル・外部ネットワークを不要にする。開発時はファイル変更後の増分再ビルドとページ再読込を先に実装し、状態保持型 HMR は別機能とする。

実行時は `SvelteSharp.Build` が生成した versioned artifact をアプリの実行ディレクトリまたは親プロジェクトの `SvelteSharp` から自動検出し、Jint/Okojo が server graph を実行する。`BuildOutputPath` は非標準配置向けの明示的な上書きであり、通常は不要である。実行時に Svelte compiler や native esbuild を起動しない。ビルド成果物が存在しない開発用の明示的なフォールバックでは動的コンパイル経路を使えるが、本番の通常経路は ASP.NET Core と組み込み JavaScript engine のプロセス内実行である。

## 11. セキュリティ要件

- Model を HTML に埋め込む際は JSON と HTML の両文脈に適したエンコードを行う。`</script>` 等による脱出をテストする。
- 秘密情報を Model に含めない。Server-only facade を client graph に混入させない。
- `<script>`、CSS、画像の CSP を設計し、nonce または hash を利用可能にする。
- `{@html}` は信頼済み入力に限定し、通常の文字列は Svelte のエスケープに任せる。
- パッケージ展開時のパス検証、integrity 検証、lockfile 固定、install script 不実行を必須とする。
- SSR 実行の時間・命令数・入力/出力サイズ・同時実行数を制限し、利用可能なエンジンではメモリ上限も設定する。例外から内部パスやサービス情報を公開しない。

## 12. 検証計画と受け入れ条件

### Gate 1: Node 不要の compiler

- Node/npm/pnpm を PATH から除いた環境で、固定版 Svelte compiler を選択した Jint または Okojo から呼べる。
- 基本構文、runes、scoped CSS、`.svelte.ts` の client/server 出力と診断が得られる。
- コンパイラの性能・メモリを代表的な View 群で測定する。

### Gate 2: SSR と hydration

- 選択した Jint または Okojo で server graph の `render` から body/head を取得し、Promise と失敗を正しく扱える。
- 同一 Model snapshot で browser graph が hydrate し、DOM を不必要に作り直さずイベントが動く。
- `<svelte:head>`、CSS、複数 View、nested component、async 対応範囲を確認する。
- compiler=Jint/SSR=Okojo、compiler=Okojo/SSR=Jint、両方同一エンジンの組合せをそれぞれ認証する。未認証の組合せは選択時に拒否する。

### Gate 3: 分離と安全性

- 並列リクエスト間で module/global/CLR facade の状態が漏れない。
- Okojo adapter が既定で `Okojo.Reflection`、`Okojo.WebPlatform`、`Okojo.Node` を要求・有効化しない。
- server-only import が client 出力や公開 URL に存在しない。
- 悪意ある Model 文字列、CSP、依存アーカイブ、SSR 制限の試験を通す。

### Gate 4: 配布

- Node が存在しない CI で `dotnet build` と `dotnet publish` が成功する。
- Jint 構成と Okojo 構成をそれぞれ独立して publish し、非選択エンジンへの暗黙依存がない。
- publish 成果物をネットワーク接続なし・Node なしで起動できる。
- 同一入力で同一 buildId、変更後に新 buildId、旧アセット保持が成立する。
- ブラウザーテストも Node をテスト基盤の必須依存にしない。

Gate を通らない機能はリリース範囲から外し、診断と互換表で明示する。Node を追加して Gate を通過した扱いにはしない。

## 13. 実装順序

1. 固定 Svelte 版の compiler を Jint と Okojo でそれぞれ実行する最小実験。
2. 両エンジンで単一 `.svelte` の server/client コンパイル、SSR、ブラウザー hydration を縦断実験する。
3. Node 不要の依存復元、native esbuild、複数 View、CSS、manifest。
4. ASP.NET Core API と3描画モード、Model snapshot、head。
5. リクエスト分離、セキュリティ、型宣言と診断。
6. publish、増分ビルド、互換表、性能測定、ドキュメント。

## 14. 参照資料

- [JsxCore: How it works](https://github.com/davidwhitney/JsxCore/blob/main/docs/how-it-works.md)
- [JsxCore: Render modes](https://github.com/davidwhitney/JsxCore/blob/main/docs/render-modes.md)
- [JsxCore: .NET interop](https://github.com/davidwhitney/JsxCore/blob/main/docs/dotnet-interop.md)
- [Svelte: Compiler API](https://svelte.dev/docs/svelte/svelte-compiler)
- [Svelte: Server API](https://svelte.dev/docs/svelte/svelte-server)
- [Svelte: Client API](https://svelte.dev/docs/svelte/svelte)
- [Svelte: TypeScript](https://svelte.dev/docs/svelte/typescript)
- [Jint](https://github.com/sebastienros/jint)
- [Okojo: README・embedding API・現在の公開予定パッケージ](https://github.com/akeit0/okojo)
- [ClearScript / V8](https://github.com/ClearFoundry/ClearScript)
