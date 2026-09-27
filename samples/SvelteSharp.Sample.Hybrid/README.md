# SvelteSharp Hybrid sample

このプロジェクトは、SvelteSharpの`Hybrid`モードを最小構成で確認するサンプルです。

```csharp
app.MapGet("/", () => Svelte.View(
    "Home",
    CreateModel(),
    SvelteRenderMode.Hybrid));
```

## 何が起きるか

1. ビルド時に同じ`Home.svelte`からserver graphとclient graphが生成されます。
2. endpointがC# modelを返します。
3. SvelteSharpがmodelをJSON snapshotへ変換します。
4. Jintがserver graphとsnapshotを使って初期HTMLを生成します。
5. レスポンスにはSSR HTML、同じsnapshot、client entryが含まれます。
6. ブラウザーのclient entryが既存DOMへ`hydrate`します。

`Views/Home.svelte`の`onMount`はサーバーでは実行されません。ブラウザーでhydrateが完了した後にだけ`hydrated`を`true`へ変更するため、画面の表示が次のように変わります。

```text
Server: initial HTML rendered
          ↓ hydrate
Browser: hydrate completed
```

`Hydrated clicks`ボタンを押すと、hydrate後にSvelteのstateとイベントが有効になったことも確認できます。

## 実行

リポジトリのルートで実行します。

```powershell
dotnet run --project samples/SvelteSharp.Sample.Hybrid/SvelteSharp.Sample.Hybrid.csproj
```

JavaScriptを無効にしてページを開いても、SSRされた見出し・リスト・`Server: initial HTML rendered`は表示されます。一方、JavaScriptを有効にするとhydrate後に表示が変わり、ボタンが動作します。
