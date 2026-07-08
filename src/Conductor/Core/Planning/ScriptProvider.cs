using System.Text.Json;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// The escape-hatch <see cref="IProgressProvider"/> for projects whose progress isn't a strict Markdown
/// table (F-1, D-2): it runs a plan-configured command and reads its stdout as a JSON array of
/// checkpoint objects (<c>[{ "id", "title", "status", "commit", "evidence" }, …]</c>). A Shamshir-style
/// <c>PROGRESS.md</c> is normalised by a small script the plan owns.
/// <para>Resilient by contract (B1.3 trap): a missing command, a nonzero exit, or malformed JSON is
/// surfaced as a single clear <see cref="InvalidOperationException"/> naming the provider, the command,
/// and the underlying cause — never a raw crash that parks the run without explanation.</para>
/// </summary>
public sealed class ScriptProvider(ScriptProviderConfig config) : IProgressProvider
{
    private readonly ScriptProviderConfig _config = config;

    public string Name => "script";

    public TrackerSnapshot Read(PlanConfig plan)
    {
        if (string.IsNullOrWhiteSpace(_config.Command))
            throw new InvalidOperationException(
                "script progress provider: progress.script.command is empty — set the command that prints checkpoint JSON.");

        var cwd = _config.Cwd is { Length: > 0 } rel ? Path.Combine(plan.Repo, rel) : plan.Repo;
        var result = ProcessRunner.RunPowerShell(
            _config.Command, cwd, TimeSpan.FromMinutes(_config.TimeoutMinutes));

        if (result.TimedOut)
            throw new InvalidOperationException(
                $"script progress provider: command timed out after {_config.TimeoutMinutes}m: {_config.Command}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"script progress provider: command exited {result.ExitCode}: {_config.Command}\n{Trim(result.Output)}");

        List<PlanCheckpoint>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<PlanCheckpoint>>(result.Output, PlanConfig.JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"script progress provider: command output was not a JSON checkpoint array ({ex.Message}). " +
                $"Expected e.g. [{{\"id\":\"P-0\",\"status\":\"DONE\"}}]. Command: {_config.Command}\n{Trim(result.Output)}",
                ex);
        }

        if (parsed is null)
            throw new InvalidOperationException(
                $"script progress provider: command produced no JSON (null). Command: {_config.Command}");

        var rows = parsed.ConvertAll(c => new CheckpointRow(
            c.Id.Trim(), c.Title.Trim(), c.Status.Trim(), c.Commit.Trim(), c.Evidence.Trim()));
        return new TrackerSnapshot { Checkpoints = rows, HandoffBlock = "", RawText = result.Output };
    }

    private static string Trim(string s) => s.Length > 800 ? string.Concat(s.AsSpan(0, 800), " …") : s;
}
