namespace Conductor.Models;

/// <summary>Progress-provider selection + config (B1.3, D-2). `Kind` picks the implementation; the
/// nested blocks configure the non-default providers. The default `markdown-table` needs no config —
/// it reads <see cref="PlanConfig.TrackerPath"/> exactly as Conductor always has.</summary>
public sealed class ProgressConfig
{
    /// <summary>"markdown-table" (default), "script", or "plan-checkpoints".</summary>
    public string Kind { get; set; } = "markdown-table";

    /// <summary>Config for the `script` provider (runs a command that prints checkpoint JSON).</summary>
    public ScriptProviderConfig? Script { get; set; }

    /// <summary>Checkpoints declared inline in the plan (the `plan-checkpoints` provider).</summary>
    public List<PlanCheckpoint>? Checkpoints { get; set; }
}

/// <summary>A checkpoint declared inline in the plan JSON (the `plan-checkpoints` provider). Mirrors a
/// tracker row so it folds into the same <c>CheckpointRow</c> contract the engine already consumes.</summary>
public sealed class PlanCheckpoint
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "TODO";
    public string Commit { get; set; } = "";
    public string Evidence { get; set; } = "";
}
