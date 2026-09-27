using SvelteSharp.JavaScript;

namespace SvelteSharp.Compiler;

/// <summary>Severity of a SvelteSharp compiler diagnostic.</summary>
public enum SvelteDiagnosticSeverity
{
    /// <summary>Informational message.</summary>
    Info,
    /// <summary>Non-fatal compatibility message.</summary>
    Warning,
    /// <summary>Compilation cannot continue.</summary>
    Error
}

/// <summary>A source location for a compiler diagnostic.</summary>
public sealed record SvelteSourceLocation(int Line, int Column, int Length = 1);

/// <summary>A compiler diagnostic that can be shown without exposing engine internals.</summary>
public sealed record SvelteCompilerDiagnostic(
    string Code,
    string Message,
    SvelteDiagnosticSeverity Severity,
    SvelteSourceLocation? Location = null);

/// <summary>Input to the compiler.</summary>
public sealed record SvelteSourceFile(string ViewName, string Source, string? PhysicalPath = null)
{
    /// <summary>Whether this source is a <c>.svelte.ts</c> module.</summary>
    public bool IsModule { get; init; }

    /// <summary>Validates the source identity.</summary>
    public SvelteSourceFile Validate()
    {
        SvelteViewName.Validate(ViewName);
        ArgumentNullException.ThrowIfNull(Source);
        return this;
    }
}

/// <summary>Resolves build-local module imports such as <c>./format.svelte.ts</c>.</summary>
public interface ISvelteModuleSourceProvider
{
    /// <summary>Loads one module relative to the importing logical view.</summary>
    ValueTask<SvelteSourceFile?> GetAsync(
        string importerViewName,
        string specifier,
        CancellationToken cancellationToken = default);
}

/// <summary>Compiler configuration fixed into a build ID.</summary>
public sealed record SvelteCompilerOptions
{
    /// <summary>The supported Svelte major version.</summary>
    public string SvelteVersion { get; init; } = "5";

    /// <summary>The pinned compiler distribution version.</summary>
    public string CompilerVersion { get; init; } = "managed-baseline";

    /// <summary>Engine used to execute a real Svelte compiler adapter.</summary>
    public SvelteJavaScriptEngine Engine { get; init; } = SvelteJavaScriptEngine.Jint;

    /// <summary>Whether to emit the server graph.</summary>
    public bool GenerateServer { get; init; } = true;

    /// <summary>Whether to emit the client graph.</summary>
    public bool GenerateClient { get; init; } = true;

    /// <summary>Whether generated CSS should be emitted as an external artifact.</summary>
    public bool ExtractCss { get; init; } = true;

    /// <summary>Whether development checks are enabled in compiler output.</summary>
    public bool Dev { get; init; }

    /// <summary>Enables Svelte's async rendering/reactivity mode.</summary>
    public bool ExperimentalAsync { get; init; } = true;

    /// <summary>Whether generated JavaScript includes source maps.</summary>
    public bool GenerateSourceMap { get; init; } = true;

    /// <summary>Validates settings before compilation.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SvelteVersion) || string.IsNullOrWhiteSpace(CompilerVersion))
        {
            throw new ArgumentException("Svelte and compiler versions must be specified.");
        }

        if (!GenerateServer && !GenerateClient)
        {
            throw new ArgumentException("At least one Svelte output must be generated.");
        }
    }
}

/// <summary>One parsed Svelte template and its generated graphs.</summary>
public sealed record SvelteCompilationResult(
    string ViewName,
    SvelteViewTemplate Template,
    string ServerGraph,
    string ClientGraph,
    string Css,
    IReadOnlyList<SvelteCompilerDiagnostic> Diagnostics,
    string SourceHash)
{
    /// <summary>Server graph source map, when generated.</summary>
    public string? ServerSourceMap { get; init; }

    /// <summary>Client graph source map, when generated.</summary>
    public string? ClientSourceMap { get; init; }

    /// <summary>Compiler version used for this output.</summary>
    public string CompilerVersion { get; init; } = "managed-baseline";

    /// <summary>Whether the compiler produced no errors.</summary>
    public bool IsSuccessful => Diagnostics.All(diagnostic => diagnostic.Severity != SvelteDiagnosticSeverity.Error);

    /// <summary>Compiled module graphs keyed by paths relative to the view directory.</summary>
    public IReadOnlyDictionary<string, string> ModuleGraphs { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the server graph contains an async render path.</summary>
    public bool RequiresAsyncRender { get; init; }
}

/// <summary>Compiles one Svelte source file into separate server and client artifacts.</summary>
public interface ISvelteCompiler
{
    /// <summary>Compiles source without using Node, npm, pnpm, or Vite.</summary>
    ValueTask<SvelteCompilationResult> CompileAsync(
        SvelteSourceFile source,
        SvelteCompilerOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>An exception raised when compilation produces an error diagnostic.</summary>
public sealed class SvelteCompilationException : Exception
{
    /// <summary>Creates a compilation exception.</summary>
    public SvelteCompilationException(string viewName, IReadOnlyList<SvelteCompilerDiagnostic> diagnostics)
        : base($"Svelte view '{viewName}' could not be compiled: {string.Join("; ", diagnostics.Select(d => d.Message))}")
    {
        ViewName = viewName;
        Diagnostics = diagnostics;
    }

    /// <summary>View that failed.</summary>
    public string ViewName { get; }

    /// <summary>Compiler diagnostics.</summary>
    public IReadOnlyList<SvelteCompilerDiagnostic> Diagnostics { get; }
}
