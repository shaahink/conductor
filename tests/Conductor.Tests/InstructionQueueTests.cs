using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class InstructionQueueTests
{
    private static PlanConfig PlanIn(string repo) => new() { Name = "T", Repo = repo };

    [Fact]
    public void WriteListConsumeRoundTrip()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var plan = PlanIn(repo);
            Assert.Empty(InstructionQueue.List(plan));

            var a = InstructionQueue.Write(plan, "Prioritise the checkout truth test", null);
            var b = InstructionQueue.Write(plan, "Also refresh the evidence file", a.File);

            var list = InstructionQueue.List(plan);
            Assert.Equal(2, list.Count);
            Assert.Equal("Prioritise the checkout truth test", list[0].Text);
            Assert.Equal(a.File, list[1].Prev);      // b links back to a
            Assert.Equal(b.File, list[0].Next);      // a links forward to b (chain)

            var section = InstructionQueue.PromptSection(plan);
            Assert.Contains("QUEUED INSTRUCTIONS", section);
            Assert.Contains("Prioritise the checkout truth test", section);
            Assert.Contains("Also refresh the evidence file", section);

            InstructionQueue.ConsumeAll(plan);
            Assert.Empty(InstructionQueue.List(plan));               // consumed → not active
            Assert.Equal("", InstructionQueue.PromptSection(plan));  // nothing to inject next time
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public void EmptyQueueProducesNoPromptSection()
    {
        var repo = Directory.CreateTempSubdirectory().FullName;
        try { Assert.Equal("", InstructionQueue.PromptSection(PlanIn(repo))); }
        finally { Directory.Delete(repo, recursive: true); }
    }
}
