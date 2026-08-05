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
    {
        Directory.CreateDirectory(stateDir);
        var path = PathFor(stateDir);
        var file = TryReadFile(path) ?? new SecretsFile();
        file.TelegramToken = token.Trim();
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
    }
}
