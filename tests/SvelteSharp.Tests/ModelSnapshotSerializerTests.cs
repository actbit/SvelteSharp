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
}
