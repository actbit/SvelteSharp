using System.Collections.Concurrent;
using System.Text;
using Acornima.Ast;
using Jint;
using Jint.Native;
using JsonParser = Jint.Native.Json.JsonParser;
using Jint.Native.Object;
using SvelteSharp.JavaScript;
using SvelteSharp.Rendering;

namespace SvelteSharp.Jint;

/// <summary>
/// Reuses Jint engines for trusted, precompiled Svelte server bundles.
/// </summary>
/// <remarks>
/// The bundle is parsed and evaluated once per pooled engine. Each render only evaluates the
/// request-specific model and restores the engine's global surface before it is returned to the
/// pool. This is a configuration-reuse optimization, not a security boundary; the Svelte bundle
/// must be trusted, while request data crosses the boundary as JSON.
/// </remarks>
public sealed class JintSsrRuntimePool : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, EngineBucket> _buckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _bundleByteCounts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots;
    private readonly int _maxPooledEnginesPerBundle;
    private int _disposed;

    /// <summary>Creates a pooled SSR runtime collection.</summary>
    public JintSsrRuntimePool(
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

    /// <summary>Renders a prebuilt server bundle using a pooled Jint engine.</summary>
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

        // The bundle is a large, stable string. Its UTF-8 length is computed once per unique
        // bundle instead of on every request.
        var bundleBytes = _bundleByteCounts.GetOrAdd(bundleCode, static code => Encoding.UTF8.GetByteCount(code));
        if (bundleBytes > context.Limits.MaxInputBytes)
        {
            throw new JavaScriptEngineException(
                SvelteJavaScriptEngine.Jint,
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
                _ => new EngineBucket(bundleCode, context.Limits, _maxPooledEnginesPerBundle));
            engine = bucket.Rent();
            return await engine.RenderAsync(modelSnapshot, bundleBytes, requiresAsyncRender, context).ConfigureAwait(false);
        }
        catch
        {
            // A failed or timed out render may leave the engine in an unknown state, so discard it
            // instead of returning it to the pool.
            engine?.Abandon();
            throw;
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
            _bundleByteCounts.Clear();
            _slots.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private sealed class EngineBucket
    {
        private readonly Prepared<Script> _preparedBundle;
        private readonly TimeSpan _timeout;
        private readonly int _maxPooledEngines;
        private readonly ConcurrentBag<PooledEngine> _idle = [];
        private int _idleCount;

        public EngineBucket(
            string bundleCode,
            JavaScriptExecutionLimits limits,
            int maxPooledEngines)
        {
            _preparedBundle = global::Jint.Engine.PrepareScript(bundleCode, source: "svelte-server.js");
            _timeout = limits.Timeout;
            _maxPooledEngines = maxPooledEngines;
        }

        public PooledEngine Rent()
        {
            if (_idle.TryTake(out var existing))
            {
                Interlocked.Decrement(ref _idleCount);
                return existing;
            }

            return PooledEngine.Create(_preparedBundle, _timeout);
        }

        public void Return(PooledEngine engine)
        {
            if (!engine.IsReusable)
            {
                return;
            }

            try
            {
                engine.RestoreGlobals();
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                engine.Dispose();
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
        private static readonly Prepared<Script> SyncRenderScript = global::Jint.Engine.PrepareScript(
            $"SvelteSharpView.renderSnapshot(globalThis.{ModelSnapshotGlobal})",
            source: "svelte-render-sync.js");
        private static readonly Prepared<Script> AsyncRenderScript = global::Jint.Engine.PrepareScript(
            $"(async () => await SvelteSharpView.renderSnapshot(globalThis.{ModelSnapshotGlobal}))()",
            source: "svelte-render-async.js");
        private static readonly Prepared<Script> AsyncModelRenderScript = global::Jint.Engine.PrepareScript(
            $"(async () => await SvelteSharpView.renderModel(globalThis.{ModelGlobal}))()",
            source: "svelte-render-model-async.js");
        private readonly GlobalSnapshot _cleanGlobals;
        private readonly JsValue _renderModel;
        private readonly bool _supportsObjectModel;
        private readonly JsonParser _jsonParser;
        private readonly JsValue[] _renderArguments = new JsValue[1];
        private int _reusable = 1;
        private int _disposed;

        private PooledEngine(
            global::Jint.Engine engine,
            GlobalSnapshot cleanGlobals,
            JsValue renderModel,
            bool supportsObjectModel,
            JsonParser jsonParser)
        {
            Engine = engine;
            _cleanGlobals = cleanGlobals;
            _renderModel = renderModel;
            _supportsObjectModel = supportsObjectModel;
            _jsonParser = jsonParser;
        }

        public global::Jint.Engine Engine { get; }

        public static PooledEngine Create(Prepared<Script> preparedBundle, TimeSpan timeout)
        {
            var engine = new global::Jint.Engine(options =>
            {
                options.TimeoutInterval(timeout);
                options.LimitRecursion(512);
            });
            try
            {
                engine.Execute(preparedBundle);
                var view = engine.GetValue("SvelteSharpView").AsObject();
                var renderModel = view.Get("renderModel");
                return new PooledEngine(
                    engine,
                    engine.Advanced.CaptureGlobalSnapshot(),
                    renderModel.IsUndefined() ? view.Get("renderSnapshot") : renderModel,
                    !renderModel.IsUndefined(),
                    new JsonParser(engine));
            }
            catch
            {
                engine.Dispose();
                throw;
            }
        }

        public async ValueTask<SvelteSsrOutput> RenderAsync(
            string modelSnapshot,
            long bundleBytes,
            bool requiresAsyncRender,
            JavaScriptExecutionContext context)
        {
            try
            {
                if (ExceedsUtf8Limit(modelSnapshot, context.Limits.MaxInputBytes - bundleBytes))
                {
                    throw new JavaScriptEngineException(
                        SvelteJavaScriptEngine.Jint,
                        "input-limit",
                        "The JavaScript input exceeded the configured limit.");
                }

                JsValue value;
                if (_supportsObjectModel)
                {
                    var model = _jsonParser.Parse(modelSnapshot);
                    if (requiresAsyncRender)
                    {
                        Engine.SetValue(ModelGlobal, model);
                        value = await Engine.EvaluateAsync(AsyncModelRenderScript, context.CancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _renderArguments[0] = model;
                        value = Engine.Call(_renderModel, _renderArguments);
                    }
                }
                else
                {
                    Engine.SetValue(ModelSnapshotGlobal, modelSnapshot);
                    value = requiresAsyncRender
                        ? await Engine.EvaluateAsync(AsyncRenderScript, context.CancellationToken).ConfigureAwait(false)
                        : Engine.Evaluate(SyncRenderScript);
                }
                return ReadOutput(value, context.Limits.MaxOutputBytes);
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

        public bool IsReusable => Volatile.Read(ref _reusable) != 0;

        public void RestoreGlobals() => Engine.Advanced.RestoreGlobalSnapshot(_cleanGlobals);

        /// <summary>Marks the engine as unusable and disposes it after a failed render.</summary>
        public void Abandon()
        {
            if (Interlocked.Exchange(ref _reusable, 0) == 0)
            {
                return;
            }

            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Engine.Dispose();
            }
        }

        private static SvelteSsrOutput ReadOutput(JsValue value, long maxOutputBytes)
        {
            ObjectInstance rendered;
            try
            {
                rendered = value.AsObject();
            }
            catch (Exception exception)
            {
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Jint,
                    "ssr-render",
                    "Svelte server render returned a non-object value.",
                    exception);
            }

            var bodyValue = rendered.Get("body");
            var body = bodyValue.IsString()
                ? bodyValue.AsString()
                : rendered.Get("html").IsString()
                    ? rendered.Get("html").AsString()
                    : string.Empty;
            var headValue = rendered.Get("head");
            var head = headValue.IsString() ? headValue.AsString() : string.Empty;
            if (ExceedsUtf8Limit(body, head, maxOutputBytes))
            {
                throw new JavaScriptEngineException(
                    SvelteJavaScriptEngine.Jint,
                    "output-limit",
                    "The JavaScript output exceeded the configured limit.");
            }

            return new SvelteSsrOutput(body, head);
        }

        // A UTF-8 string is between one and three bytes per char, so most requests can be
        // validated against the limit without scanning the whole string.
        private static bool ExceedsUtf8Limit(string text, long remaining)
        {
            if (text.Length > remaining)
            {
                return true;
            }

            if ((long)text.Length * 3 <= remaining)
            {
                return false;
            }

            return Encoding.UTF8.GetByteCount(text) > remaining;
        }

        private static bool ExceedsUtf8Limit(string first, string second, long limit)
        {
            var chars = (long)first.Length + second.Length;
            if (chars > limit)
            {
                return true;
            }

            if (chars * 3 <= limit)
            {
                return false;
            }

            return Encoding.UTF8.GetByteCount(first) + Encoding.UTF8.GetByteCount(second) > limit;
        }
    }
}
