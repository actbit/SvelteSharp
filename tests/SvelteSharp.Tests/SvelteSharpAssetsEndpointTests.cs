using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SvelteSharp.AspNetCore;
using SvelteSharp.Rendering;

namespace SvelteSharp.Tests;

public sealed class SvelteSharpAssetsEndpointTests
{
    [Fact]
    public async Task ServesAllowlistedAssetsAndReturnsNotFoundOtherwise()
    {
        var assetStore = new SvelteAssetStore();
        assetStore.Put("b1", "views/Home.js", "console.log(1);", "text/javascript; charset=utf-8");

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(assetStore);
        var app = builder.Build();
        app.MapSvelteSharpAssets();

        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            var ok = await client.GetAsync("/_svelte/vb1/views/Home.js");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("javascript", ok.Content.Headers.ContentType!.ToString());
            Assert.Contains("immutable", ok.Headers.CacheControl!.ToString());
            Assert.Equal("console.log(1);", await ok.Content.ReadAsStringAsync());

            var missingAsset = await client.GetAsync("/_svelte/vb1/views/Missing.js");
            Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);

            var missingBuild = await client.GetAsync("/_svelte/vother/views/Home.js");
            Assert.Equal(HttpStatusCode.NotFound, missingBuild.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
