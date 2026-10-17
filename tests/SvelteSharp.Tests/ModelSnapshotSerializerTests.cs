using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SvelteSharp.Models;

namespace SvelteSharp.Tests;

public sealed class ModelSnapshotSerializerTests
{
    [Fact]
    public void SerializesNullModelAsNull()
        => Assert.Equal("null", ModelSnapshotSerializer.Serialize(null, typeof(TestModel)));

    [Fact]
    public void EscapesHtmlSensitiveCharactersByDefault()
    {
        var json = ModelSnapshotSerializer.Serialize(new TestModel { Text = "</script>" }, typeof(TestModel));

        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003C", json);
    }

    [Fact]
    public void EscapesOutputFromAnUnsafeEncoder()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = ModelSnapshotSerializer.Serialize(
            new TestModel { Text = "</script><script>alert(1)</script>" },
            typeof(TestModel),
            options);

        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", json, StringComparison.Ordinal);
        Assert.Contains("\\u003C", json);
    }

    [Fact]
    public void EscapesSpecialCharactersAndPreservesTheirValues()
    {
        const string payload = "</script> <!-- --> ]]> <![CDATA[ </ScRiPt > & ' \u2028 \u2029";

        var json = ModelSnapshotSerializer.Serialize(new TestModel { Text = payload }, typeof(TestModel));

        Assert.DoesNotContain("<", json, StringComparison.Ordinal);
        Assert.DoesNotContain(">", json, StringComparison.Ordinal);
        Assert.DoesNotContain("&", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2028", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2029", json, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(payload, document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void EscapesOutputFromAConverterThatWritesRawMarkup()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new RawMarkupConverter());

        var json = ModelSnapshotSerializer.Serialize(new RawModel(), typeof(RawModel), options);

        Assert.DoesNotContain("</script>", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\\u003C", json);
    }

    [Fact]
    public void WrapsCircularModelsAsModelContractException()
    {
        var node = new Node();
        node.Next = node;

        var exception = Assert.Throws<ModelContractException>(
            () => ModelSnapshotSerializer.Serialize(node, typeof(Node)));

        Assert.Contains("cannot be serialized", exception.Message);
    }

    private sealed class TestModel
    {
        public string Text { get; init; } = string.Empty;
    }

    private sealed class Node
    {
        public Node? Next { get; set; }
    }

    private sealed class RawModel
    {
    }

    private sealed class RawMarkupConverter : JsonConverter<RawModel>
    {
        public override RawModel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, RawModel value, JsonSerializerOptions options)
            => writer.WriteRawValue("{\"raw\":\"</script>\"}");
    }
}
