using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SvelteSharp.Compiler;

/// <summary>
/// A deterministic, Node-free compiler baseline. It covers the server/client contract
/// and simple Svelte 5 markup while adapter packages can replace it with the pinned
/// Svelte compiler without changing the public pipeline.
/// </summary>
public sealed class ManagedSvelteCompiler : ISvelteCompiler
{
    private static readonly Regex Script = new(
        "<script\\b[^>]*>(?<content>.*?)</script\\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Head = new(
        "<svelte:head\\s*>(?<content>.*?)</svelte:head\\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex Style = new(
        "<style\\b[^>]*>(?<content>.*?)</style\\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public ValueTask<SvelteCompilationResult> CompileAsync(
        SvelteSourceFile source,
        SvelteCompilerOptions options,
        CancellationToken cancellationToken = default)
    {
        source.Validate();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<SvelteCompilerDiagnostic>();
        var markup = source.Source;

        if (markup.Contains("{@html", StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "SVE1001",
                "{@html} is not enabled by the managed baseline; use trusted, explicitly reviewed content.",
                SvelteDiagnosticSeverity.Error));
        }

        if (markup.Contains(" on:", StringComparison.Ordinal) || markup.Contains(" onclick=", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new(
                "SVE1002",
                "Event directives require the full Svelte compiler adapter and are not supported by the managed baseline.",
                SvelteDiagnosticSeverity.Error));
        }

        var body = Script.Replace(markup, string.Empty);
        body = Head.Replace(body, string.Empty);
        var head = Head.Match(markup).Success ? Head.Match(markup).Groups["content"].Value.Trim() : string.Empty;
        var cssMatches = Style.Matches(body);
        var css = string.Join(Environment.NewLine, cssMatches.Select(match => match.Groups["content"].Value.Trim()));
        body = Style.Replace(body, string.Empty).Trim();

        if (markup.Contains("<script", StringComparison.OrdinalIgnoreCase)
            && !Script.IsMatch(markup))
        {
            diagnostics.Add(new(
                "SVE1003",
                "A script element is missing its closing tag.",
                SvelteDiagnosticSeverity.Error));
        }

        if (markup.Contains("{#", StringComparison.Ordinal) || markup.Contains("{/", StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                "SVE1004",
                "Control blocks require the full Svelte compiler adapter and are not supported by the managed baseline.",
                SvelteDiagnosticSeverity.Error));
        }

        var template = new SvelteViewTemplate(body, head, css);
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Source))).ToLowerInvariant();
        var serverGraph = options.GenerateServer ? CreateServerGraph(source.ViewName, sourceHash) : string.Empty;
        var clientGraph = options.GenerateClient ? CreateClientGraph(template) : string.Empty;

        return ValueTask.FromResult(new SvelteCompilationResult(
            SvelteViewName.Validate(source.ViewName),
            template,
            serverGraph,
            clientGraph,
            css,
            diagnostics,
            sourceHash));
    }

    private static string CreateServerGraph(string viewName, string sourceHash)
        => $"// SvelteSharp managed server graph\n// view: {viewName}\n// source: {sourceHash}\n";

    private static string CreateClientGraph(SvelteViewTemplate template)
    {
        var bodyLiteral = JsonSerializer.Serialize(template.Body);
        var headLiteral = JsonSerializer.Serialize(template.Head);
        return $$"""
            // SvelteSharp managed browser graph. It has no Node runtime dependency.
            const bodyTemplate = {{bodyLiteral}};
            const headTemplate = {{headLiteral}};
            const expression = /\{([A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*)\}/g;
            const resolve = (model, path) => path.split('.').reduce((value, part) =>
              part === 'model' || part === 'props' ? value : value == null ? undefined : value[part], model);
            const escape = (value) => String(value ?? '').replace(/[&<>"']/g,
              character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
            const render = (template, model) => template.replace(expression,
              (_, path) => escape(resolve(model, path)));
            export function mount(target, model) {
              target.innerHTML = render(bodyTemplate, model);
              return { update(nextModel) { target.innerHTML = render(bodyTemplate, nextModel); } };
            }
            export function hydrate(target, model) {
              if (!target.innerHTML.trim()) return mount(target, model);
              return { update(nextModel) { target.innerHTML = render(bodyTemplate, nextModel); } };
            }
            export { headTemplate };
            """;
    }
}
