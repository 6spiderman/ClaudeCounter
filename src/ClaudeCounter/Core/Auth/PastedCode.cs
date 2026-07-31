namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Whatever the user pasted, reduced to a code and (when present) the state.
/// </summary>
/// <remarks>
/// Anthropic's copy/paste page shows the value as <c>code#state</c>, but people
/// paste all sorts of things: the whole callback URL from the address bar, the
/// code with a trailing newline, the code wrapped in quotes by a shell. Being
/// strict here just produces a confusing error for something we can obviously
/// interpret.
/// </remarks>
public sealed record PastedCode(string Code, string? State)
{
    public static PastedCode? Parse(string? pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted))
            return null;

        var text = pasted.Trim().Trim('"', '\'');
        if (text.Length == 0)
            return null;

        // A full callback URL: pull code and state out of the query string.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return FromUrl(text);
        }

        var hash = text.IndexOf('#');
        if (hash < 0)
            return new PastedCode(text, null);

        var code = text[..hash];
        var state = text[(hash + 1)..];
        return code.Length == 0 ? null : new PastedCode(code, state.Length == 0 ? null : state);
    }

    private static PastedCode? FromUrl(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;

        var code = QueryValue(uri.Query, "code");
        if (string.IsNullOrEmpty(code))
            return null;

        var state = QueryValue(uri.Query, "state");

        // Some pages put the fragment to the right of the code parameter
        // instead of in the query; treat it the same way.
        if (state is null && uri.Fragment.Length > 1)
            state = Uri.UnescapeDataString(uri.Fragment[1..]);

        return new PastedCode(code, string.IsNullOrEmpty(state) ? null : state);
    }

    private static string? QueryValue(string query, string name)
    {
        if (query.Length <= 1)
            return null;

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                continue;
            if (!pair[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }
}
