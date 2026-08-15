using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

/// <summary>Local secrets the Face lets you type in directly (M8.2's "configure it through the
/// app" Telegram onboarding) rather than requiring an env var. Lives at
/// &lt;StateDir&gt;/secrets.local.json, already excluded by the state dir's own blanket
/// .gitignore ("*", written by RunLoop.EnsureStateDirGitignore) — never committed. An env var,
/// where one exists for the same credential, always takes precedence; this is the fallback.</summary>
#pragma warning disable MA0045 // sync file I/O by design — rare, human-triggered reads/saves, not a hot path
public static class SecretsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string PathFor(string stateDir) => Path.Combine(stateDir, "secrets.local.json");

    public static string? TryReadTelegramToken(string stateDir)
    {
        var token = TryReadFile(PathFor(stateDir))?.TelegramToken?.Trim();
        return token is { Length: > 0 } ? token : null;
    }

    public static void WriteTelegramToken(string stateDir, string token)
        => Write(stateDir, (file, t) => file.TelegramToken = t, token);

    /// <summary>KS9.1: the GitHub personal access token, in the SAME file beside the Telegram one —
    /// one place the operator's local credentials live, one blanket .gitignore covering it.
    /// <c>CONDUCTOR_GITHUB_TOKEN</c> still wins; see <c>GithubTokens.Resolve</c>.</summary>
    public static string? TryReadGithubToken(string stateDir)
    {
        var token = TryReadFile(PathFor(stateDir))?.GithubToken?.Trim();
        return token is { Length: > 0 } ? token : null;
    }

    public static void WriteGithubToken(string stateDir, string token)
        => Write(stateDir, (file, t) => file.GithubToken = t, token);

    /// <summary>Read-modify-write of the one file. Shared so a second credential cannot be added by
    /// a path that CLOBBERS the first — serializing a fresh <c>SecretsFile</c> would drop the other
    /// token, and the failure would only show up the next time Telegram was used.</summary>
    private static void Write(string stateDir, Action<SecretsFile, string> set, string token)
    {
        Directory.CreateDirectory(stateDir);
        var path = PathFor(stateDir);
        var file = TryReadFile(path) ?? new SecretsFile();
        set(file, token.Trim());
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
    }

    private static SecretsFile? TryReadFile(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<SecretsFile>(File.ReadAllText(path), JsonOpts); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private sealed class SecretsFile
    {
        public string? TelegramToken { get; set; }
        public string? GithubToken { get; set; }
    }
}
