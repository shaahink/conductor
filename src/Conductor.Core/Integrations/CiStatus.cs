using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Models;

namespace Conductor.Core.Integrations;

/// <summary>CH1.3 — one workflow's newest run on the branch, as it was when asked.</summary>
/// <param name="Workflow">The workflow's display name, e.g. <c>CI</c>.</param>
/// <param name="Path">Its file, e.g. <c>.github/workflows/ci.yml</c> — the thing an owner edits.</param>
/// <param name="State">GitHub's own word for the workflow: <c>active</c>, <c>disabled_manually</c>, …</param>
/// <param name="RunSha">The commit that run was for, or <c>""</c> when the workflow has never run on
/// this branch. That empty string is a finding, not a blank.</param>
/// <param name="Status"><c>queued</c>, <c>in_progress</c>, <c>completed</c>, or <c>""</c>.</param>
/// <param name="Conclusion"><c>success</c>, <c>failure</c>, … or <c>""</c> while it is still running.</param>
/// <param name="Url">Where a reader goes to look at it.</param>
public sealed record CiWorkflowVerdict(
    string Workflow, string Path, string State, string RunSha, string Status, string Conclusion, string Url);

/// <summary>
/// CH1.3 — what CI said, when it was asked, and about which commit.
///
/// <para><b>An observation, not a health record.</b> DV1.1's rule is "derived, never stored", because
/// a stored health record outlives the condition that raised it. What is stored here is not health:
/// it is a dated measurement carrying the commit it was about, and the health is DERIVED from it
/// every time a surface asks. That is what makes it self-invalidating — the moment HEAD moves past
/// <see cref="HeadSha"/> the derived answer becomes "CI has not judged this commit", with no
/// clearing step and no way for a stale green to be reported as a current one.</para>
///
/// <para>It is stored at all because the surfaces that must show it — the REPORT.md header and the
/// owner queue — are built synchronously and must render on a machine with no network and no token,
/// exactly as <c>conductor report</c> does after a run is over.</para>
/// </summary>
public sealed record CiStatus(
    string FetchedUtc, string Repo, string Branch, string HeadSha, IReadOnlyList<CiWorkflowVerdict> Workflows)
{
    /// <summary>Where it lives, beside the run's other derived files.</summary>
    public const string FileName = "ci-status.json";

    /// <summary>The recorded observation, or null when nobody has asked yet. Never throws: an
    /// unreadable or half-written file is the same as no observation, and a report that crashed
    /// because a cache file was truncated would be a worse failure than the one being fixed.</summary>
    public static CiStatus? Read(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            var path = Path.Combine(plan.StateDir, FileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), CiJsonContext.Default.CiStatus);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Records the observation. Atomic, because a report may read it at any moment.</summary>
    public static void Write(PlanConfig plan, CiStatus status)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Directory.CreateDirectory(plan.StateDir);
        AtomicFile.Write(Path.Combine(plan.StateDir, FileName),
            JsonSerializer.Serialize(status, CiJsonContext.Default.CiStatus));
    }
}

/// <summary>Source-generated, on <c>GithubJsonContext</c>'s reasoning: reflection-based
/// <see cref="JsonSerializer"/> is what a trimmed publish silently loses.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CiStatus))]
public sealed partial class CiJsonContext : JsonSerializerContext;
