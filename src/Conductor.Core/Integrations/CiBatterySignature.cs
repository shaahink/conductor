using System.Globalization;

namespace Conductor.Core.Integrations;

/// <summary>
/// CH1.3 — one command reduced to WHAT MAKES IT THE SAME COMMAND, so a gate and a CI step can be
/// compared without either being re-worded to match.
///
/// <para>The two batteries are written in different dialects for good reasons: a gate is one shell
/// line with the repo's absolute path in it, a CI step is a YAML block with a
/// <c>working-directory</c>. Comparing them verbatim would report drift on every line; comparing
/// them not at all is what let the local battery be green for a whole era while CI was red. So each
/// side is reduced to a signature — the program, plus the one argument that decides what it does —
/// and the signatures are compared.</para>
///
/// <para><b>What it deliberately drops:</b> wrappers (<c>cmd /c</c>, <c>pwsh -c</c>), directory
/// changes, flags, and file arguments that only name the solution. <c>dotnet build Conductor.slnx
/// -clp:ErrorsOnly</c> and <c>dotnet build Conductor.slnx --configuration Debug</c> are the same
/// step. <c>go build ./...</c> and <c>go test ./...</c> are not.</para>
/// </summary>
public static class CiBatterySignature
{
    /// <summary>Commands that move around or print — never a step of a battery, on either side.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "chdir", "set", "export", "echo", "if", "then", "fi", "true", ":",
    };

    /// <summary>Shells whose real command is the thing AFTER them.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "bash", "sh", "shell",
    };

    /// <summary>Every distinct signature in one shell command, which may be several commands joined
    /// by <c>&amp;&amp;</c>, <c>;</c> or newlines. Order is preserved and duplicates removed.</summary>
    public static IReadOnlyList<string> Of(string? command)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(command)) return found;

        foreach (var part in Split(command))
        {
            var sig = One(part);
            if (sig.Length > 0 && !found.Contains(sig, StringComparer.Ordinal)) found.Add(sig);
        }
        return found;
    }

    /// <summary>One command line's signature, or <c>""</c> when it is noise.</summary>
    private static string One(string command)
    {
        var tokens = Tokenise(command);
        var i = 0;

        // Peel wrappers: `cmd /c "go build ./..."`, `powershell -File x.ps1`, `pwsh -c "..."`.
        while (i < tokens.Count && Wrappers.Contains(Program(tokens[i])))
        {
            var shell = Program(tokens[i]);
            i++;
            // -File names a SCRIPT, and the script IS the step: `tools/gates/ratchet.ps1` is not
            // interchangeable with any other powershell invocation, so it keeps the shell's name.
            for (var k = i; k < tokens.Count; k++)
            {
                if (!IsFlag(tokens[k], "file")) continue;
                if (k + 1 >= tokens.Count) break;
                return Word(shell) + " " + Normalise(tokens[k + 1]);
            }
            while (i < tokens.Count && (tokens[i].StartsWith('-') || tokens[i].StartsWith('/'))) i++;
        }

        if (i >= tokens.Count) return "";
        var program = Program(tokens[i]);
        if (Noise.Contains(program)) return "";
        i++;

        // The verb: the first argument that is not a flag and not a path-only argument. `dotnet build
        // Conductor.slnx` is `dotnet build`; `./bin/conductor demo` is `conductor demo`.
        for (; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith('-')) continue;
            if (t.Contains('=', StringComparison.Ordinal)) continue;
            return Word(program) + " " + Word(t);
        }
        return Word(program);
    }

    /// <summary>Split a shell blob into individual commands. Naive on purpose — a <c>&amp;&amp;</c>
    /// inside a quoted string would split wrongly, and no battery on either side has one.</summary>
    private static IEnumerable<string> Split(string command)
    {
        // Quotes come off FIRST. A gate wraps its real command in them — `cmd /c "cd /d <repo> &&
        // go build ./..."` — and splitting a quoted blob on `&&` would otherwise leave the opening
        // quote glued to the first token, which reads as a program nobody ran.
        var flat = command.Replace("\r\n", "\n", StringComparison.Ordinal)
                          .Replace("\"", " ", StringComparison.Ordinal)
                          .Replace("'", " ", StringComparison.Ordinal);
        foreach (var line in flat.Split('\n'))
            foreach (var piece in line.Split(["&&", "||", ";", "|"], StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = piece.Trim().Trim('"');
                if (trimmed.Length > 0) yield return trimmed;
            }
    }

    /// <summary>Whitespace split that keeps a quoted argument whole.</summary>
    private static List<string> Tokenise(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var ch in command)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else current.Append(ch);
            }
            else if (ch is '"' or '\'') quote = ch;
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static bool IsFlag(string token, string name) =>
        (token.StartsWith('-') || token.StartsWith('/'))
        && string.Equals(token.TrimStart('-', '/'), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The program, without its directory or extension: <c>./src/x/bin/conductor.exe</c>
    /// and <c>conductor</c> are the same program.</summary>
    private static string Program(string token)
    {
        var t = Normalise(token);
        var slash = t.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < t.Length) t = t[(slash + 1)..];
        return t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4] : t;
    }

    private static string Normalise(string token) =>
        token.Replace('\\', '/').Trim('"').Trim();

    private static string Word(string token) => token.ToLower(CultureInfo.InvariantCulture);
}
