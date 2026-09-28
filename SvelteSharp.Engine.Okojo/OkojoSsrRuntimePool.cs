using System.Collections.Concurrent;
using System.Text;
using Okojo.JavaScript;
using Okojo.JavaScript.Bytecode;
using Okojo.JavaScript.Embedding;
using Okojo.JavaScript.Execution;
using Okojo.JavaScript.Objects;
using SvelteSharp.JavaScript;
using SvelteSharp.Rendering;

namespace SvelteSharp.Engine.Okojo;

/// <summary>
/// Reuses Okojo runtimes for trusted, precompiled Svelte server bundles.
/// </summary>
/// <remarks>
/// The bundle is compiled and evaluated once per pooled runtime. Each request only supplies a
/// JSON snapshot to a prepared render script and reads the returned object directly. Okojo's
/// synchronous evaluator does not expose a cooperative timeout, so work is bounded on a worker
/// and a runtime that times out or fails is discarded instead of being returned to the pool.
/// This is a configuration-reuse optimization, not a security boundary; server bundles must be
/// trusted.
/// </remarks>
public sealed class OkojoSsrRuntimePool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EngineBucket> _buckets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly int _maxPooledEnginesPerBundle;
    private int _disposed;

    /// <summary>Creates a pooled Okojo SSR runtime collection.</summary>
    public OkojoSsrRuntimePool(
        JavaScriptExecutionLimits? limits = null,
        int maxPooledEnginesPerBundle = 4)
    {
        var effectiveLimits = limits ?? JavaScriptExecutionLimits.Default;
        effectiveLimits.Validate();
        if (maxPooledEnginesPerBundle <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPooledEnginesPerBundle));
        }

        _slots = new SemaphoreSlim(effectiveLimits.MaxConcurrentExecutions);
        _maxPooledEnginesPerBundle = maxPooledEnginesPerBundle;
    }

    /// <summary>Renders a prebuilt server bundle using a pooled Okojo runtime.</summary>
    internal async ValueTask<SvelteSsrOutput> RenderAsync(
        string bundleCode,
        string modelSnapshot,
        bool requiresAsyncRender,
        JavaScriptExecutionContext context)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(bundleCode);
        ArgumentNullException.ThrowIfNull(modelSnapshot);
        context.Limits.Validate();

        var bundleBytes = Encoding.UTF8.GetByteCount(bundleCode);
        if (bundleBytes > context.Limits.MaxInputBytes)
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Okojo,
                "input-limit",
                "The JavaScript input exceeded the configured limit.");
        }

        await _slots.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        EngineBucket? bucket = null;
        PooledEngine? engine = null;
        try
        {
            bucket = _buckets.GetOrAdd(
                bundleCode,
                _ => new EngineBucket(bundleCode, _maxPooledEnginesPerBundle));
            engine = bucket.Rent();
            return await engine.RenderAsync(modelSnapshot, bundleBytes, requiresAsyncRender, context).ConfigureAwait(false);
        }
        finally
        {
            if (bucket is not null && engine is not null)
            {
                bucket.Return(engine);
            }

            _slots.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            foreach (var bucket in _buckets.Values)
            {
                bucket.Dispose();
            }

            _buckets.Clear();
            _slots.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class EngineBucket
    {
        private readonly string _bundleCode;
        private readonly int _maxPooledEngines;
        private readonly ConcurrentBag<PooledEngine> _idle = [];
        private int _idleCount;

        public EngineBucket(string bundleCode, int maxPooledEngines)
        {
            _bundleCode = bundleCode;
            _maxPooledEngines = maxPooledEngines;
        }

        public PooledEngine Rent()
        {
            if (_idle.TryTake(out var existing))
            {
                Interlocked.Decrement(ref _idleCount);
                return existing;
            }

            return PooledEngine.Create(_bundleCode);
        }

        public void Return(PooledEngine engine)
        {
            if (!engine.IsReusable)
            {
                return;
            }

            var idleCount = Interlocked.Increment(ref _idleCount);
            if (idleCount <= _maxPooledEngines)
            {
                _idle.Add(engine);
                return;
            }

            Interlocked.Decrement(ref _idleCount);
            engine.Dispose();
        }

        public void Dispose()
        {
            while (_idle.TryTake(out var engine))
            {
                Interlocked.Decrement(ref _idleCount);
                engine.Dispose();
            }
        }
    }

    private sealed class PooledEngine : IDisposable
    {
        private const string ModelSnapshotGlobal = "__svelteSharpModelSnapshot";
        private const string ModelGlobal = "__svelteSharpModel";
        private readonly JsRuntime _runtime;
        private readonly JsRealm _realm;
        private readonly JsScript _parseModelScript;
        private readonly JsFunction _renderFunction;
        private readonly bool _usesObjectModel;
        private int _reusable = 1;
        private int _disposed;

        private PooledEngine(
            JsRuntime runtime,
            JsRealm realm,
            JsScript parseModelScript,
            JsFunction renderFunction,
            bool usesObjectModel)
        {
            _runtime = runtime;
            _realm = realm;
            _parseModelScript = parseModelScript;
            _renderFunction = renderFunction;
            _usesObjectModel = usesObjectModel;
        }

        public bool IsReusable => Volatile.Read(ref _reusable) != 0;

        public static PooledEngine Create(string bundleCode)
        {
            var runtime = JsRuntime.Create();
            try
            {
                var realm = runtime.MainRealm;
                var bundle = realm.CompileScript(bundleCode, "svelte-server.js");
                realm.Execute(bundle, pumpJobsAfterRun: false);

                var view = realm.Global["SvelteSharpView"].AsObject();
                var renderModel = view["renderModel"];
                var usesObjectModel = !renderModel.IsUndefined;
                var renderValue = usesObjectModel ? renderModel : view["renderSnapshot"];
                var renderFunction = renderValue.AsObject() as JsFunction
                    ?? throw new JavaScriptEngineException(
                        SvelteJavaScriptEngine.Okojo,
                        "ssr-render",
                        "Svelte server bundle did not expose a callable render function.");
                var parseModelScript = realm.CompileScript(
                    $"globalThis.{ModelGlobal} = JSON.parse(globalThis.{ModelSnapshotGlobal});",
                    "svelte-parse-model.js");
                return new PooledEngine(runtime, realm, parseModelScript, renderFunction, usesObjectModel);
            }
            catch
            {
                runtime.Dispose();
                throw;
            }
        }

        public async ValueTask<SvelteSsrOutput> RenderAsync(
            string modelSnapshot,
            long bundleBytes,
            bool requiresAsyncRender,
            JavaScriptExecutionContext context)
        {
            if (bundleBytes + Encoding.UTF8.GetByteCount(modelSnapshot) > context.Limits.MaxInputBytes)
            {
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Okojo,
                    "input-limit",
                    "The JavaScript input exceeded the configured limit.");
            }

            Task<SvelteSsrOutput>? renderTask = null;
            try
            {
                renderTask = Task.Run(
                    () => RenderCoreAsync(
                        modelSnapshot,
                        requiresAsyncRender,
                        context.Limits.MaxOutputBytes,
                        context.CancellationToken),
                    CancellationToken.None);
                return await renderTask.WaitAsync(context.Limits.Timeout, context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                Abandon(renderTask);
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Okojo,
                    "timeout",
                    "Okojo exceeded the configured JavaScript execution timeout.",
                    exception);
            }
            catch (JavaScriptEngineException)
            {
                Abandon(renderTask);
                throw;
            }
            catch (OperationCanceledException)
            {
                Abandon(renderTask);
                throw;
            }
            catch (Exception exception)
            {
                Abandon(renderTask);
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Okojo,
                    "evaluate",
                    "Okojo failed to evaluate the requested script.",
                    exception);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _runtime.Dispose();
            }
        }

        private async Task<SvelteSsrOutput> RenderCoreAsync(
            string modelSnapshot,
            bool requiresAsyncRender,
            long maxOutputBytes,
            CancellationToken cancellationToken)
        {
            try
            {
                _realm.Global[ModelSnapshotGlobal] = JsValue.FromString(modelSnapshot);
                JsValue model;
                if (_usesObjectModel)
                {
                    _realm.Execute(_parseModelScript, pumpJobsAfterRun: false);
                    model = _realm.Global[ModelGlobal];
                }
                else
                {
                    model = JsValue.FromString(modelSnapshot);
                }

                var rendered = _renderFunction.Call(_realm, JsValue.Undefined, [model]);
                if (requiresAsyncRender)
                {
                    rendered = await _realm.ToPumpedValueTask(rendered, cancellationToken).ConfigureAwait(false);
                }

                return ReadOutput(rendered, maxOutputBytes);
            }
            finally
            {
                _realm.Global[ModelSnapshotGlobal] = JsValue.Undefined;
                _realm.Global[ModelGlobal] = JsValue.Undefined;
            }
        }

        private void Abandon(Task? renderTask)
        {
            if (Interlocked.Exchange(ref _reusable, 0) == 0)
            {
                return;
            }

            if (renderTask is null)
            {
                Dispose();
                return;
            }

            _ = renderTask.ContinueWith(
                _ => Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static SvelteSsrOutput ReadOutput(JsValue value, long maxOutputBytes)
        {
            try
            {
                var rendered = value.AsObject();
                var bodyValue = rendered["body"];
                var body = bodyValue.IsString
                    ? bodyValue.AsString()
                    : rendered["html"].IsString
                        ? rendered["html"].AsString()
                        : string.Empty;
                var headValue = rendered["head"];
                var head = headValue.IsString ? headValue.AsString() : string.Empty;
                if (Encoding.UTF8.GetByteCount(body) + Encoding.UTF8.GetByteCount(head) > maxOutputBytes)
                {
                    throw new JavaScriptEngineException(
                        SvelteJavaScriptEngine.Okojo,
                        "output-limit",
                        "The JavaScript output exceeded the configured limit.");
                }

                return new SvelteSsrOutput(body, head);
            }
            catch (JavaScriptEngineException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Okojo,
                    "ssr-render",
                    "Okojo Svelte server render returned a non-object value.",
                    exception);
            }
        }
    }
}
