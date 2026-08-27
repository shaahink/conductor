namespace Conductor.Core;

/// <summary>
/// Bug #75 — where a multi-line <c>conductor note</c> body comes from, when the argument cannot carry it.
///
/// <para>Three DV3 acceptance records survived in the ledger as their first line alone — "DV3.3
/// ACCEPTANCE, declared before editing. Done means:" and nothing after it. <c>NoteCommand</c> never
/// truncated anything and neither did the store: the loss is in the <c>conductor.cmd</c> shim the
/// installed binary is reached through. A batch file hands <c>%*</c> to its target through
/// <c>cmd.exe</c>, and <c>cmd.exe</c> ends an argument at the first newline — measured 2026-08-27
/// with a scratch <c>echoargs.cmd</c>: <c>"line1\nline2\nline3"</c> arrives as <c>"line1</c>, from
/// PowerShell and from bash alike. Every multi-line note a session ever wrote through the shim was
/// cut the same way, silently, and the sessions that read the ledger believed they had the record.</para>
///
/// <para>The fix is a body channel a shim cannot cut: <c>-</c> as the text (or as <c>--detail</c>)
/// means "read the body from stdin", which is bytes on a pipe and not an argument at all. The
/// heuristic in <see cref="LooksCut"/> is the other half — a body that ends in the punctuation a
/// header ends in, with nothing after it, is the exact shape the shim leaves behind, and the verb says
/// so on stderr rather than filing it in silence.</para>
/// </summary>
public static class LedgerBody
{
    /// <summary>The argument that names stdin as the body's source.</summary>
    public const string StdinMarker = "-";

    /// <summary>The line the verb prints when it files a body that looks cut. One sentence, with the
    /// command that carries a body whole, so the operator who sees it once never sees it again.</summary>
    public const string CutHint =
        "the body ends in a colon or a dash with nothing after it - if you passed a multi-line argument, " +
        "the .cmd shim keeps only the first line; pipe the body instead: conductor note - < body.md";

    public static bool WantsStdin(string? arg) =>
        string.Equals(arg?.Trim(), StdinMarker, StringComparison.Ordinal);

    /// <summary>The body the verb files: the argument as given, or stdin when the argument is
    /// <see cref="StdinMarker"/>. Throws when stdin is named and nothing is piped into it — a note that
    /// waits on an interactive terminal for EOF is a session hung on a prompt nobody will answer.</summary>
    public static string Resolve(string? arg, TextReader stdin, bool stdinRedirected)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        if (!WantsStdin(arg)) return arg ?? "";
        if (!stdinRedirected)
            throw new InvalidOperationException(
                "'-' names stdin as the body, and nothing is piped into it: conductor note - < body.md");
        var body = stdin.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (body.Length == 0)
            throw new InvalidOperationException("stdin was empty - nothing to file");
        return body;
    }

    /// <summary>True for a single-line body ending in <c>:</c> or a trailing dash — the header of a
    /// list whose items never arrived. A one-line note that ends in a full stop, a word, or a closing
    /// bracket is not suspected; the shim's signature is specifically "the sentence that introduces
    /// what follows, and nothing follows".</summary>
    public static bool LooksCut(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var trimmed = body.Trim();
        if (trimmed.Contains('\n', StringComparison.Ordinal)) return false;
        return trimmed.EndsWith(':')
            || trimmed.EndsWith(" -", StringComparison.Ordinal)
            || trimmed.EndsWith(" —", StringComparison.Ordinal);
    }
}
