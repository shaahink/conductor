using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// Resolves the configured <see cref="IProgressProvider"/> for a plan (B1.3, D-2). The default is the
/// byte-identical <see cref="MarkdownTableProvider"/>, so plans without a <c>progress</c> block behave
/// exactly as before. Selection is fail-fast: an unknown kind, or a kind missing its required config,
/// throws a clear <see cref="InvalidOperationException"/> at wire-up time rather than mid-run.
/// </summary>
public static class ProgressProviderFactory
{
    public static IProgressProvider Create(PlanConfig plan)
    {
        var cfg = plan.Progress ?? new ProgressConfig();
        return cfg.Kind?.Trim().ToLowerInvariant() switch
        {
            null or "" or "markdown-table" => new MarkdownTableProvider(),
            "script" => new ScriptProvider(
                cfg.Script ?? throw new InvalidOperationException(
                    "progress.kind is 'script' but progress.script is missing — declare the command that prints checkpoint JSON.")),
            "plan-checkpoints" => new PlanCheckpointProvider(
                cfg.Checkpoints is { Count: > 0 } cps
                    ? cps
                    : throw new InvalidOperationException(
                        "progress.kind is 'plan-checkpoints' but progress.checkpoints is empty — declare at least one checkpoint.")),
            var other => throw new InvalidOperationException(
                $"unknown progress.kind '{other}' — expected 'markdown-table', 'script', or 'plan-checkpoints'."),
        };
    }
}
