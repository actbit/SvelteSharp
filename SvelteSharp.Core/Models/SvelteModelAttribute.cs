using System.Reflection;

namespace SvelteSharp.Models;

/// <summary>Marks a CLR model as part of the explicit SvelteSharp type contract.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SvelteModelAttribute : Attribute
{
    /// <summary>Optional exported TypeScript name.</summary>
    public string? Name { get; init; }
}

/// <summary>Registry of models intentionally exposed to Svelte views.</summary>
public sealed class SvelteModelRegistry
{
    private readonly HashSet<Type> _types = [];
    private readonly object _gate = new();

    /// <summary>Registers a model type.</summary>
    public void Register<T>() => Register(typeof(T));

    /// <summary>Registers a model type.</summary>
    public void Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate)
        {
            _types.Add(type);
        }
    }

    /// <summary>
    /// Registers every type in an assembly that is explicitly marked with
    /// <see cref="SvelteModelAttribute"/>.
    /// </summary>
    /// <remarks>
    /// The attribute is the build-time contract. <c>AddModel&lt;T&gt;</c> remains
    /// available for runtime-only registration, while attributed models can
    /// be discovered by both the ASP.NET host and the NuGet/MSBuild build tool
    /// without executing application code.
    /// </remarks>
    public int RegisterAttributedModels(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var discovered = 0;
        foreach (var type in GetLoadableTypes(assembly)
            .Where(type => type is { IsClass: true } or { IsValueType: true })
            .Where(type => !type.IsGenericTypeDefinition)
            .Where(type => type.GetCustomAttribute<SvelteModelAttribute>() is not null))
        {
            Register(type);
            discovered++;
        }

        return discovered;
    }

    /// <summary>Returns a stable snapshot of registered model types.</summary>
    public IReadOnlyList<Type> GetTypes()
    {
        lock (_gate)
        {
            return _types.OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Generates declarations for all registered models.</summary>
    public IReadOnlyDictionary<Type, string> GenerateDeclarations(ModelContractOptions? options = null)
    {
        var generator = new TypeScriptDeclarationGenerator(options);
        return GetTypes().ToDictionary(type => type, type => generator.Generate(type));
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).Cast<Type>();
        }
    }
}
