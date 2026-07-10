using System.Text.Json;

namespace Conductor.Core;

/// <summary>Parsed form of a <c>control.json</c> drop-file (written by the CLI verbs, consumed by the
/// running orchestrator). Kept pure/side-effect-free so the flag handling — including nullable
/// <c>confirmed</c>/<c>force</c> serialised as JSON null by non-destructive commands — is unit-tested.</summary>
public readonly record struct ControlCommand(ControlAction? Action, bool Confirmed, string? IntentId, string? StageId, bool Force, string? Value)
{
    /// <summary>Wraps a bare verb (no payload) — what the TUI's keypress queue and headless
    /// <see cref="PlainSink"/> both send; the file/HTTP ingresses populate the other fields directly.</summary>
    public static ControlCommand Of(ControlAction action) => new(action, false, null, null, false, null);
}

public static class ControlFile
{
    /// <summary>Parse a control.json body. Every field is read via <see cref="JsonValueKind"/> so a null
    /// or wrong-typed value (operator input, not an engine fault) yields a default rather than throwing —
    /// only genuinely malformed JSON throws <see cref="JsonException"/>, which the caller treats as a
    /// skipped poll.</summary>
    public static ControlCommand Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cmd = root.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var action = cmd?.ToLowerInvariant() switch
        {
            "pause" => ControlAction.PauseAfterSession,
            "resume" => ControlAction.ResumeRun,
            "approve" => ControlAction.ResumeRun,
            "abort" => ControlAction.AbortNow,
            "skip" => ControlAction.SkipStage,
            "kill" => ControlAction.KillSession,
            "stop-after" => ControlAction.StopAfterSession,
            "retry-stage" => ControlAction.RetryStage,
            "rollback" => ControlAction.Rollback,
            "pause-after-stage" => ControlAction.PauseAfterStage,
            "goto" => ControlAction.Goto,
            _ => (ControlAction?)null,
        };
        var confirmed = root.TryGetProperty("confirmed", out var cf) && cf.ValueKind == JsonValueKind.True;
        var force = root.TryGetProperty("force", out var ff) && ff.ValueKind == JsonValueKind.True;
        var intentId = root.TryGetProperty("intentId", out var ii) && ii.ValueKind == JsonValueKind.String ? ii.GetString() : null;
        var stageId = root.TryGetProperty("stageId", out var si) && si.ValueKind == JsonValueKind.String ? si.GetString() : null;
        var value = root.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
        return new ControlCommand(action, confirmed, intentId, stageId, force, value);
    }
}
