using System.Text.Json;

namespace SvelteSharp.Models;

/// <summary>Serializes models once for the server and browser sides of a view.</summary>
public static class ModelSnapshotSerializer
{
    private static readonly JsonSerializerOptions DefaultOptions = CreateDefaultOptions();

    /// <summary>Serializes a model using HTML-safe JSON defaults.</summary>
    public static string Serialize(object? model, Type modelType, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelType);
        var jsonOptions = options is null ? DefaultOptions : CloneAndValidate(options);

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
        if (options.ReferenceHandler is not null)
        {
            throw new ModelContractException(
                "Reference-preserving JSON is not supported for SvelteSharp snapshots; circular models must be rejected.");
        }

        // JsonSerializerOptions is made immutable by System.Text.Json on first use. Reusing the
        // configured instance avoids cloning the options on every SSR request and matches the
        // lifetime used by the rest of the ASP.NET Core JSON pipeline. Only options without an
        // explicit encoder need a clone to preserve SvelteSharp's HTML-safe default.
        return options.Encoder is null
            ? new JsonSerializerOptions(options)
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
            }
            : options;
    }
}
