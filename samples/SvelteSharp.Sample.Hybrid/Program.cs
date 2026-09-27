using SvelteSharp;
using SvelteSharp.AspNetCore;
using SvelteSharp.Engine.Jint;
using SvelteSharp.Models;

var builder = WebApplication.CreateBuilder(args);

var svelte = builder.Services.AddSvelteSharp(options =>
{
    options.ViewsPath = "Views";
});
svelte.UseJintForCompiler()
      .UseJintForSsr();

var app = builder.Build();
app.MapSvelteSharpAssets();

app.MapGet("/", () =>
{
    // Hybridでは、endpointがC# modelと描画モードを一度だけ決めます。
    // SvelteSharpはmodelをJSON snapshotにして、同じsnapshotをJint SSRと
    // ブラウザー側のhydrateに渡します。
    return Svelte.View(
        "Home",
        CreateModel(),
        SvelteRenderMode.Hybrid);
});

app.Run();

static HomePageModel CreateModel()
    => new(
        "SvelteSharp Hybrid sample",
        ["SSR HTML", "Browser hydration", "C# model binding", "One JSON snapshot"]);

[SvelteModel(Name = "HomePage")]
public sealed record HomePageModel(string Title, IReadOnlyList<string> Features);
