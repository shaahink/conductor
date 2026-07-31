using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC3.4 — the advisor works by default or is refused loudly. The shipped default used to be an
/// EMPTY arg list, so the documented advisor (<c>"advisor": { "enabled": true, "command": "claude" }</c>)
/// spawned a CLI with no question at all: it answered nothing, every consult fell back to the
/// deterministic default, and the only trace was one grey log line (devcontext #3). Measured here:
/// the default invocation carries the prompt, an invocation that cannot answer is refused at plan
/// load, and doctor tells you which model your second brain actually is.
/// </summary>
public sealed class SC3_4AdvisorTests
{
    private static PlanConfig PlanWithAdvisor(string advisorJson)
    {
        var json = $$"""
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "advisor": {{advisorJson}},
          "stages": [ { "id": "T0", "title": "t", "sessions": 1 } ]
        }
        """;
        return JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts)!;
    }

    private static List<string> AdvisorErrors(PlanConfig plan)
        => [.. plan.CollectErrors().Where(e => e.Contains("advisor", StringComparison.Ordinal))];

    // ------------------------------------------------------------------ the default

    /// <summary>The headline: a plan that names the advisor and leaves the invocation to us gets an
    /// invocation that works — headless, one-shot, question on argv.</summary>
    [Fact]
    public void AnAdvisorBlockThatOmitsArgsGetsAWorkingHeadlessInvocation()
    {
        var plan = PlanWithAdvisor("""{ "enabled": true, "command": "claude" }""");

        Assert.NotEmpty(plan.Advisor!.Args);
        Assert.Contains(plan.Advisor.Args, a => a.Contains("{prompt}", StringComparison.Ordinal));
        Assert.Empty(AdvisorErrors(plan));
    }

    /// <summary>The default output kind has to be one the unwrapper knows, or the default invocation
    /// answers into a envelope nothing opens.</summary>
    [Fact]
    public void TheDefaultOutputKindIsOneTheUnwrapperKnows()
        => Assert.True(AdvisorConfig.IsKnownOutput(new AdvisorConfig().Output));

    /// <summary>Pins the vocabulary to the code that consumes it: every kind the validator accepts is
    /// a kind <see cref="Advisor.UnwrapEnvelope"/> actually handles. Without this the two lists drift
    /// and a plan passes load with an envelope that arrives unopened.</summary>
    [Fact]
    public void EveryAcceptedOutputKindIsActuallyUnwrapped()
    {
        Assert.Equal("the answer", Advisor.UnwrapEnvelope("the answer", "text"));
        Assert.Equal("the answer", Advisor.UnwrapEnvelope("""{"result":"the answer"}""", "json"));
        Assert.Equal("the answer", Advisor.UnwrapEnvelope(
            """{"type":"system"}""" + "\n" + """{"type":"result","result":"the answer"}""", "stream-json"));
        Assert.Equal(3, AdvisorConfig.OutputKinds.Count);
    }

    // ------------------------------------------------------------------ refused at load

    [Fact]
    public void ExplicitlyEmptyArgsIsRefusedAtLoadNamingTheField()
    {
        var errors = AdvisorErrors(PlanWithAdvisor("""{ "enabled": true, "command": "claude", "args": [] }"""));

        Assert.Contains(errors, e => e.Contains("plan.advisor.args is empty", StringComparison.Ordinal)
                                  && e.Contains("answers nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void ArgsWithoutThePromptPlaceholderAreRefusedAtLoad()
    {
        var errors = AdvisorErrors(PlanWithAdvisor("""{ "command": "claude", "args": ["-p", "--output-format", "json"] }"""));

        Assert.Contains(errors, e => e.Contains("carries no {prompt} placeholder", StringComparison.Ordinal));
    }

    /// <summary>bug 7: <c>advisor.provider</c> was set in five shipped plans and read by nothing. It
    /// looks exactly like <c>agent.provider</c>, which does select an adapter — so the plan could claim
    /// one model was the second brain while another one answered.</summary>
    [Fact]
    public void AnUnknownAdvisorKeyIsRefusedAtLoadNamingItAndTheKnownFields()
    {
        var errors = AdvisorErrors(PlanWithAdvisor(
            """{ "command": "opencode", "args": ["run", "{prompt}"], "provider": "claude" }"""));

        var e = Assert.Single(errors);
        Assert.Contains("plan.advisor.provider is not an advisor field", e, StringComparison.Ordinal);
        Assert.Contains("remediationScript", e, StringComparison.Ordinal); // the known-field list is printed
        Assert.Contains("no provider adapter", e, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOutputKindIsRefusedAtLoad()
    {
        var errors = AdvisorErrors(PlanWithAdvisor("""{ "command": "claude", "args": ["-p", "{prompt}"], "output": "streamjson" }"""));

        Assert.Contains(errors, e => e.Contains("plan.advisor.output is 'streamjson'", StringComparison.Ordinal)
                                  && e.Contains("stream-json", StringComparison.Ordinal));
    }

    [Fact]
    public void AZeroTimeoutIsRefusedAtLoad()
    {
        var errors = AdvisorErrors(PlanWithAdvisor("""{ "command": "claude", "args": ["-p", "{prompt}"], "timeoutMinutes": 0 }"""));

        Assert.Contains(errors, e => e.Contains("plan.advisor.timeoutMinutes must be >= 1", StringComparison.Ordinal));
    }

    /// <summary>A disabled advisor is never spawned, so its invocation is nobody's business — but an
    /// unknown key still is: it is a claim about behaviour, and it would be just as wrong the day the
    /// block is switched back on.</summary>
    [Fact]
    public void ADisabledAdvisorIsJudgedOnlyOnKeysThatLie()
    {
        Assert.Empty(AdvisorErrors(PlanWithAdvisor("""{ "enabled": false, "command": "", "args": [] }""")));
        Assert.Contains(AdvisorErrors(PlanWithAdvisor("""{ "enabled": false, "provider": "claude" }""")),
            e => e.Contains("plan.advisor.provider", StringComparison.Ordinal));
    }

    /// <summary>No advisor block at all is a supported choice, not a defect.</summary>
    [Fact]
    public void NoAdvisorBlockIsNotAnError()
    {
        var plan = JsonSerializer.Deserialize<PlanConfig>("""
        {
          "name": "T", "repo": ".", "tracker": "t.md",
          "agent": { "command": "opencode", "args": ["run", "{prompt}"] },
          "stages": [ { "id": "T0", "title": "t", "sessions": 1 } ]
        }
        """, PlanConfig.JsonOpts)!;

        Assert.Null(plan.Advisor);
        Assert.Empty(AdvisorErrors(plan));
    }

    // ------------------------------------------------------------------ the spawn path

    /// <summary>The guard behind the load-time refusal: a PlanConfig built in code can still carry an
    /// argless advisor, and the answer is to say so, not to spawn a CLI that will sit there.</summary>
    [Fact]
    public async Task AnArglessAdvisorIsNotSpawnedAndSaysWhy()
    {
        var plan = new PlanConfig { Name = "T", Repo = Path.GetTempPath(), Tracker = "t.md" };
        plan.Advisor = new AdvisorConfig { Enabled = true, Command = "definitely-not-a-real-command-xyz123", Args = [] };
        var log = new List<string>();

        var answer = await Advisor.AskTextAsync(plan, "anything", log.Add);

        Assert.Null(answer);
        Assert.Contains(log, l => l.Contains("advisor.args is empty", StringComparison.Ordinal)
                               && l.Contains("-p {prompt}", StringComparison.Ordinal));
    }

    /// <summary>The other half of the same guarantee: when args DO carry the placeholder, the question
    /// reaches the CLI's argv — which is the whole difference between the default that works and the
    /// default that hung. Driven through a real spawn of a stand-in CLI.</summary>
    [Fact]
    public async Task ThePromptReachesTheCliArgvThroughTheSharedSpawnPath()
    {
        var plan = new PlanConfig { Name = "T", Repo = Path.GetTempPath(), Tracker = "t.md" };
        plan.Advisor = OperatingSystem.IsWindows()
            ? new AdvisorConfig { Command = "cmd", Args = ["/c", "echo", "{prompt}"], Output = "text", TimeoutMinutes = 1 }
            : new AdvisorConfig { Command = "/bin/sh", Args = ["-c", "echo \"$0\"", "{prompt}"], Output = "text", TimeoutMinutes = 1 };

        var answer = await Advisor.AskTextAsync(plan, "ADVISOR-OK", _ => { });

        Assert.Contains("ADVISOR-OK", answer ?? "", StringComparison.Ordinal);
    }

    /// <summary>The scaffold's advisor block ships with <c>"--model", "{model}"</c> so
    /// <c>plan import --model</c> can pick one. Every OTHER consult leaves that token unfilled, and
    /// passing it through literally asks the CLI for a model named <c>{model}</c> — the same silent
    /// nothing the argless default produced. Dropped, exactly as agent args drop it.</summary>
    [Fact]
    public void AnUnfilledModelTokenAndItsFlagAreDroppedNotPassedThrough()
    {
        var resolved = Advisor.ResolveArgs(["-p", "{prompt}", "--output-format", "json", "--model", "{model}"], "ASK");

        Assert.Equal(["-p", "ASK", "--output-format", "json"], resolved);
    }

    // ------------------------------------------------------------------ doctor

    private static PlanConfig DoctorPlan(AdvisorConfig? advisor)
        => new() { Name = "t", Repo = Path.GetTempPath(), Tracker = "TRACKER.md", Advisor = advisor };

    [Fact]
    public void CheckAdvisor_Ok_AndPrintsTheInvocation_WhenTheCliIsInstalled()
    {
        // git is a hard dependency of this whole tool, so it is a safe "definitely on PATH" stand-in.
        var check = DoctorCommand.CheckAdvisor(DoctorPlan(new AdvisorConfig { Command = "git", Args = ["log", "{prompt}"], Output = "text" }));

        Assert.Equal("ok", check.State);
        Assert.Equal("advisor", check.Name);
        Assert.Contains("git log {prompt}", check.Message, StringComparison.Ordinal);
        Assert.Contains("6m timeout", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAdvisor_Warn_WhenTheConfiguredCliIsNotInstalled()
    {
        var check = DoctorCommand.CheckAdvisor(DoctorPlan(
            new AdvisorConfig { Command = "definitely-not-a-real-command-xyz123", Args = ["-p", "{prompt}"] }));

        Assert.Equal("warn", check.State);
        Assert.Contains("not found on PATH", check.Message, StringComparison.Ordinal);
        Assert.Contains("deterministic default", check.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckAdvisor_Ok_WhenNoneConfiguredOrDisabled()
    {
        Assert.Equal("ok", DoctorCommand.CheckAdvisor(DoctorPlan(null)).State);
        Assert.Contains("not configured", DoctorCommand.CheckAdvisor(DoctorPlan(null)).Message, StringComparison.Ordinal);

        var off = DoctorCommand.CheckAdvisor(DoctorPlan(new AdvisorConfig { Enabled = false, Command = "claude" }));
        Assert.Equal("ok", off.State);
        Assert.Contains("disabled", off.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the shipped plans

    /// <summary>bug 7 lived in five plans in this repo. They are the worked examples people copy, so a
    /// key nothing reads is worse there than anywhere else — and after this checkpoint they would not
    /// even load.</summary>
    [Fact]
    public void NoShippedPlanCarriesAnAdvisorKeyNothingReads()
    {
        var plansDir = Path.Combine(RepoRoot(), "plans");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(plansDir, "*.plan.json"))
        {
            var plan = JsonSerializer.Deserialize<PlanConfig>(File.ReadAllText(file), PlanConfig.JsonOpts);
            if (plan?.Advisor?.UnknownFields is { Count: > 0 } unknown)
                offenders.Add($"{Path.GetFileName(file)}: {string.Join(", ", unknown.Keys)}");
        }

        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
