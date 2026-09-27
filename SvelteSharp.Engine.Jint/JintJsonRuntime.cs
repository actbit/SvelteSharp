using System.Text;
using System.Text.Json;
using Jint;
using SvelteSharp.JavaScript;

namespace SvelteSharp.Engine.Jint;

/// <summary>Jint-backed JSON runtime factory.</summary>
public sealed class JintJsonRuntimeFactory : ICompilerJavaScriptRuntimeFactory, ISsrJavaScriptRuntimeFactory
{
    private readonly SemaphoreSlim _concurrency;

    /// <summary>Creates a Jint factory with a concurrency limit.</summary>
    public JintJsonRuntimeFactory(JavaScriptExecutionLimits? limits = null)
    {
        _concurrency = new SemaphoreSlim((limits ?? JavaScriptExecutionLimits.Default).MaxConcurrentExecutions);
    }

    /// <inheritdoc />
    public SvelteJavaScriptEngine Engine => SvelteJavaScriptEngine.Jint;

    /// <inheritdoc />
    public IJavaScriptJsonRuntime Create(JavaScriptExecutionContext context)
    {
        context.Limits.Validate();
        _concurrency.Wait(context.CancellationToken);
        return new JintJsonRuntime(context, _concurrency);
    }
}

/// <summary>One isolated Jint realm for a compiler or SSR operation.</summary>
public sealed class JintJsonRuntime : IJavaScriptJsonRuntime
{
    private readonly global::Jint.Engine _engine;
    private readonly JavaScriptExecutionContext _context;
    private readonly SemaphoreSlim _concurrency;
    private int _disposed;

    internal JintJsonRuntime(JavaScriptExecutionContext context, SemaphoreSlim concurrency)
    {
        _context = context;
        _concurrency = concurrency;
        _engine = new global::Jint.Engine(options =>
        {
            options.TimeoutInterval(context.Limits.Timeout);
            options.LimitRecursion(512);
        });
    }

    /// <inheritdoc />
    public ValueTask<JsonDocument> EvaluateJsonAsync(
        string script,
        string resultExpression,
        JavaScriptExecutionContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(resultExpression);
        context.Limits.Validate();
        if (Encoding.UTF8.GetByteCount(script) > context.Limits.MaxInputBytes)
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Jint,
                "input-limit",
                "The JavaScript input exceeded the configured limit.");
        }

        return new ValueTask<JsonDocument>(Task.Run(() => EvaluateAsync(script, resultExpression, context), context.CancellationToken));
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

    private async Task<JsonDocument> EvaluateAsync(
        string script,
        string resultExpression,
        JavaScriptExecutionContext context)
    {
        try
        {
            // Always enter Jint's asynchronous evaluation path. Svelte's server
            // render API is PromiseLike even when a particular component happens
            // to render synchronously, and EvaluateAsync drains that promise/job
            // queue instead of accidentally serializing the promise object.
            var wrappedScript = $"(async () => {{\n{script}\nreturn {resultExpression};\n}})()";
            var value = await _engine.EvaluateAsync(wrappedScript, cancellationToken: context.CancellationToken);
            var json = value.IsString() ? value.AsString() : value.ToString();
            if (Encoding.UTF8.GetByteCount(json) > context.Limits.MaxOutputBytes)
            {
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Jint,
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
                SvelteJavaScriptEngine.Jint,
                "evaluate",
                "Jint failed to evaluate the requested script.",
                exception);
        }
    }
}
