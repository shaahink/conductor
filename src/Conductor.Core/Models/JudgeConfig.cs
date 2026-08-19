using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

/// <summary>
/// KS4.5 — the advisory second-model review. A separate block from <see cref="AdvisorConfig"/> on
/// purpose, and not a mode of it: the advisor is asked what the run should DO and its answer moves
/// the run, while the judge is asked what the work IS WORTH and its answer moves nothing. Sharing one
/// block would have made that difference a matter of which call site read it.
/// <para>OFF by default, unlike the advisor. A judge costs a model spawn on every delivery and buys
/// no decision, so a plan that has not asked for one does not pay for one.</para>
/// </summary>
public sealed class JudgeConfig
{
    /// <summary>Same shipped shape as the advisor's, and for the same reason (SC3.4): a CLI spawned
    /// with no arguments is handed no question and answers nothing until its timeout.</summary>
    public static IReadOnlyList<string> DefaultArgs { get; } = ["-p", "{prompt}"];

    /// <summary>The only keys the engine reads. Anything else in the judge block is refused at plan
    /// load rather than ignored — bug 7's lesson, applied to the block before it can repeat it.</summary>
    public static IReadOnlyList<string> KnownFields { get; } =
        ["enabled", "command", "args", "output", "timeoutMinutes", "focus"];

    /// <summary>Off unless the plan says otherwise.</summary>
    public bool Enabled { get; set; }

    public string Command { get; set; } = "claude";

    public List<string> Args { get; set; } = [.. DefaultArgs];

    /// <summary>"text" | "json" | "stream-json" — the transport envelope to peel, same vocabulary as
    /// <see cref="AdvisorConfig.OutputKinds"/>, which is the list this is validated against.</summary>
    public string Output { get; set; } = "text";

    public int TimeoutMinutes { get; set; } = 6;

    /// <summary>Optional extra sentence handed to the judge — what this plan wants a second pair of
    /// eyes on. Prose, so it is brace-validated like every other authored string that reaches a
    /// prompt (SC3.3).</summary>
    public string? Focus { get; set; }

    /// <summary>Keys the judge block does not have, kept so plan load can name them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
