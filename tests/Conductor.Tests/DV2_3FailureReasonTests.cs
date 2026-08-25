using Conductor.Core;
using Conductor.Models;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV2.3, bug #66 — <c>report push failed:</c> with nothing after the colon, on a live run, over and
/// over, undiagnosable.
///
/// <para>The cause is not that git said nothing. It is that the message quoted <c>Output</c>, and git
/// writes every refusal it has — a rejected non-fast-forward, an auth failure, no configured remote —
/// to STDERR. The one stream the log line read was the one guaranteed to be empty.
/// <see cref="ProcessRunner.FailureReason"/> prefers stderr, falls back to stdout, and when a process
/// fails with nothing on either stream says so with the exit code, because a colon with nothing after
/// it is what made the failure undiagnosable in the first place.</para>
///
/// <para>The last test is the regression proper: a REAL failing <c>git push</c>, whose
/// <c>Output</c> really is empty, proving the defect's mechanism rather than describing it.</para>
/// </summary>
public sealed class DV2_3FailureReasonTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"conductor-dv23push-{Guid.NewGuid():N}");
    private readonly ITestOutputHelper _out;

    public DV2_3FailureReasonTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    private static ProcResult Result(string stdout, string stderr, int exit = 1, bool timedOut = false,
        double seconds = 0.5) => new(exit, stdout, stderr, timedOut, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Stderr_wins_over_stdout_because_that_is_where_git_refuses()
    {
        Assert.Equal("fatal: no configured push destination",
            Result("Everything up-to-date", "fatal: no configured push destination").FailureReason());
    }

    [Fact]
    public void Stdout_is_the_fallback_when_stderr_is_empty()
    {
        Assert.Equal("error: could not read from remote", Result("error: could not read from remote", "   ").FailureReason());
    }

    /// <summary>The head, not the tail: a refused command announces the refusal and then explains how
    /// to fix it, so the last lines are the advice and the first is the reason. That is the exact
    /// shape of <c>git push</c> with no remote — see the live test at the bottom of this file.</summary>
    [Fact]
    public void The_first_lines_are_kept_and_joined_because_a_refusal_leads_with_its_reason()
    {
        const string refusal = "fatal: it went wrong\ntry this\nor this\nor even this\nlast word";
        Assert.Equal("fatal: it went wrong | try this | or this", Result("", refusal).FailureReason());
        Assert.Equal("fatal: it went wrong", Result("", refusal).FailureReason(1));
    }

    /// <summary>The property that #66 is actually about: whatever happened, this says something.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "\r\n")]
    [InlineData(null, null)]
    public void A_failure_with_no_output_at_all_is_still_never_the_empty_string(string? stdout, string? stderr)
    {
        var reason = Result(stdout!, stderr!, exit: 128).FailureReason();
        _out.WriteLine(reason);
        Assert.Equal("exit code 128, with no output on stdout or stderr", reason);
    }

    [Fact]
    public void A_timeout_says_so_whether_or_not_it_printed_anything()
    {
        Assert.Equal("timed out after 600s, with no output on stdout or stderr",
            Result("", "", exit: -1, timedOut: true, seconds: 600).FailureReason());
        Assert.Equal("fatal: unable to access remote (timed out)",
            Result("", "fatal: unable to access remote", exit: -1, timedOut: true).FailureReason());
    }

    /// <summary>
    /// The regression, against a real process. A repository with no remote refuses <c>git push</c>
    /// on stderr and prints NOTHING on stdout — so the old message, which quoted <c>Output</c>, was
    /// structurally incapable of carrying a reason, and this asserts that emptiness rather than
    /// assuming it.
    /// </summary>
    [Fact]
    public void A_real_failing_git_push_has_an_empty_stdout_and_a_reason_anyway()
    {
        Directory.CreateDirectory(_repo);
        var init = ProcessRunner.Run("git", ["init", "--quiet"], _repo, TimeSpan.FromMinutes(1));
        Assert.Equal(0, init.ExitCode);

        var push = Git.Exec(_repo, "push");
        _out.WriteLine($"exit={push.ExitCode}");
        _out.WriteLine($"stdout=[{push.Output}]");
        _out.WriteLine($"stderr=[{push.StdErr}]");
        _out.WriteLine($"FailureReason=[{push.FailureReason()}]");

        Assert.NotEqual(0, push.ExitCode);
        // What "report push failed: " was quoting.
        Assert.True(string.IsNullOrWhiteSpace(push.Output), $"expected an empty stdout, got: {push.Output}");

        var reason = push.FailureReason();
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("fatal", reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The field line itself, end to end: a real report commit pushed from a repo with no remote,
    /// through the real <see cref="Reporter.WriteAndPublish"/>, collecting what it logs. This is the
    /// sentence that appeared on a live run — <c>report push failed:</c> and then nothing at all —
    /// and it is the one the triage ledger's row for #66 asks for.
    /// </summary>
    [Fact]
    public void The_report_push_failure_line_now_carries_a_reason()
    {
        Directory.CreateDirectory(_repo);
        Git.Exec(_repo, "init", "-b", "main");
        Git.Exec(_repo, "config", "user.email", "dv23@rig");
        Git.Exec(_repo, "config", "user.name", "DV23");
        Git.Exec(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "x");
        Git.Exec(_repo, "add", "-A");
        Git.Exec(_repo, "commit", "-m", "seed");

        var plan = new PlanConfig
        {
            Name = "DV23",
            Repo = _repo,
            // Push with no remote configured: the same shape as the live failure, which was a push
            // the upstream refused.
            Report = new ReportConfig { Commit = true, Push = true },
            Stages = { new StageConfig { Id = "DV2", Title = "The sweep" } },
        };
        var state = new RunState
        {
            PlanName = "DV23",
            CurrentStage = "DV2",
            Status = RunStatus.Idle,
            History =
            {
                new SessionRecord
                {
                    Number = 1, Stage = "DV2", Attempt = 1,
                    Kind = SessionKind.Deliver, Outcome = SessionOutcome.Advanced,
                },
            },
        };

        var logged = new List<string>();
        Reporter.WriteAndPublish(plan, state, new TrackerSnapshot(), null, logged.Add);
        foreach (var l in logged) _out.WriteLine(l);

        var line = Assert.Single(logged, l => l.StartsWith("report push failed:", StringComparison.Ordinal));
        var after = line["report push failed:".Length..].Trim();
        Assert.NotEqual("", after);                 // the entire defect, in one assertion
        Assert.Contains("fatal", after, StringComparison.OrdinalIgnoreCase);
    }
}
