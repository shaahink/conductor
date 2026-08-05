using Conductor.Core.History;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Integrations;

public sealed partial class TelegramService
{
    /// <summary>K5.4 — the second half of the stamp. FU-OWNER-11 put the plan and the session on
    /// every message; what it still could not answer is WHICH CHECKOUT and WHICH WORK. One chat can
    /// carry two clones of the same plan on two branches, and a message that names neither is
    /// unreadable in exactly the way the identity line was invented to fix.
    /// <para>Stamped at the same choke point as the identity line, so a push added to the engine
    /// tomorrow carries it without knowing it exists. Empty — not a row of separators — when there is
    /// no repo, no branch, no stage and no tracker to read.</para></summary>
    internal string ContextLine(string? stageId = null)
    {
        var parts = new List<string>(3);

        var repo = RepoLabel();
        var branch = Branch();
        if (repo.Length > 0) parts.Add(branch.Length > 0 ? $"{repo}@{branch}" : repo);

        // The message's OWN stage wins over the run's: a session-end push composed while the run has
        // already moved on is about the stage it names, not the stage the engine is now in.
        var stage = string.IsNullOrWhiteSpace(stageId) ? _state.CurrentStage : stageId;
        if (!string.IsNullOrWhiteSpace(stage)) parts.Add(StageLabel(stage));

        var checkpoint = CurrentCheckpoint(stage);
        if (checkpoint is { Length: > 0 }) parts.Add(checkpoint);

        return parts.Count == 0 ? "" : $"<i>{EscapeHtml(string.Join(" · ", parts))}</i>";
    }

    private string RepoLabel()
    {
        try { return RunHistory.RepoLabel(_plan.Repo) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return ""; }
    }

    /// <summary>The checkpoint the run is on: the one marked in progress, else the next one not done.
    /// The tracker is the same view the Face and the report read, so a push cannot claim a checkpoint
    /// the board disagrees with.</summary>
    private string? CurrentCheckpoint(string? stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId)) return null;
        try
        {
            var rows = _progress.Read(_plan, CancellationToken.None).ForStage(stageId).ToList();
            return (rows.FirstOrDefault(c => c.IsInProgress) ?? rows.FirstOrDefault(c => !c.IsDone))?.Id;
        }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    // ── the branch and the remote, both of which cost a git process ──

    private string _branch = "";
    private DateTime _branchReadUtc = DateTime.MinValue;
    private static readonly TimeSpan GitFactTtl = TimeSpan.FromSeconds(30);

    /// <summary>Shelling out to git on every message would put a process between the engine and each
    /// push; a stage that switches branch is still reflected within
    /// <see cref="GitFactTtl"/>, which is far finer than the interval a human reads a chat at.</summary>
    private string Branch()
    {
        if (DateTime.UtcNow - _branchReadUtc < GitFactTtl) return _branch;
        _branchReadUtc = DateTime.UtcNow;
        try { _branch = Git.Branch(_plan.Repo) ?? ""; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { _branch = ""; }
        // A detached HEAD answers "HEAD", which names nothing — better to say nothing.
        if (string.Equals(_branch, "HEAD", StringComparison.Ordinal)) _branch = "";
        return _branch;
    }

    private string? _remote;
    private bool _remoteRead;

    /// <summary>The remote this run's links point at. <see cref="Reporter"/> memoizes the git call
    /// itself; this only avoids asking on every single push.</summary>
    internal string? Remote()
    {
        if (_remoteRead) return _remote;
        _remoteRead = true;
        try { _remote = Reporter.RemoteUrl(_plan.Repo); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { _remote = null; }
        return _remote;
    }

    /// <summary>Identity, then context — the block every outbound message opens with. The first line
    /// is unchanged from FU-OWNER-11 on purpose: it is what every other surface and test recognises a
    /// conductor push by.</summary>
    internal string Stamp(int? sessionNumber, string? stageId = null)
    {
        var context = ContextLine(stageId);
        return context.Length > 0 ? IdentityFor(sessionNumber) + "\n" + context : IdentityFor(sessionNumber);
    }
}
