namespace SvelteSharp;

/// <summary>Describes how a Svelte view is rendered.</summary>
public enum SvelteRenderMode
{
    /// <summary>Render only on the server.</summary>
    Server,

    /// <summary>Render only in the browser.</summary>
    Client,

    /// <summary>Render on the server and hydrate in the browser.</summary>
    Hybrid
}

/// <summary>Identifies one of the supported embedded JavaScript engines.</summary>
public enum SvelteJavaScriptEngine
{
    /// <summary>Jint adapter.</summary>
    Jint,

    /// <summary>Okojo adapter.</summary>
    Okojo
}
