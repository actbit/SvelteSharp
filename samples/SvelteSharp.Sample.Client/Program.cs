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

app.MapGet("/", () => Svelte.View(
    "Home",
    CreateModel(),
    SvelteRenderMode.Client,
    new PageMetadata("SvelteSharp Client sample")));

app.Run();

static HomePageModel CreateModel()
    => new(
        "SvelteSharp Client sample",
        ["HTML shell", "Browser rendering", "C# model binding"]);

[SvelteModel(Name = "HomePage")]
public sealed record HomePageModel(string Title, IReadOnlyList<string> Features);
