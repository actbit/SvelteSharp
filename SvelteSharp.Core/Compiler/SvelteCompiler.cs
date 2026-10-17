using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SvelteSharp.JavaScript;

namespace SvelteSharp.Compiler;

/// <summary>Executes the pinned Svelte 5 compiler bundle through the selected JS engine.</summary>
public sealed class SvelteCompiler : ISvelteCompiler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
    };

    private readonly ICompilerJavaScriptRuntimeFactory _runtimeFactory;
    private readonly ISvelteModuleSourceProvider? _moduleSourceProvider;
    private readonly string _compilerBundle;
    private static readonly Regex ModuleImport = new(
        "[\\\"'](\\.\\.?/[^\\\"']+\\.svelte\\.ts)[\\\"']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Creates a compiler backed by an engine adapter.</summary>
    public SvelteCompiler(
        ICompilerJavaScriptRuntimeFactory runtimeFactory,
        ISvelteModuleSourceProvider? moduleSourceProvider = null,
        SvelteToolchain? toolchain = null)
    {
        _runtimeFactory = runtimeFactory;
        _moduleSourceProvider = moduleSourceProvider;
        _compilerBundle = (toolchain ?? SvelteToolchain.Resolve()).LoadCompilerBundle();
    }

    /// <inheritdoc />
    public async ValueTask<SvelteCompilationResult> CompileAsync(
        SvelteSourceFile source,
        SvelteCompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        source.Validate();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (_runtimeFactory.Engine != options.Engine)
        {
            throw new JavaScriptEngineException(
                _runtimeFactory.Engine,
                "compile",
                $"The selected compiler engine is '{options.Engine}', but the registered adapter is '{_runtimeFactory.Engine}'.");
        }

        var compilerOptions = new Dictionary<string, object?>
        {
            ["filename"] = source.PhysicalPath ?? source.ViewName + (source.IsModule ? ".svelte.ts" : ".svelte"),
            ["dev"] = options.Dev,
            ["css"] = options.ExtractCss ? "external" : "injected",
            ["experimental"] = new Dictionary<string, object?> { ["async"] = options.ExperimentalAsync }
        };
        var modules = await CollectModulesAsync(source, cancellationToken);
        var requestWithOutputs = new
        {
            source = source.Source,
            module = source.IsModule,
            generateServer = options.GenerateServer,
            generateClient = options.GenerateClient,
            options = compilerOptions,
            modules
        };
        var script = BuildCompileScript(requestWithOutputs);
        var limits = JavaScriptExecutionLimits.Default with
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxInputBytes = 16 * 1024 * 1024,
            MaxOutputBytes = 32 * 1024 * 1024
        };

        await using var runtime = _runtimeFactory.Create(new JavaScriptExecutionContext(limits, cancellationToken));
        using var document = await runtime.EvaluateJsonAsync(script, "JSON.stringify(globalThis.__svelteSharpResult)", new JavaScriptExecutionContext(limits, cancellationToken));
        return ParseResult(source, options, document.RootElement);
    }

    private async ValueTask<IReadOnlyList<ModuleCompileRequest>> CollectModulesAsync(
        SvelteSourceFile root,
        CancellationToken cancellationToken)
    {
        if (_moduleSourceProvider is null)
        {
            if (ModuleImport.IsMatch(root.Source))
            {
                throw new SvelteCompilationException(
                    root.ViewName,
                    [new SvelteCompilerDiagnostic(
                        "svelte_module_provider_missing",
                        "A .svelte.ts import was found, but no module source provider is registered.",
                        SvelteDiagnosticSeverity.Error)]);
            }

            return [];
        }

        var rootDirectory = Path.GetDirectoryName(root.ViewName.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var queue = new Queue<SvelteSourceFile>([root]);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var modules = new List<ModuleCompileRequest>();
        while (queue.Count > 0)
        {
            var importer = queue.Dequeue();
            foreach (Match match in ModuleImport.Matches(importer.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var specifier = match.Groups[1].Value;
                var module = await _moduleSourceProvider.GetAsync(importer.ViewName, specifier, cancellationToken);
                if (module is null)
                {
                    throw new SvelteCompilationException(
                        root.ViewName,
                        [new SvelteCompilerDiagnostic(
                            "svelte_module_not_found",
                            $"The Svelte module '{specifier}' imported by '{importer.ViewName}' was not found.",
                            SvelteDiagnosticSeverity.Error)]);
                }

                if (!visited.Add(module.ViewName))
                {
                    continue;
                }

                var modulePath = GetModulePath(rootDirectory, module.ViewName);
                modules.Add(new ModuleCompileRequest(modulePath, module.ViewName, module.Source));
                queue.Enqueue(module);
            }
        }

        return modules;
    }

    private static string GetModulePath(string rootDirectory, string moduleName)
    {
        var normalizedRoot = rootDirectory.Replace('\\', '/').Trim('/');
        var normalizedModule = moduleName.Replace('\\', '/').Trim('/');
        var prefix = string.IsNullOrEmpty(normalizedRoot) ? string.Empty : normalizedRoot + "/";
        if (!normalizedModule.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new SvelteCompilationException(
                moduleName,
                [new SvelteCompilerDiagnostic(
                    "svelte_module_outside_root",
                    $"The Svelte module '{moduleName}' is outside the view root.",
                    SvelteDiagnosticSeverity.Error)]);
        }

        return normalizedModule[prefix.Length..];
    }

    private string BuildCompileScript(object request)
    {
        var requestJson = JsonSerializer.Serialize(request, Json);
        return $$"""
            {{_compilerBundle}}
            const __svelteSharpRequest = {{requestJson}};
            globalThis.__svelteSharpResult = null;
            try {
              const __compile = (generate) => {
                const __options = { ...__svelteSharpRequest.options, generate };
                return __svelteSharpRequest.module
                  ? svelte.compileModule(__svelteSharpRequest.source, __options)
                  : svelte.compile(__svelteSharpRequest.source, __options);
              };
              const __modules = {};
              const __moduleWarnings = [];
              for (const __module of __svelteSharpRequest.modules ?? []) {
                const __compiledModule = svelte.compileModule(__module.source, {
                  ...__svelteSharpRequest.options,
                  filename: __module.filename
                });
                __modules[__module.path] = {
                  code: __compiledModule.js?.code ?? '',
                  map: __compiledModule.js?.map ?? null
                };
                for (const __warning of __compiledModule.warnings ?? []) {
                  __moduleWarnings.push(__warning);
                }
              }
              const __server = __svelteSharpRequest.generateServer ? __compile('server') : null;
              const __client = __svelteSharpRequest.generateClient ? __compile('client') : null;
              globalThis.__svelteSharpResult = {
                ok: true,
                version: svelte.VERSION,
                server: __server?.js ? { code: __server.js.code, map: __server.js.map ?? null } : null,
                client: __client?.js ? { code: __client.js.code, map: __client.js.map ?? null } : null,
                css: (__server?.css ?? __client?.css) ? { code: (__server?.css ?? __client?.css).code, map: (__server?.css ?? __client?.css).map ?? null } : null,
                modules: __modules,
                warnings: [...(__server?.warnings ?? []), ...(__client?.warnings ?? []), ...__moduleWarnings].map((warning) => ({
                  code: warning.code ?? 'svelte_warning',
                  message: warning.message ?? String(warning),
                  start: warning.start ?? null,
                  end: warning.end ?? null
                }))
              };
            } catch (__error) {
              globalThis.__svelteSharpResult = {
                ok: false,
                error: {
                  code: __error?.code ?? 'svelte_compile_error',
                  message: __error?.message ?? String(__error),
                  start: __error?.start ?? null,
                  end: __error?.end ?? null
                }
              };
            }
            """;
    }

    private static SvelteCompilationResult ParseResult(
        SvelteSourceFile source,
        SvelteCompilerOptions options,
        JsonElement result)
    {
        var diagnostics = new List<SvelteCompilerDiagnostic>();
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Source))).ToLowerInvariant();
        if (!result.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = result.GetProperty("error");
            diagnostics.Add(ToDiagnostic(error, SvelteDiagnosticSeverity.Error));
            return Failed(source, options, sourceHash, diagnostics);
        }

        foreach (var warning in result.GetProperty("warnings").EnumerateArray())
        {
            diagnostics.Add(ToDiagnostic(warning, SvelteDiagnosticSeverity.Warning));
        }

        var server = result.GetProperty("server");
        var client = result.GetProperty("client");
        var serverGraph = options.GenerateServer && server.ValueKind != JsonValueKind.Null
            ? server.GetProperty("code").GetString() ?? string.Empty
            : string.Empty;
        var clientGraph = options.GenerateClient && client.ValueKind != JsonValueKind.Null
            ? client.GetProperty("code").GetString() ?? string.Empty
            : string.Empty;
        var css = result.TryGetProperty("css", out var cssElement) && cssElement.ValueKind != JsonValueKind.Null
            ? cssElement.GetProperty("code").GetString() ?? string.Empty
            : string.Empty;
        var serverSourceMap = server.ValueKind != JsonValueKind.Null && server.TryGetProperty("map", out var serverMap) && serverMap.ValueKind != JsonValueKind.Null
            ? serverMap.GetRawText()
            : null;
        var clientSourceMap = client.ValueKind != JsonValueKind.Null && client.TryGetProperty("map", out var clientMap) && clientMap.ValueKind != JsonValueKind.Null
            ? clientMap.GetRawText()
            : null;
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        if (result.TryGetProperty("modules", out var moduleElement) && moduleElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var module in moduleElement.EnumerateObject())
            {
                var code = module.Value.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(code))
                {
                    modules[module.Name] = code;
                }
            }
        }

        return new SvelteCompilationResult(
            SvelteViewName.Validate(source.ViewName),
            new SvelteViewTemplate(string.Empty, string.Empty, css),
            serverGraph,
            clientGraph,
            css,
            diagnostics,
            sourceHash)
        {
            ServerSourceMap = options.GenerateServer ? serverSourceMap : null,
            ClientSourceMap = options.GenerateClient ? clientSourceMap : null,
            CompilerVersion = options.CompilerVersion,
            ModuleGraphs = modules,
            RequiresAsyncRender = serverGraph.Contains("async", StringComparison.Ordinal)
        };
    }

    private static SvelteCompilationResult Failed(
        SvelteSourceFile source,
        SvelteCompilerOptions options,
        string sourceHash,
        IReadOnlyList<SvelteCompilerDiagnostic> diagnostics)
        => new(
            SvelteViewName.Validate(source.ViewName),
            new SvelteViewTemplate(string.Empty, string.Empty, string.Empty),
            string.Empty,
            string.Empty,
            string.Empty,
            diagnostics,
            sourceHash)
        {
            CompilerVersion = options.CompilerVersion
        };

    private static SvelteCompilerDiagnostic ToDiagnostic(JsonElement value, SvelteDiagnosticSeverity severity)
    {
        var code = value.TryGetProperty("code", out var codeValue) ? codeValue.GetString() : null;
        var message = value.TryGetProperty("message", out var messageValue) ? messageValue.GetString() : null;
        var location = value.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Object
            ? new SvelteSourceLocation(
                start.TryGetProperty("line", out var line) ? line.GetInt32() : 1,
                start.TryGetProperty("column", out var column) ? column.GetInt32() : 0,
                1)
            : null;
        return new SvelteCompilerDiagnostic(
            code ?? "svelte_diagnostic",
            message ?? "The Svelte compiler returned an unknown diagnostic.",
            severity,
            location);
    }

    private sealed record ModuleCompileRequest(string Path, string Filename, string Source);
}
