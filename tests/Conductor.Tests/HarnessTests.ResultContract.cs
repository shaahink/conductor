using Conductor.Core;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// K5.1 — the result contract driven through a real run, not asserted from source reading.
///
/// <para>The unit tests prove the parse. They do not prove the leg that actually loses the fields: an
/// agent printing a structured result, a real provider assembling its text, <c>SessionRunner</c>
/// storing it, and only then the record being read by everything downstream. That is where the 700
/// character cut lived, and a cut is invisible until something long enough passes through it.</para>
///
/// <para>So this agent reports the shape the format asks for, with one bullet long enough to push the
/// <c>evidence:</c> and <c>gaps:</c> lines past character 700 — exactly the sessions whose evidence
/// path used to vanish from the record, the report and the phone.</para>
/// </summary>
public sealed partial class HarnessTests
{
    private const string ContractHeadline = "delivered H0.1 with the result contract in the loop";
    // No apostrophe anywhere in these: the fake agent carries them inside a single-quoted PowerShell
    // string, and one apostrophe ends that string, empties the result and fails the test at step 1.
    private const string ContractGaps = "the golden report render belongs to K5.2, not to this one";
    private const string ContractEvidence = ".conductor/evidence/K5/K5-1-live-run.md";
    private const string ContractSecondBullet = "a second, ordinary outcome bullet";

    /// <summary>Exactly what the fake agent prints — one string, so the assertion about what the old
    /// cut would have done is made against the same bytes the run actually carried.</summary>
    private static string ContractResultText() =>
        $"SESSION-RESULT: {ContractHeadline}\n" +
        "- one bullet long enough to bury what follows it: " + new string('p', 620) + "\n" +
        $"- {ContractSecondBullet}\n" +
        "artefacts: harness-output.txt\n" +
        $"evidence: {ContractEvidence}\n" +
        $"gaps: {ContractGaps}";

    /// <summary>PowerShell, not cmd.exe: see <see cref="CacheHeavyAgentScript"/> for why.</summary>
    private static string ContractAgentScript() => string.Join("\r\n",
        "param([string]$Prompt = \"\")",
        "Write-Output '{\"type\":\"step_start\"}'",
        "Write-Output '{\"type\":\"step_finish\",\"part\":{\"cost\":0.0004,\"tokens\":" +
        "{\"input\":1000,\"output\":500,\"cache\":{\"read\":0}}}}'",
        "Write-Output '{\"type\":\"text\",\"part\":{\"text\":\"" +
        ContractResultText().Replace("\n", "\\n", StringComparison.Ordinal) + "\"}}'",
        "Set-Content -Path harness-output.txt -Value 'harness done'",
        "git add harness-output.txt",
        "git commit -m 'feat: deliver contract checkpoint'",
        "exit 0",
        "");

    [Fact]
    public async Task FullCycle_KeepsTheEvidenceAndGapsThe700CharCutUsedToEat()
    {
        var script = Path.Combine(_repo, "contract-agent.ps1");
        await File.WriteAllTextAsync(script, ContractAgentScript());

        var plan = new PlanConfig
        {
            Name = "ContractPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "powershell",
                Args = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Prompt", "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        Assert.Equal(0, await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None));

        var session = Assert.Single(state.History);
        var stored = session.ResultSummary;

        // 0. The cut this checkpoint exists to remove, measured on the very text this run carried:
        //    at 700 characters both of the fields a reviewer reads are already gone.
        var printed = ContractResultText();
        var oldCut = printed.Length <= 700 ? printed : printed[..700] + "…";
        Assert.DoesNotContain("evidence:", oldCut, StringComparison.Ordinal);
        Assert.DoesNotContain("gaps:", oldCut, StringComparison.Ordinal);

        // 1. The record kept them.
        Assert.Contains($"evidence: {ContractEvidence}", stored, StringComparison.Ordinal);
        Assert.Contains($"gaps: {ContractGaps}", stored, StringComparison.Ordinal);
        Assert.Contains(ContractHeadline, stored, StringComparison.Ordinal);

        // 2. Bounded, not unbounded — the long bullet was clipped ON ITS OWN, so one verbose bullet
        //    cannot crowd out the fields that follow it.
        Assert.True(stored.Length < printed.Length, $"stored={stored.Length} printed={printed.Length}");
        Assert.DoesNotContain(new string('p', 400), stored, StringComparison.Ordinal);
        Assert.Contains(ContractSecondBullet, stored, StringComparison.Ordinal);

        // 3. What Telegram would send is bounded by dropping whole fields, and still carries the
        //    headline — where before it was this same paragraph cut blind a second time.
        var compact = SessionResult.Parse(stored).ToCompact(700);
        Assert.True(compact.Length <= 700, $"compact is {compact.Length}");
        Assert.Contains(ContractHeadline, compact, StringComparison.Ordinal);

        // 4. And REPORT.md shows fields instead of one blockquoted paragraph.
        var reportPath = Directory.GetFiles(_repo, "REPORT.md", SearchOption.AllDirectories).FirstOrDefault();
        Assert.NotNull(reportPath);
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains($"> **{ContractHeadline}**", report, StringComparison.Ordinal);
        Assert.Contains($"> evidence: {ContractEvidence}", report, StringComparison.Ordinal);
    }
}
