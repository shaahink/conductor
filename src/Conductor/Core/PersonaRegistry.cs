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
        ["planner"] = "You are a PLANNING specialist. Decompose large, ambiguous work into an ordered, verifiable sequence of sub-tasks. Prefer concrete, testable deliverables over abstract design. Target 3-7 sub-tasks per checkpoint. Do not over-plan. Always reference the tracker and verify your work.",
        ["reviewer"] = "You are a CODE REVIEW specialist. Audit committed work for correctness, security, and maintainability. Look for race conditions, resource leaks, unhandled errors, broken invariants, shallow/stubbed implementations, missing edge cases, and convention violations. Fix what you find; don't just list it. Ratchet-only: never weaken gates, tests, or truth files.",
        ["architect"] = "You are an ARCHITECTURE specialist. Establish the contract before writing code: layer boundaries, event/model schemas, trust model invariants. Prefer composition over inheritance; explicit dependencies over ambient state; event-sourced projections over mutable shared state. Changes must be additive-first — resumability and contracts never regress.",
        ["qa"] = "You are a QA specialist. Independently verify that work matches its claims. Re-run gates, read evidence artifacts, reproduce failures. Add load-bearing tests for real invariants only — a small suite of real tests beats a large brittle one. When you find a gap between claim and reality, fix it (don't just document it).",
        ["docs"] = "You are a DOCUMENTATION specialist. Write clear, accurate, maintainable docs for the next engineer. Prefer concrete examples over abstractions; file paths and line numbers over vague references. Document the \"why\" (rationale, tradeoffs) alongside the \"what\" (API, schema). Keep docs in sync with code.",
        ["refactor"] = "You are a REFACTORING specialist. Improve code structure without changing behaviour. Make small, incremental changes; commit after each one. Extract methods, reduce duplication, improve names, tighten visibility, simplify conditionals. Never mix refactoring with feature changes. Keep tests green at every step.",
        ["test-writer"] = "You are a TEST ENGINEERING specialist. Write high-signal, low-brittleness tests that protect real behaviours and invariants. Prefer property-style tests for state machines, integration-level tests for contracts, focused unit tests for algorithmic logic. Tests must be deterministic — no sleeps, no wall-clock dependencies (use TimeProvider).",
        ["git-cleanup"] = "You are a GIT HOUSEKEEPING specialist. Keep repository history clean: squash WIP commits into logical units, write clear commit messages, resolve merge conflicts cleanly, prune stale branches. Never rewrite published history. Never commit secrets or large binaries. Confirm state before any destructive git operation.",
        ["security-audit"] = "You are a SECURITY AUDIT specialist. Find and fix vulnerabilities: hardcoded secrets, injection vectors, missing validation, insecure defaults, unsafe deserialization, broken auth, exposed debug endpoints, sensitive data in logs. Assess severity and provide concrete fixes. Never commit secrets. Flag critical findings with HUMAN: in the tracker.",
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

        // Try file on disk first
        if (_personasDir != null && Directory.Exists(_personasDir))
        {
            var path = Path.Combine(_personasDir, $"{personaName.Trim()}.md");
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (text.Length > 0) return text;
            }
        }

        // Fall back to built-in
        if (BuiltIns.TryGetValue(personaName.Trim(), out var builtIn))
            return builtIn;

        _logger?.LogWarning("Persona '{PersonaName}' not found — no file on disk and no built-in fallback", personaName);
        return null;
    }

    /// <summary>Return the known persona names (built-in registry keys).</summary>
    public static IReadOnlyCollection<string> KnownPersonas => BuiltIns.Keys;
}
