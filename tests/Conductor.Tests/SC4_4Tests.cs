using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC4.4 — injections outrank stale evidence. devcontext #15: a human queued a correction for a fix
/// session and the engine rendered it 113 lines BELOW the gate output it was correcting, because the
/// queue section was APPENDED to the composed prompt while the evidence sat at the top. The agent read
/// them in order and worked the evidence. Two rules are measured here: an injection renders directly
/// under the role line, above everything the engine composed; and on a fix prompt the gateFailures
/// block it outranks says so in its own text, so the two never read as peers.
/// </summary>
public sealed class SC4_4Tests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("sc44").FullName;

    private PlanConfig Plan() => new()
    {
        Name = "Loom",
        Repo = _repo,
        Tracker = "LOOM-START.md",
        PlanDoc = "docs/proposal.md",
        PromptExtra = "EXTRA-MARKER",
    };

    private static readonly StageConfig Stage = new() { Id = "L2", Title = "BodyFacts", Sessions = 3, Notes = "watch the anchoring" };

    private const string Marker = "QUEUED INSTRUCTIONS";

    private static int LineOf(string text, string needle)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle, StringComparison.Ordinal)) return i + 1;
        return -1;
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { /* temp dir */ }
    }

    // ------------------------------------------------------------------ position

    /// <summary>The regression itself: the injection must be the FIRST thing after the role line —
    /// above the required reading, the ritual, the tools contract and the stage notes.</summary>
    [Fact]
    public void InjectionRendersDirectlyUnderTheRoleLineOfADeliverPrompt()
    {
        var plan = Plan();
        plan.ReadOrder = ["docs/ARCH.md"];
        InstructionQueue.Write(plan, "Stop chasing the flake, deliver L2.3", null);

        var prompt = new PromptBuilder(plan).Deliver(Stage, 5, 2, 6);

        var lines = prompt.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.StartsWith("You are one autonomous engineering session", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, LineOf(prompt, Marker));                    // role line, blank, injection
        Assert.Contains("Stop chasing the flake, deliver L2.3", prompt, StringComparison.Ordinal);
        // …and everything the prompt used to put above it is now below it. The anchors are headings the
        // CURRENT built-in has: SF6.1 rewrote the template and the old "PRE-SESSION RITUAL" heading became
        // step 1 of "Do, in order:", which turned this assertion into a -1 that read as a real regression.
        Assert.InRange(LineOf(prompt, "Required reading (in order):"), 4, int.MaxValue);
        Assert.InRange(LineOf(prompt, "Do, in order:"), 4, int.MaxValue);            // the ritual, post-SF6.1
        Assert.InRange(LineOf(prompt, "ORIENT, THEN SAY WHAT YOU ARE TAKING"), 4, int.MaxValue);
        Assert.InRange(LineOf(prompt, "## Conductor tools"), 4, int.MaxValue);       // the spliced tools contract
        Assert.InRange(LineOf(prompt, "watch the anchoring"), 4, int.MaxValue);
    }

    /// <summary>Same rule on the prompt that provoked it. The gate output is the block an injection is
    /// most often there to override, so it is the block that must not sit above one.</summary>
    [Fact]
    public void InjectionRendersAboveTheGateOutputOnAFixPrompt()
    {
        var plan = Plan();
        InstructionQueue.Write(plan, "The build gate is broken in CI, not in the code - do not touch the build", null);
        var fix = new PendingFix { FromSession = 4, GateFailures = "### Gate `build` FAILED (exit 1)", ProgressSummary = "commits: 0" };

        var prompt = new PromptBuilder(plan).Fix(Stage, 5, 3, 6, fix);

        Assert.Equal(3, LineOf(prompt, Marker));
        Assert.InRange(LineOf(prompt, "Gate `build` FAILED"), LineOf(prompt, Marker) + 1, int.MaxValue);
    }

    /// <summary>A persona is a role definition, so it may precede the injection — but it is the only
    /// thing that may, and the injection still lands under the template's role line rather than
    /// inside the persona's body.</summary>
    [Fact]
    public void APersonaSystemPromptIsTheOnlyBlockAllowedAboveAnInjection()
    {
        var plan = Plan();
        var personasDir = Path.Combine(_repo, "personas");
        Directory.CreateDirectory(personasDir);
        File.WriteAllText(Path.Combine(personasDir, "deliver.md"), "PERSONA-LINE-ONE\nPERSONA-LINE-TWO");
        InstructionQueue.Write(plan, "Deliver L2.3 first", null);

        var prompt = new PromptBuilder(plan, new PersonaRegistry(personasDir)).Deliver(Stage, 1, 1, 1, personaOverride: "deliver");

        Assert.Equal(1, LineOf(prompt, "PERSONA-LINE-ONE"));
        Assert.Equal(2, LineOf(prompt, "PERSONA-LINE-TWO"));          // persona body stays intact
        var role = LineOf(prompt, "You are one autonomous engineering session");
        Assert.Equal(role + 2, LineOf(prompt, Marker));               // still directly under the role line
    }

    /// <summary>No queue, no section, no reshuffle: an ordinary prompt is byte-identical to what the
    /// templates render, so this change costs nothing when nobody has injected anything.</summary>
    [Fact]
    public void AnEmptyQueueLeavesThePromptUntouched()
    {
        var prompt = new PromptBuilder(Plan()).Deliver(Stage, 1, 1, 1);

        Assert.DoesNotContain(Marker, prompt, StringComparison.Ordinal);
        Assert.StartsWith("You are one autonomous engineering session", prompt, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ supersession

    /// <summary>The second half: the gate output must not read as a peer instruction. It keeps its
    /// content — nothing is deleted — but it carries the stamp that names what outranks it.</summary>
    [Fact]
    public void AQueuedInjectionStampsTheFixPromptsGateFailuresBlockSuperseded()
    {
        var plan = Plan();
        InstructionQueue.Write(plan, "Skip the gate, it is known-bad", null);
        InstructionQueue.Write(plan, "Then update the tracker", null);
        var fix = new PendingFix { FromSession = 4, GateFailures = "### Gate `build` FAILED (exit 1)", ProgressSummary = "commits: 0" };

        var prompt = new PromptBuilder(plan).Fix(Stage, 5, 3, 6, fix);

        Assert.Contains("SUPERSEDED", prompt, StringComparison.Ordinal);
        Assert.Contains("2 human instructions are queued", prompt, StringComparison.Ordinal);
        // The stamp sits WITH the evidence it demotes, between the intro line and the gate text.
        var stamp = LineOf(prompt, "SUPERSEDED");
        Assert.InRange(stamp, LineOf(prompt, Marker) + 1, LineOf(prompt, "Gate `build` FAILED") - 1);
        Assert.Contains("Gate `build` FAILED", prompt, StringComparison.Ordinal);   // nothing dropped
    }

    /// <summary>Singular reads as English, and the stamp counts what is actually queued.</summary>
    [Fact]
    public void TheStampCountsTheQueuedInstructions()
    {
        Assert.Contains("1 human instruction is queued", InstructionQueue.SupersedeStamp(1), StringComparison.Ordinal);
        Assert.Contains("3 human instructions are queued", InstructionQueue.SupersedeStamp(3), StringComparison.Ordinal);
    }

    /// <summary>Without an injection there is nothing to outrank, so the gate output stands unstamped —
    /// the ordinary fix prompt must not start crying wolf.</summary>
    [Fact]
    public void AnEmptyQueueLeavesTheGateFailuresBlockUnstamped()
    {
        var fix = new PendingFix { FromSession = 4, GateFailures = "### Gate `build` FAILED (exit 1)", ProgressSummary = "commits: 0" };

        var prompt = new PromptBuilder(Plan()).Fix(Stage, 5, 3, 6, fix);

        Assert.DoesNotContain("SUPERSEDED", prompt, StringComparison.Ordinal);
        Assert.Contains("Gate `build` FAILED", prompt, StringComparison.Ordinal);
    }

    /// <summary>The queue section states the rank in words as well as by position — a model that reads
    /// the prompt out of order still learns which block wins.</summary>
    [Fact]
    public void TheQueueSectionStatesThatItOutranksWhatFollows()
    {
        var plan = Plan();
        InstructionQueue.Write(plan, "Do the thing", null);

        var section = InstructionQueue.PromptSection(plan);

        Assert.Contains("OUTRANK", section, StringComparison.Ordinal);
        Assert.Contains("1. [do-the-thing] Do the thing", section, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the splice itself

    [Theory]
    [InlineData("You are X.\nBody line.", "\n")]
    [InlineData("You are X.\r\nBody line.", "\r\n")]
    public void TheSpliceKeepsTheTemplatesOwnLineEnding(string text, string eol)
    {
        var spliced = PromptBuilder.InsertAfterRoleLine(text, "BLOCK");

        Assert.Equal($"You are X.{eol}{eol}BLOCK{eol}{eol}Body line.", spliced);
    }

    /// <summary>A one-line template has no "after the role line" — appending is the same position, and
    /// must not throw.</summary>
    [Fact]
    public void ASingleLineTemplateGetsTheBlockAppended()
        => Assert.Equal("You are X.\n\nBLOCK", PromptBuilder.InsertAfterRoleLine("You are X.", "BLOCK"));
}
