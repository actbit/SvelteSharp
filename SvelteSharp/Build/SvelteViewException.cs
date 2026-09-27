namespace SvelteSharp.Build;

/// <summary>A diagnostic error with view and build context.</summary>
public sealed class SvelteViewException : Exception
{
    /// <summary>Creates a view diagnostic.</summary>
    public SvelteViewException(string viewName, string? buildId, string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        ViewName = viewName;
        BuildId = buildId;
        Code = code;
    }

    /// <summary>Logical view name.</summary>
    public string ViewName { get; }

    /// <summary>Build ID, when known.</summary>
    public string? BuildId { get; }

    /// <summary>Stable diagnostic code.</summary>
    public string Code { get; }
}
