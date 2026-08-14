using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS2.0 — the inject path, pinned. `inject` is the one channel a human has to steer a live run, and
/// the devcontext field note §23 measured a 2,919-character instruction arriving in the queue as a
/// 343-byte file whose text ended at the first newline, under a green success line that said nothing.
/// These tests hold the two halves of the answer: the whole argument is stored, and the line the
/// operator reads states how much of it was.
/// </summary>
public class InjectCommandTests
{
    /// <summary>The field note's own probe: three lines of content, one of them after a blank line.</summary>
    private const string Probe =
        "PROBE-alpha line one\nPROBE-beta line two no blank before\n\nPROBE-gamma line four after a blank";

    private static PlanConfig PlanIn(string repo) => new() { Name = "T", Repo = repo };

    private static string StoredText(PlanConfig plan, string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstructionQueue.Dir(plan), file)));
        return doc.RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public void InjectStoresTheWholeInstruction()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var plan = PlanIn(repo);

            var entry = InjectCommand.Queue(plan, Probe);

            // The file on disk, not the return value: what the next session reads is what matters.
            Assert.Equal(Probe, StoredText(plan, entry.File), StringComparer.Ordinal);
            Assert.Equal(Probe, entry.Text, StringComparer.Ordinal);
            Assert.Equal(Probe, InstructionQueue.List(plan).Single().Text, StringComparer.Ordinal);

            // The slug is still first-line-only — the filename shortens, the instruction does not.
            Assert.Equal("001-probealpha-line-one.json", entry.File, StringComparer.Ordinal);
            Assert.DoesNotContain("probebeta", entry.File, StringComparison.Ordinal);
            Assert.DoesNotContain("probegamma", entry.File, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    [Fact]
    public void InjectEchoesTheStoredCount()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var plan = PlanIn(repo);
            // The field note's own size, to the character, so the thousands separator is exercised.
            var big = "Fix the gate harness first.\n\n" + new string('x', 2_890);
            Assert.Equal(2_919, big.Length);

            var entry = InjectCommand.Queue(plan, big);
            var line = InjectCommand.QueuedLine(entry);

            Assert.Contains("(2,919 chars)", line, StringComparison.Ordinal);
            Assert.Contains(entry.File, line, StringComparison.Ordinal);
            // The echoed count is the STORED count: read it back off disk and compare, because a
            // number computed from the argument would have said 2,919 in the field note too.
            var stored = StoredText(plan, entry.File);
            Assert.Contains(
                $"({stored.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} chars)",
                line, StringComparison.Ordinal);

            // And a one-line instruction reads the same way, so the count is not a multi-line special case.
            var small = InjectCommand.Queue(plan, "Prioritise the checkout truth test");
            Assert.Contains("(34 chars)", InjectCommand.QueuedLine(small), StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(repo); }
    }

    [Fact]
    public void TheNextSessionPromptCarriesEveryLine()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var plan = PlanIn(repo);
            InjectCommand.Queue(plan, Probe);
            InjectCommand.Queue(plan, "And then the second instruction");

            var section = InstructionQueue.PromptSection(plan);

            // Verbatim: the whole instruction, in one piece, under its numbered item.
            Assert.Contains($"1. [probealpha-line-one] {Probe}", section, StringComparison.Ordinal);
            Assert.Contains("2. [and-then-the-second-instruction] And then the second instruction",
                section, StringComparison.Ordinal);
            // A blank line between items, so the multi-line first one cannot read as part of the second.
            Assert.Contains("PROBE-gamma line four after a blank\n\n2. ",
                section.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(repo); }
    }
}
