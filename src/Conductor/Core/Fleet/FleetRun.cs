namespace Conductor.Core.Fleet;

/// <summary>One conductor run this machine can see. <paramref name="Port"/> 0 means an engine holding a
/// plan lock with no control plane to talk to.</summary>
public sealed record FleetRun(
    int Port,
    string BaseUrl,
    string PlanName,
    string RunId,
    string Repo,
    string StateDir,
    string Status,
    string StageId,
    string StageTitle,
    string? AttentionReason,
    int Done,
    int Total,
    decimal CostUsd)
{
    /// <summary>The engine process, from the discovery file or the plan lock. 0 = neither could be read.</summary>
    public int Pid { get; init; }

    /// <summary>When the control plane bound (discovery file) or the engine took the lock. Null = unknown.</summary>
    public DateTime? StartedUtc { get; init; }

    /// <summary>Did this run's state dir actually have a discovery file naming this port? False is
    /// normal, not broken — see <see cref="FleetScan"/> on why the probe leads.</summary>
    public bool HasDiscoveryFile { get; init; }

    /// <summary>First eight of the run id, the form every other surface prints.</summary>
    public string ShortRunId => RunId.Length >= 8 ? RunId[..8] : RunId;

    /// <summary>Trailing directory name of the repo — the way a human names which run they mean.</summary>
    public string RepoLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Repo)) return "";
            var trimmed = Repo.Replace('\\', '/').TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        }
    }
}
