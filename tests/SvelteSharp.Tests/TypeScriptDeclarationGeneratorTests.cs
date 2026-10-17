using System.Text.Json.Serialization;
using SvelteSharp.Models;

namespace SvelteSharp.Tests;

public sealed class TypeScriptDeclarationGeneratorTests
{
    [Fact]
    public void GeneratesNestedArraysDictionariesAndNullableValues()
    {
        var declaration = new TypeScriptDeclarationGenerator().Generate<CatalogModel>();

        Assert.Contains("export interface CatalogModel", declaration);
        Assert.Contains("items: Array<CatalogItem>;", declaration);
        Assert.Contains("export interface CatalogItem", declaration);
        Assert.Contains("counts: Record<string, number>;", declaration);
        Assert.Contains("title: string | null;", declaration);
        Assert.Contains("createdAt: string;", declaration);
        Assert.Contains("kind: number;", declaration);
    }

    [Fact]
    public void UsesStringEnumsWhenConfigured()
    {
        var declaration = new TypeScriptDeclarationGenerator(new ModelContractOptions { EnumsAsStrings = true })
            .Generate<CatalogModel>();

        Assert.Contains("kind: \"Draft\" | \"Published\";", declaration);
    }

    [Theory]
    [InlineData("display-name")]
    [InlineData("2count")]
    public void QuotesJsonNamesThatAreNotTypeScriptIdentifiers(string jsonName)
    {
        var declaration = new TypeScriptDeclarationGenerator().Generate<InexactNameModel>();

        Assert.Contains($"\"{jsonName}\": string;", declaration);
    }

    [Fact]
    public void MarksNullIgnoredPropertiesAsOptional()
    {
        var declaration = new TypeScriptDeclarationGenerator().Generate<OptionalModel>();

        Assert.Contains("count?: number;", declaration);
    }

    [Fact]
    public void RejectsNonStringDictionaryKeys()
        => Assert.Throws<ModelContractException>(() => new TypeScriptDeclarationGenerator().Generate<BadKeyModel>());

    [Fact]
    public void RejectsUnsupportedPropertyTypes()
        => Assert.Throws<ModelContractException>(() => new TypeScriptDeclarationGenerator().Generate<BadTypeModel>());

    private sealed class CatalogModel
    {
        public IReadOnlyList<CatalogItem> Items { get; init; } = Array.Empty<CatalogItem>();
        public Dictionary<string, int> Counts { get; init; } = new();
        public string? Title { get; init; }
        public DateTime CreatedAt { get; init; }
        public CatalogKind Kind { get; init; }
    }

    private sealed class CatalogItem
    {
        public decimal Price { get; init; }
        public bool Active { get; init; }
    }

    private enum CatalogKind
    {
        Draft,
        Published
    }

    private sealed class InexactNameModel
    {
        [JsonPropertyName("display-name")]
        public string DisplayName { get; init; } = string.Empty;

        [JsonPropertyName("2count")]
        public string Total { get; init; } = string.Empty;
    }

    private sealed class OptionalModel
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Count { get; init; }
    }

    private sealed class BadKeyModel
    {
        public Dictionary<int, string> Values { get; init; } = new();
    }

    private sealed class BadTypeModel
    {
        public System.IO.Stream? Data { get; init; }
    }
}
