using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS7.1 gate: the permission posture, asserted against the behaviour MEASURED on the installed CLI
/// (claude 2.1.235, print mode) rather than against what a settings doc implies.
///
/// <para>The measurements these tests encode, each one a live probe recorded in the KS7.1 evidence
/// file: <c>permissions.deny</c> is the only enforced boundary; it is enforced with the bypass flag
/// ON; <c>permissions.allow</c> pre-approves and gates nothing in print mode; a specifier-level deny
/// emits <c>{"type":"system","subtype":"permission_denied"}</c> on the stream, which is where the
/// refusal telemetry comes from.</para>
/// </summary>
public class KS7_1PermissionPostureTests
{
    // ── the config gate: an unknown mode is refused BY NAME, never quietly ignored

    [Theory]
    [InlineData(PermissionsConfig.ModeAcceptEdits)]
    [InlineData(PermissionsConfig.ModeAuto)]
    [InlineData(PermissionsConfig.ModeBypass)]
    [InlineData(PermissionsConfig.ModeManual)]
    [InlineData(PermissionsConfig.ModeDontAsk)]
    [InlineData(PermissionsConfig.ModePlan)]
    public void EverySpellingTheInstalledCliAcceptsIsAccepted(string mode) =>
        Assert.Null(new PermissionsConfig { Mode = mode }.ModeRefusal());

    [Fact]
    public void AnAbsentModeIsNotAnError()
    {
        Assert.Null(new PermissionsConfig().ModeRefusal());
        Assert.Null(new PermissionsConfig { Mode = "  " }.ModeRefusal());
    }

    /// <summary>The refusal must name the value that was typed and the values that exist. A typo the
    /// CLI silently ignores yields a run reporting a posture it is not under — and print mode already
    /// hides a settings file that fails to validate, so this is the only place it can be caught.</summary>
    [Fact]
    public void AnUnknownModeIsRefusedByName()
    {
        var refusal = new PermissionsConfig { Mode = "acceptedits" }.ModeRefusal();

        Assert.NotNull(refusal);
        Assert.Contains("acceptedits", refusal, StringComparison.Ordinal);
        Assert.Contains(PermissionsConfig.ModeAcceptEdits, refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanLoadRefusesAnUnknownModeAtBothLevels()
    {
        var plan = MinimalPlan();
        plan.Agent.Permissions = new PermissionsConfig { Mode = "yolo" };
        plan.Stages[0].Agent = new AgentConfig { Permissions = new PermissionsConfig { Mode = "nope" } };

        var errors = plan.CollectErrors();

        Assert.Contains(errors, e => e.Contains("yolo", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("nope", StringComparison.Ordinal) && e.Contains("S1", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanLoadAcceptsAKnownModeAtBothLevels()
    {
        var plan = MinimalPlan();
        plan.Agent.Permissions = new PermissionsConfig { Mode = PermissionsConfig.ModeAcceptEdits };
        plan.Stages[0].Agent = new AgentConfig { Permissions = new PermissionsConfig { Mode = PermissionsConfig.ModePlan } };

        Assert.DoesNotContain(plan.CollectErrors(), e => e.Contains("permission", StringComparison.OrdinalIgnoreCase));
    }

    // ── the settings fragment: only what was asked for

    [Fact]
    public void AnEmptyBlockWritesNoSettingsAtAll()
    {
        Assert.Null(PermissionPosture.SettingsFragment(null));
        Assert.Null(PermissionPosture.SettingsFragment(new PermissionsConfig()));
    }

    /// <summary>An unset list must not be emitted as <c>[]</c>: to anyone auditing the profile, an
    /// empty deny array reads as "considered and left empty", which is a different claim from "not
    /// configured".</summary>
    [Fact]
    public void OnlyTheMembersThatWereConfiguredAreEmitted()
    {
        var frag = PermissionPosture.SettingsFragment(new PermissionsConfig { Deny = ["Bash(curl:*)"] });

        Assert.NotNull(frag);
        Assert.True(frag.ContainsKey("deny"));
        Assert.False(frag.ContainsKey("allow"));
        Assert.False(frag.ContainsKey("defaultMode"));
    }

    [Fact]
    public void AModeIsWrittenAsDefaultModeInTheFile()
    {
        var frag = PermissionPosture.SettingsFragment(new PermissionsConfig
        {
            Mode = PermissionsConfig.ModeAcceptEdits,
            Allow = ["Bash(git status:*)"],
            Deny = ["WebFetch", "Bash(curl:*)"],
        });

        Assert.NotNull(frag);
        Assert.Equal(PermissionsConfig.ModeAcceptEdits, frag["defaultMode"]);
        Assert.Equal(new[] { "WebFetch", "Bash(curl:*)" }, Assert.IsType<string[]>(frag["deny"]));
        Assert.Equal(new[] { "Bash(git status:*)" }, Assert.IsType<string[]>(frag["allow"]));
    }

    // ── the command line: what it gains

    [Fact]
    public void NoModeAddsNoFlag()
    {
        Assert.Empty(PermissionPosture.ExtraArgs(null, ["-p", "{prompt}"]));
        Assert.Empty(PermissionPosture.ExtraArgs(new PermissionsConfig { Deny = ["WebFetch"] }, ["-p", "{prompt}"]));
    }

    [Fact]
    public void AConfiguredModeBecomesTheFlag()
    {
        var args = PermissionPosture.ExtraArgs(
            new PermissionsConfig { Mode = PermissionsConfig.ModeAcceptEdits }, ["-p", "{prompt}"]);

        Assert.Equal(["--permission-mode", PermissionsConfig.ModeAcceptEdits], args);
    }

    /// <summary>Same rule --mcp-config and --settings already follow: a plan that names its own flag
    /// keeps it rather than receiving a second, conflicting one.</summary>
    [Fact]
    public void APlansOwnPermissionModeIsLeftAlone()
    {
        var args = PermissionPosture.ExtraArgs(
            new PermissionsConfig { Mode = PermissionsConfig.ModeAcceptEdits },
            ["-p", "{prompt}", "--permission-mode", "plan"]);

        Assert.Empty(args);
    }

    [Fact]
    public void McpArgsCarryThePermissionModeAlongsideTheRest()
    {
        var args = SessionRunner.McpArgsFor("claude", ["-p", "{prompt}"], "mcp.json", "settings.session.json",
            new PermissionsConfig { Mode = PermissionsConfig.ModeAcceptEdits });

        Assert.Contains("--mcp-config", args, StringComparer.Ordinal);
        Assert.Contains("--settings", args, StringComparer.Ordinal);
        Assert.Contains("--permission-mode", args, StringComparer.Ordinal);
        Assert.Contains(PermissionsConfig.ModeAcceptEdits, args, StringComparer.Ordinal);
    }

    // ── the command line: what it loses

    /// <summary>Both spellings go. --allow-dangerously-skip-permissions only OFFERS the bypass, but
    /// leaving it on a restricted profile's command line advertises an escape hatch the posture says
    /// is closed.</summary>
    [Fact]
    public void ARestrictedModeStripsEveryBypassSpelling()
    {
        var args = new List<string>
        {
            "-p", "{prompt}", "--dangerously-skip-permissions",
            "--allow-dangerously-skip-permissions", "--verbose",
        };

        var stripped = PermissionPosture.StripBypass(args,
            new PermissionsConfig { Mode = PermissionsConfig.ModeAcceptEdits });

        Assert.Equal(["-p", "{prompt}", "--verbose"], stripped);
    }

    /// <summary>A posture that ASKS for bypass keeps it — the posture is a statement of intent, and
    /// stripping a flag the plan and the posture agree on would be conductor overruling both.</summary>
    [Fact]
    public void ABypassPostureKeepsTheFlag()
    {
        var args = new List<string> { "-p", "--dangerously-skip-permissions" };

        var kept = PermissionPosture.StripBypass(args, new PermissionsConfig { Mode = PermissionsConfig.ModeBypass });

        Assert.Contains("--dangerously-skip-permissions", kept, StringComparer.Ordinal);
    }

    /// <summary>The measured asymmetry, pinned: deny rules are enforced by the CLI even under the
    /// bypass flag, so a deny-only posture must NOT touch a plan's command line. Conductor changes
    /// only what the operator asked it to change.</summary>
    [Fact]
    public void ADenyOnlyPostureLeavesTheCommandLineExactlyAsItWas()
    {
        var args = new List<string> { "-p", "{prompt}", "--dangerously-skip-permissions" };

        var same = PermissionPosture.StripBypass(args, new PermissionsConfig { Deny = ["Bash(curl:*)"] });

        Assert.Same(args, same);
    }

    [Fact]
    public void NoPostureIsTheIdentityOnTheCommandLine()
    {
        var args = new List<string> { "-p", "--dangerously-skip-permissions" };

        Assert.Same(args, PermissionPosture.StripBypass(args, null));
    }

    [Fact]
    public void TheLogLineStatesTheModeTheRuleCountsAndWhatWasStripped()
    {
        var line = PermissionPosture.Describe(new PermissionsConfig
        {
            Mode = PermissionsConfig.ModeAcceptEdits,
            Deny = ["WebFetch", "Bash(curl:*)"],
        }, strippedBypassFlags: 1);

        Assert.Contains(PermissionsConfig.ModeAcceptEdits, line, StringComparison.Ordinal);
        Assert.Contains("2 deny rule(s)", line, StringComparison.Ordinal);
        Assert.Contains("1 bypass flag(s) stripped", line, StringComparison.Ordinal);
    }

    // ── merge: a stage overrides the plan field by field

    [Fact]
    public void AStagePostureOverridesThePlansFieldByField()
    {
        var basePlan = new AgentConfig
        {
            Permissions = new PermissionsConfig
            {
                Mode = PermissionsConfig.ModeBypass,
                Deny = ["WebFetch"],
                Allow = ["Read"],
            },
        };

        var merged = basePlan.Merge(new AgentConfig
        {
            Permissions = new PermissionsConfig { Mode = PermissionsConfig.ModeAcceptEdits },
        });

        Assert.Equal(PermissionsConfig.ModeAcceptEdits, merged.Permissions!.Mode);
        Assert.Equal(["WebFetch"], merged.Permissions.Deny);
        Assert.Equal(["Read"], merged.Permissions.Allow);
    }

    [Fact]
    public void AStagePostureSurvivesWhenThePlanHasNone()
    {
        var merged = new AgentConfig().Merge(new AgentConfig
        {
            Permissions = new PermissionsConfig { Deny = ["Bash(curl:*)"] },
        });

        Assert.Equal(["Bash(curl:*)"], merged.Permissions!.Deny);
    }

    // ── refusal telemetry: the wire event, parsed

    /// <summary>The envelope is the one the installed CLI actually emitted during the KS7.1 probe,
    /// copied verbatim from the captured stream — not a shape invented to match the parser.</summary>
    private const string RealRefusalLine =
        """
        {"type":"system","subtype":"permission_denied","tool_name":"Bash","tool_use_id":"toolu_01WZuUvL3VBPxSbjfjRRbzae","decision_reason_type":"subcommandResults","message":"Permission to use Bash with command git status --short has been denied."}
        """;

    [Fact]
    public void ARefusedCallIsParsedIntoItsOwnChannelWithTheToolAndTheReason()
    {
        var emitted = new List<(string Kind, string Text)>();
        var state = new AgentStreamState((k, t) => emitted.Add((k, t)));

        new ClaudeProvider().ParseLine(RealRefusalLine, state);

        var refusal = Assert.Single(state.Refusals);
        Assert.Equal("Bash", refusal.ToolName);
        Assert.Equal("subcommandResults", refusal.ReasonType);
        Assert.Contains("git status --short", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("refusal", Assert.Single(emitted).Kind);
    }

    /// <summary>A refusal must not arrive as a bare "system permission_denied" line: that spelling
    /// throws away the only field that says WHAT was refused, and a run then cannot tell "the deny
    /// list bit twice" from "the deny list never loaded".</summary>
    [Fact]
    public void ARefusedCallNeverLandsOnThePlainSystemChannel()
    {
        var emitted = new List<(string Kind, string Text)>();
        var state = new AgentStreamState((k, t) => emitted.Add((k, t)));

        new ClaudeProvider().ParseLine(RealRefusalLine, state);

        Assert.DoesNotContain(emitted, e => string.Equals(e.Kind, "system", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRefusalSinkReceivesTheStructuredRefusal()
    {
        var sunk = new List<ToolRefusal>();
        var state = new AgentStreamState((_, _) => { }, onRefusal: sunk.Add);

        new ClaudeProvider().ParseLine(RealRefusalLine, state);

        Assert.Equal("Bash", Assert.Single(sunk).ToolName);
    }

    [Fact]
    public void ASessionWithNoRefusalsReportsNone()
    {
        var state = new AgentStreamState((_, _) => { });

        new ClaudeProvider().ParseLine("""{"type":"system","subtype":"init","permissionMode":"acceptEdits"}""", state);

        Assert.Empty(state.Refusals);
    }

    /// <summary>Every refusal is kept, in order, including repeats of the same rule — the session
    /// report counts them, and collapsing duplicates here would make "x3" unrecoverable.</summary>
    [Fact]
    public void RepeatedRefusalsAreAllKept()
    {
        var state = new AgentStreamState((_, _) => { });
        var provider = new ClaudeProvider();

        provider.ParseLine(RealRefusalLine, state);
        provider.ParseLine(RealRefusalLine, state);

        Assert.Equal(2, state.Refusals.Count);
    }

    /// <summary>An envelope missing the optional fields still yields a usable refusal rather than an
    /// exception — the parser must never be the reason a restricted run dies.</summary>
    [Fact]
    public void AThinRefusalEnvelopeStillParses()
    {
        var state = new AgentStreamState((_, _) => { });

        new ClaudeProvider().ParseLine("""{"type":"system","subtype":"permission_denied"}""", state);

        var refusal = Assert.Single(state.Refusals);
        Assert.Equal("tool", refusal.ToolName);
        Assert.Null(refusal.ReasonType);
        Assert.Equal("tool refused", refusal.Line);
    }

    private static PlanConfig MinimalPlan() => new()
    {
        Repo = Directory.GetCurrentDirectory(),
        Stages = [new StageConfig { Id = "S1", Title = "one" }],
    };
}
