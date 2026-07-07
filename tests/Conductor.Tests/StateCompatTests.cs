using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// Guards that the live Loom run (already on L1, L0 confirmed+audited) still deserializes with the
/// v2 model — i.e. adding fields never breaks resuming an in-flight plan. The JSON mirrors the real
/// .conductor/state.json schema, including get-only computed fields that are serialized but ignored.
/// </summary>
public class StateCompatTests
{
    private const string LiveState = """
    {
      "planName": "Loom",
      "status": "running",
      "currentStage": "L1",
      "currentStageStartHead": "9386bb6608cc52fac9535cf4a9b4c88262b402dc",
      "sessionCounter": 5,
      "attemptsThisStage": 0,
      "consecutiveBackoffs": 0,
      "stopAfterSession": false,
      "skippedStages": [],
      "confirmedStages": [ "L0" ],
      "auditedStages": [ "L0" ],
      "history": [
        {
          "number": 4, "stage": "L0", "kind": "audit",
          "startedUtc": "2026-07-07T18:24:27.8289794Z",
          "endedUtc": "2026-07-07T18:56:15.4693343Z",
          "outcome": "progress",
          "claudeSessionId": "c46ae99a-e880-4d39-92b3-9964305297a1",
          "resumeCount": 0, "newCommits": [], "newlyDone": [],
          "costUsd": 0.053532347, "numTurns": 56,
          "tokensInput": 58007, "tokensOutput": 12076, "tokensReasoning": 7165, "tokensCacheRead": 3188864,
          "attempt": 1, "resultSummary": "SESSION-RESULT: L0 audit pass"
        },
        {
          "number": 5, "stage": "L1", "kind": "deliver",
          "startedUtc": "2026-07-07T19:02:42.3650585Z",
          "claudeSessionId": "cc33d498-6f2a-45cf-8bb1-b8db86c30a0d",
          "resumeCount": 0, "newCommits": [], "newlyDone": [], "attempt": 1
        }
      ],
      "updatedUtc": "2026-07-07T19:02:42.4102756Z",
      "totalCostUsd": 0.053532347,
      "totalTokensInput": 58007,
      "totalTokensOutput": 12076,
      "totalTokensReasoning": 7165
    }
    """;

    [Fact]
    public void LiveL1StateResumesUnderV2Model()
    {
        var path = Path.Combine(Path.GetTempPath(), $"conductor-compat-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, LiveState);
            var s = RunState.LoadOrNew(path, "Loom");

            Assert.Equal(RunStatus.Running, s.Status);
            Assert.Equal("L1", s.CurrentStage);
            Assert.Equal(5, s.SessionCounter);
            Assert.Contains("L0", s.ConfirmedStages);
            Assert.Contains("L0", s.AuditedStages);
            Assert.Equal(2, s.History.Count);
            Assert.Equal(SessionKind.Audit, s.History[0].Kind);
            Assert.Equal(SessionKind.Deliver, s.History[1].Kind);
            // computed totals recomputed from history (get-only serialized field ignored on load)
            Assert.Equal(0.053532347m, s.TotalCostUsd);
            Assert.Equal(58007, s.TotalTokensInput);
            // one finished session (#4) has cost, #5 is running (no EndedUtc) → 0 untracked finished
            Assert.Equal(0, s.History.Count(h => h.EndedUtc != null && h.CostUsd == null));
        }
        finally { File.Delete(path); }
    }
}
