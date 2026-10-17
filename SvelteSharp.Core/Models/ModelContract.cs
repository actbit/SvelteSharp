using System.Text.Json;

namespace SvelteSharp.Models;

/// <summary>Describes the JSON-to-TypeScript contract generated for a model.</summary>
public sealed record ModelContract(
    Type ClrType,
    string TypeScriptName,
    string Declaration,
    string JsonSnapshot);

/// <summary>Thrown when a model cannot be represented by the JSON contract.</summary>
public sealed class ModelContractException : Exception
{
    /// <summary>Creates a model contract exception.</summary>
    public ModelContractException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>Options for model declaration generation and snapshot serialization.</summary>
public sealed record ModelContractOptions
{
    /// <summary>Default contract options.</summary>
    public static ModelContractOptions Default { get; } = new();

    /// <summary>JSON options used for both TypeScript names and snapshots.</summary>
    public JsonSerializerOptions Json { get; init; } = CreateDefaultJsonOptions();

    /// <summary>Whether enums use their JSON string representation.</summary>
    public bool EnumsAsStrings { get; init; }

    private static JsonSerializerOptions CreateDefaultJsonOptions()
        => new(JsonSerializerDefaults.Web)
        {
            // JavaScriptEncoder.Default escapes HTML-sensitive characters, including
            // '<', '>', '&' and the slash sequence used to close a script element.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
        };
}
