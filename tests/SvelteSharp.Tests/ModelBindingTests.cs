using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SvelteSharp.Models;

namespace SvelteSharp.Tests;

public sealed class ModelBindingTests
{
    [Fact]
    public void GeneratesTypeScriptFromTheSameJsonContractAsTheSnapshot()
    {
        var options = new ModelContractOptions
        {
            EnumsAsStrings = true,
            Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.Default
            }
        };

        var generator = new TypeScriptDeclarationGenerator(options);
        var declaration = generator.Generate<OrderPageModel>();

        Assert.Contains("export interface OrderPage", declaration);
        Assert.Contains("displayName: string;", declaration);
        Assert.Contains("status: \"Draft\" | \"Published\";", declaration);
        Assert.Contains("note?: string | null;", declaration);

        var model = new OrderPageModel
        {
            DisplayName = "</script><script>alert(1)</script>",
            Status = OrderStatus.Published,
            Note = null
        };
        var snapshot = generator.CreateContract(typeof(OrderPageModel), model).JsonSnapshot;

        Assert.Contains("\\u003C/script\\u003E", snapshot);
        Assert.DoesNotContain("</script>", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"displayName\":\"\\u003C/script", snapshot);
        Assert.Contains("\"status\":\"Published\"", snapshot);
    }

    [Fact]
    public void UsesExplicitModelNameAndBuildsAnIndexDeclaration()
    {
        var registry = new SvelteModelRegistry();
        registry.Register<OrderPageModel>();

        var declarations = registry.GenerateDeclarations(new ModelContractOptions
        {
            Json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                Encoder = JavaScriptEncoder.Default
            }
        });

        Assert.True(declarations.TryGetValue(typeof(OrderPageModel), out var declaration));
        Assert.Contains("export interface OrderPage", declaration);
        Assert.DoesNotContain("OrderPageModel", declaration);
    }

    [Fact]
    public void DiscoversAttributedModelsWithoutExecutingApplicationStartup()
    {
        var registry = new SvelteModelRegistry();

        var discovered = registry.RegisterAttributedModels(typeof(ModelBindingTests).Assembly);

        Assert.True(discovered > 0);
        Assert.Contains(typeof(OrderPageModel), registry.GetTypes());
        Assert.Contains(
            registry.GenerateDeclarations().Values,
            declaration => declaration.Contains("export interface OrderPage", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsReferencePreservingSnapshots()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReferenceHandler = ReferenceHandler.Preserve
        };

        var exception = Assert.Throws<ModelContractException>(() =>
            ModelSnapshotSerializer.Serialize(new OrderPageModel(), options));

        Assert.Contains("Reference-preserving", exception.Message);
    }

    [SvelteModel(Name = "OrderPage")]
    private sealed class OrderPageModel
    {
        [JsonPropertyName("displayName")]
        public string DisplayName { get; init; } = string.Empty;

        public OrderStatus Status { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; init; }
    }

    private enum OrderStatus
    {
        Draft,
        Published
    }
}
