using System.Text.Json;

namespace SvelteSharp.Models;

/// <summary>Serializes models once for the server and browser sides of a view.</summary>
public static class ModelSnapshotSerializer
{
    /// <summary>Serializes a model using HTML-safe JSON defaults.</summary>
    public static string Serialize(object? model, Type modelType, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        var jsonOptions = options is null ? CreateDefaultOptions() : CloneAndValidate(options);

        try
        {
            return JsonSerializer.Serialize(model, modelType, jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new ModelContractException(
                $"The model of type '{modelType}' cannot be serialized as a SvelteSharp snapshot.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new ModelContractException(
                $"The model of type '{modelType}' uses a JSON shape unsupported by SvelteSharp.", exception);
        }
    }

    /// <summary>Serializes using the runtime type, preserving the same safe JSON rules.</summary>
    public static string Serialize(object? model, JsonSerializerOptions? options = null)
        => Serialize(model, model?.GetType() ?? typeof(object), options);

    private static JsonSerializerOptions CreateDefaultOptions()
        => new(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
            ReferenceHandler = null
        };

    private static JsonSerializerOptions CloneAndValidate(JsonSerializerOptions options)
    {
        var clone = new JsonSerializerOptions(options)
        {
            Encoder = options.Encoder ?? System.Text.Encodings.Web.JavaScriptEncoder.Default
        };

        if (clone.ReferenceHandler is not null)
        {
            throw new ModelContractException(
                "Reference-preserving JSON is not supported for SvelteSharp snapshots; circular models must be rejected.");
        }

        return clone;
    }
}
