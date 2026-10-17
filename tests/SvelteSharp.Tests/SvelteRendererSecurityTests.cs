using System.Text.Encodings.Web;
using System.Text.Json;

namespace SvelteSharp.Tests;

public sealed class SvelteRendererSecurityTests
{
    [Fact]
    public async Task EscapesTheEmbeddedModelSnapshotWhenAnUnsafeEncoderIsConfigured()
    {
        await using var host = SvelteTestHost.Create(options =>
            options.ModelJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");
        const string payload = "</script><script>alert(1)</script>";

        var rendered = await host.RenderAsync("Home", new { title = payload }, SvelteRenderMode.Hybrid);

        var json = ExtractModelScript(rendered.Document);
        Assert.DoesNotContain("<", json, StringComparison.Ordinal);
        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(payload, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DoesNotIntroduceExtraScriptCloseTags()
    {
        await using var host = SvelteTestHost.Create(options =>
            options.ModelJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        host.Views.Set("Home", "<script>let { model } = $props();</script><h1>{model.title}</h1>");

        var rendered = await host.RenderAsync(
            "Home",
            new { title = "</script></script></script>" },
            SvelteRenderMode.Hybrid);

        // The document contains exactly the model snapshot script and the module script.
        Assert.Equal(2, CountOccurrences(rendered.Document, "</script>"));
    }

    private static string ExtractModelScript(string html)
    {
        const string marker = "id=\"svelte-model\">";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
