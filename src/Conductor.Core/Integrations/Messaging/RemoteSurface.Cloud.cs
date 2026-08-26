using Conductor.Core.Events;
using Conductor.Core.Integrations.Cloud;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>DV5.1 / findings §2.3 CL-2 — <c>/cloud</c>: the owner's own verb for work that runs
/// somewhere conductor cannot watch.
///
/// <para>Its own partial for the reason <c>RemoteSurface.Routing</c> is: this is the one command on
/// the surface with an EXTERNAL process behind it, and the one whose answer can take minutes. It is
/// therefore DETACHED — the inbound poll loop must keep answering <c>/status</c> while a cloud
/// session thinks — and <see cref="CloudInFlight"/> exists so that a detached task is still one a
/// test and a shutdown can await.</para>
///
/// <para>What it does NOT do is start a cloud session. Measured against claude
/// <see cref="CloudCliFacts.MeasuredVersion"/>, creating one is interactive-only; conductor refuses
/// in the owner's chat with the platform's own words rather than faking a terminal to defeat a
/// refusal a research-preview surface makes on purpose.</para></summary>
public sealed partial class RemoteSurface
{
    private Task _cloudInFlight = Task.CompletedTask;

    /// <summary>The <c>/cloud</c> call currently running, or a completed task. The call is detached
    /// on purpose (a cloud round trip is minutes and the poll loop is one thread of control), and a
    /// detached task nobody can await is a racing test and a truncating shutdown.</summary>
    public Task CloudInFlight => _cloudInFlight;

    /// <summary>Which checkout <c>/cloud</c> is about, by the same ladder <c>/project</c> sets: this
    /// chat's selected project, else the run this surface belongs to.</summary>
    private (string Repo, string Name) CloudTarget(string chatId, long? threadId)
    {
        if (_notes is { } notes)
        {
            var current = notes.Routes.Current(chatId, threadId);
            if (current is { Length: > 0 } && notes.Projects.Resolve(current).Project is { } chosen)
                return (chosen.Repo, chosen.Name);
        }

        return (_composer.RepoDir, _composer.RunLabel);
    }

    private Task CloudAsync(string chatId, long? threadId, string argument, CancellationToken ct)
    {
        if (_cloud is null)
        {
            return ReplyAsync(chatId,
                "This bot has no cloud verb wired: it is running against a channel with no project behind it.",
                null, ct);
        }

        var (repo, name) = CloudTarget(chatId, threadId);
        _cloudInFlight = RunCloudAsync(chatId, repo, name, argument, ct);
        return Task.CompletedTask;
    }

    private async Task RunCloudAsync(string chatId, string repo, string name, string argument,
        CancellationToken ct)
    {
        CloudVerbResult result;
        try
        {
            result = await _cloud!.RunAsync(repo, name, argument, ct).ConfigureAwait(false);
        }
        // Catch-all on purpose, exactly as the inject path does: an inbound command must never take
        // the poll loop down with it, and this one has a subprocess and a network behind it.
        catch (Exception ex)
        {
            await ReplyAsync(chatId, $"/cloud failed: {MessageComposer.EscapeHtml(ex.Message)}", null, ct)
                .ConfigureAwait(false);
            return;
        }

        _store?.AppendEvent(new OwnerCloudAction
        {
            RunId = _state.RunId ?? "",
            Action = result.Action,
            Repo = repo,
            CloudSessionId = result.SessionId,
            Url = result.Url,
            Cost = result.Cost,
        });

        await ReplyAsync(chatId, MessageComposer.EscapeHtml(result.Reply), null, ct).ConfigureAwait(false);
    }
}
