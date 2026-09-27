using System.Text;
using System.Text.Json;
using Okojo.JavaScript.Embedding;
using SvelteSharp.JavaScript;

namespace SvelteSharp.Engine.Okojo;

/// <summary>Okojo-backed JSON runtime factory.</summary>
public sealed class OkojoJsonRuntimeFactory : ICompilerJavaScriptRuntimeFactory, ISsrJavaScriptRuntimeFactory
{
    private readonly SemaphoreSlim _concurrency;

    /// <summary>Creates an Okojo factory with a concurrency limit.</summary>
    public OkojoJsonRuntimeFactory(JavaScriptExecutionLimits? limits = null)
    {
        _concurrency = new SemaphoreSlim((limits ?? JavaScriptExecutionLimits.Default).MaxConcurrentExecutions);
    }

    /// <inheritdoc />
    public SvelteJavaScriptEngine Engine => SvelteJavaScriptEngine.Okojo;

    /// <inheritdoc />
    public IJavaScriptJsonRuntime Create(JavaScriptExecutionContext context)
    {
        context.Limits.Validate();
        _concurrency.Wait(context.CancellationToken);
        return new OkojoJsonRuntime(context, _concurrency);
    }
}

/// <summary>One isolated Okojo runtime for a compiler or SSR operation.</summary>
public sealed class OkojoJsonRuntime : IJavaScriptJsonRuntime
{
    private readonly JavaScriptExecutionContext _context;
    private readonly SemaphoreSlim _concurrency;
    private int _disposed;

    internal OkojoJsonRuntime(JavaScriptExecutionContext context, SemaphoreSlim concurrency)
    {
        _context = context;
        _concurrency = concurrency;
    }

    /// <inheritdoc />
    public ValueTask<JsonDocument> EvaluateJsonAsync(
        string script,
        string resultExpression,
        JavaScriptExecutionContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        context.Limits.Validate();
        if (Encoding.UTF8.GetByteCount(script) > context.Limits.MaxInputBytes)
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Okojo,
                "input-limit",
                "The JavaScript input exceeded the configured limit.");
        }

        return new ValueTask<JsonDocument>(EvaluateBoundedAsync(script, resultExpression, context));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _concurrency.Release();
        }

        return ValueTask.CompletedTask;
    }

    private static async Task<JsonDocument> EvaluateAsync(
        string script,
        string resultExpression,
        JavaScriptExecutionContext context)
    {
        try
        {
            using var runtime = JsRuntime.Create();
            var realm = runtime.MainRealm;
            // Compiler jobs and synchronous SSR stay on Okojo's direct path.
            // Svelte's async render path is marked explicitly by the adapter and
            // is the only case that needs the embedding promise pump.
            var value = script.Contains("await SvelteSharpView.renderSnapshot(", StringComparison.Ordinal)
                ? await realm.EvaluateAsyncWithHostPump(
                    $"(async () => {{\n{script}\nreturn {resultExpression};\n}})()",
                    _ => ValueTask.CompletedTask,
                    context.CancellationToken)
                : EvaluateSynchronously(realm, script, resultExpression);
            var json = value.AsString();
            if (Encoding.UTF8.GetByteCount(json) > context.Limits.MaxOutputBytes)
            {
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Okojo,
                    "output-limit",
                    "The JavaScript output exceeded the configured limit.");
            }

            return JsonDocument.Parse(json);
        }
        catch (JavaScriptEngineException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Okojo,
                "evaluate",
                "Okojo failed to evaluate the requested script.",
                exception);
        }
    }

    private static async Task<JsonDocument> EvaluateBoundedAsync(
        string script,
        string resultExpression,
        JavaScriptExecutionContext context)
    {
        try
        {
            return await Task.Run(
                    () => EvaluateAsync(script, resultExpression, context),
                    context.CancellationToken)
                .WaitAsync(context.Limits.Timeout, context.CancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Okojo,
                "timeout",
                "Okojo exceeded the configured JavaScript execution timeout.",
                exception);
        }
    }

    private static global::Okojo.JavaScript.JsValue EvaluateSynchronously(
        global::Okojo.JavaScript.Execution.JsRealm realm,
        string script,
        string resultExpression)
    {
        realm.Evaluate(script);
        return realm.Evaluate(resultExpression);
    }
}
