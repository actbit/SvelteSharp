using System.Text.Json;

namespace SvelteSharp.JavaScript;

/// <summary>Limits applied to an embedded JavaScript execution.</summary>
public sealed record JavaScriptExecutionLimits
{
    /// <summary>Default limits for an SSR request.</summary>
    public static JavaScriptExecutionLimits Default { get; } = new();

    /// <summary>Maximum execution duration.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum UTF-8 input accepted by a module loader.</summary>
    public long MaxInputBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Maximum UTF-8 output emitted by an execution.</summary>
    public long MaxOutputBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>Maximum number of concurrent request runtimes.</summary>
    public int MaxConcurrentExecutions { get; init; } = 32;

    /// <summary>Validates all configured limits.</summary>
    public void Validate()
    {
        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), "The JavaScript timeout must be positive.");
        }

        if (MaxInputBytes <= 0 || MaxOutputBytes <= 0 || MaxConcurrentExecutions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxInputBytes), "JavaScript limits must be positive.");
        }
    }
}

/// <summary>Configuration passed to an engine for one isolated execution.</summary>
public sealed record JavaScriptExecutionContext(
    JavaScriptExecutionLimits Limits,
    CancellationToken CancellationToken = default);

/// <summary>Engine-neutral representation of a JavaScript module.</summary>
public sealed record JavaScriptModule(string Specifier, string Source);

/// <summary>Executes an already loaded module without exposing engine-specific values.</summary>
public interface IJavaScriptEngine : IAsyncDisposable
{
    /// <summary>Evaluates a module and returns an engine-neutral value.</summary>
    ValueTask<object?> EvaluateModuleAsync(
        JavaScriptModule module,
        JavaScriptExecutionContext context);
}

/// <summary>Creates isolated JavaScript runtimes.</summary>
public interface IJavaScriptEngineFactory
{
    /// <summary>The engine selected by this factory.</summary>
    SvelteJavaScriptEngine Engine { get; }

    /// <summary>Creates a new runtime. Runtimes must not be shared between requests.</summary>
    IJavaScriptEngine Create(JavaScriptExecutionContext context);
}

/// <summary>Factory contract used only while compiling Svelte source.</summary>
public interface ICompilerJavaScriptHostFactory : IJavaScriptEngineFactory
{
}

/// <summary>Factory contract used only while rendering SSR requests.</summary>
public interface ISsrJavaScriptHostFactory : IJavaScriptEngineFactory
{
}

/// <summary>A request-scoped engine that exposes only JSON across the host boundary.</summary>
public interface IJavaScriptJsonRuntime : IAsyncDisposable
{
    /// <summary>Executes a script and evaluates a JSON-producing expression.</summary>
    ValueTask<JsonDocument> EvaluateJsonAsync(
        string script,
        string resultExpression,
        JavaScriptExecutionContext context);
}

/// <summary>Creates isolated JSON runtimes for one host responsibility.</summary>
public interface IJavaScriptJsonRuntimeFactory
{
    /// <summary>The selected engine.</summary>
    SvelteJavaScriptEngine Engine { get; }

    /// <summary>Creates a runtime that must not be shared across requests.</summary>
    IJavaScriptJsonRuntime Create(JavaScriptExecutionContext context);
}

/// <summary>Compiler-only JSON runtime factory.</summary>
public interface ICompilerJavaScriptRuntimeFactory : IJavaScriptJsonRuntimeFactory
{
}

/// <summary>SSR-only JSON runtime factory.</summary>
public interface ISsrJavaScriptRuntimeFactory : IJavaScriptJsonRuntimeFactory
{
}

/// <summary>Bridges engine-specific PromiseLike values into cancellable .NET tasks.</summary>
public interface IJavaScriptPromiseBridge
{
    /// <summary>Awaits a PromiseLike value or propagates its rejection.</summary>
    ValueTask<object?> AwaitAsync(object promiseLike, CancellationToken cancellationToken = default);
}

/// <summary>Supplies modules to a compiler or SSR runtime.</summary>
public interface IJavaScriptModuleLoader
{
    /// <summary>Loads a module by its build-local specifier.</summary>
    ValueTask<JavaScriptModule> LoadAsync(string specifier, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a selected engine cannot execute a requested operation.</summary>
public sealed class JavaScriptEngineException : Exception
{
    /// <summary>Creates an engine diagnostic exception.</summary>
    public JavaScriptEngineException(
        SvelteJavaScriptEngine engine,
        string operation,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Engine = engine;
        Operation = operation;
    }

    /// <summary>The engine that produced the error.</summary>
    public SvelteJavaScriptEngine Engine { get; }

    /// <summary>The operation that failed.</summary>
    public string Operation { get; }
}

/// <summary>A factory that fails explicitly instead of silently falling back to another engine.</summary>
public class UnavailableJavaScriptEngineFactory : IJavaScriptEngineFactory
{
    private readonly string _reason;

    /// <summary>Creates an unavailable adapter.</summary>
    public UnavailableJavaScriptEngineFactory(SvelteJavaScriptEngine engine, string reason)
    {
        Engine = engine;
        _reason = string.IsNullOrWhiteSpace(reason) ? "The adapter is not installed." : reason;
    }

    /// <inheritdoc />
    public SvelteJavaScriptEngine Engine { get; }

    /// <inheritdoc />
    public IJavaScriptEngine Create(JavaScriptExecutionContext context)
        => throw new JavaScriptEngineException(
            Engine,
            "create-runtime",
            $"The {Engine} JavaScript adapter is unavailable. {_reason}");
}
