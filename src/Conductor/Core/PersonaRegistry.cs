using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core;

/// <summary>
/// Resolves persona names to system-prompt templates. Loads from
/// <c>&lt;PlanDir&gt;/personas/&lt;name&gt;.md</c> with built-in fallbacks when
/// a file is missing or the persona doesn't exist (B7.2).
/// </summary>
public sealed class PersonaRegistry
{
    private readonly string? _personasDir;
    private readonly ILogger<PersonaRegistry>? _logger;
    private static readonly Dictionary<string, string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deliver"] = "You are a DELIVERY specialist. You build, fix, and ship working software. Execute the assigned checkpoint: read the tracker, understand the design doc, make incremental changes, keep tests green, commit after each. Never weaken gates, tests, or truth files. Prefer small, reviewable diffs.",
        ["verify"] = "You are a VERIFICATION specialist. Independently verify that work matches its claims. Re-run the checkpoint's truth gate yourself, check evidence against artifacts and git history, produce a structured score {0-100, findings[], verdict}. A small suite of real tests beats a large brittle one. When you find a gap between claim and reality, record it — the findings feed the retry.",
        ["advise"] = "You are an ADVISORY specialist. Diagnose stalled runs, fact-check handoffs against git/log/artifacts, recommend recovery paths. Your verdict (Retry/NeedsHuman/SkipStage/RerunGates) is honored by the orchestrator. You never modify code — you analyze, recommend, and explain.",
    };

    public PersonaRegistry(PlanConfig plan, ILogger<PersonaRegistry>? logger = null)
    {
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(plan.PlanDir))
            _personasDir = Path.Combine(plan.PlanDir, "personas");
    }

    public PersonaRegistry(string? personasDir = null, ILogger<PersonaRegistry>? logger = null)
    {
        _logger = logger;
        _personasDir = personasDir;
    }

    /// <summary>Return the system prompt for a persona name, or null if the persona doesn't exist.
    /// Loads from disk when available, falls back to built-in templates (B7.2).</summary>
    public string? ResolveSystemPrompt(string? personaName)
    {
        if (string.IsNullOrWhiteSpace(personaName)) return null;

        var name = personaName.Trim();

        // Reject path traversal attempts — persona names are alphanumeric identifiers, never paths
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal))
        {
            _logger?.LogWarning("Persona name '{PersonaName}' contains invalid characters — falling back to built-in", personaName);
            if (BuiltIns.TryGetValue(name, out var fallback)) return fallback;
            return null;
        }

        // Try file on disk first
        if (_personasDir != null && Directory.Exists(_personasDir))
        {
            var path = Path.Combine(_personasDir, $"{name}.md");
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path).Trim();
                    if (text.Length > 0) return text;
                    _logger?.LogWarning("Persona file '{Path}' is empty — falling back to built-in", path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(ex, "Cannot read persona file '{Path}' — falling back to built-in", path);
                }
            }
        }

        // Fall back to built-in
        if (BuiltIns.TryGetValue(name, out var builtIn))
            return builtIn;

        _logger?.LogWarning("Persona '{PersonaName}' not found — no file on disk and no built-in fallback", personaName);
        return null;
    }

    /// <summary>Return the known persona names (built-in registry keys).</summary>
    public static IReadOnlyCollection<string> KnownPersonas => BuiltIns.Keys;
}
