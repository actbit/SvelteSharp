# SvelteSharp 実装状況

`architecture.md` の責務を、.NET 10 のプロジェクト群と namespace ごとに分離しています。JsxCore のソースには依存していません。

実装済み:

- `Svelte.View`、Server / Client / Hybrid、`PageMetadata`
- ASP.NET Core の `AddSvelteSharp` と `MapSvelteSharpAssets`
- C# model の HTML-safe JSON snapshot、`[SvelteModel]` の自動発見、明示的な model registry、TypeScript 宣言生成
- 生成 `.d.ts` と同じ JSON 設定による Svelte 側 model binding。通常の NuGet/MSBuild build ではアプリの `TargetPath` を検査し、`[SvelteModel]` の `models/index.d.ts` を自動生成
- source provider、Svelte compiler、server/client の別 graph、compiler diagnostics
- Jint / Okojo の compiler host・SSR host 契約と、明示的な engine 選択
- build ID、immutable asset URL、manifest、public/private artifact 分離、原子的 publish
- build済みserver/client artifactの実行時ロード。通常のJint/Okojo実行ではesbuild子プロセスを起動しない
- request ごとの描画入力、path traversal 防止、CSP nonce、CSP-safe JSON
- `SvelteSharp.Build` NuGet の自動 toolchain restore
  - Svelte / TypeScript / esbuild は SvelteSharp の source/NuGet に同梱しない
  - 利用側MSBuildプロパティで3つの完全SemVerを上書き可能。既定値と生成メタデータを比較して再復元
  - 初回 `dotnet build` 時に .NET が npm registry から固定版 tarball を取得
  - SHA-512 integrity、展開パス、symbolic link を検証し、install script は実行しない
  - 復元後の compiler/SSR/build は Node/npm を使わない
- 実装テスト（model contract、XSS-safe snapshot、Jint compile/bundle/SSR、private `.d.ts` artifact）

意図的な制限:

- `ManagedSvelteCompiler` は動作確認用 baseline で、実際の Svelte 5 機能には Jint/Okojo adapter を使用します。
- Svelte の type-only import と model contract を提供します。Svelte source 全体の TypeScript 型検査を提供するものではありません。
- Okojo の現行 prerelease adapter は同期 SSR を認証済みとし、async SSR は理由付きエラーにします。Jint は検証済みの async render 経路を使用します。
- npm registry への初回復元は build/setup 時に行います。起動時や復元済み成果物の利用時に外部ネットワークへアクセスしません。
