using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Models;

public sealed class AdvisorConfig
{
    /// <summary>SC3.4: the shipped default is a WORKING headless invocation. It used to be an empty
    /// list, which spawned <c>claude</c> with no arguments at all — an interactive session that is
    /// handed no question, waits on stdin for up to <see cref="TimeoutMinutes"/>, and answers
    /// nothing (devcontext #3). Whatever CLI answers, its args must carry <c>{prompt}</c>.</summary>
    public static IReadOnlyList<string> DefaultArgs { get; } = ["-p", "{prompt}"];

    /// <summary>The only keys the engine reads. Anything else in the advisor block is refused at
    /// plan load rather than ignored — see the extension bucket below.</summary>
    public static IReadOnlyList<string> KnownFields { get; } =
        ["enabled", "command", "args", "output", "timeoutMinutes", "remediationScript"];

    /// <summary>Every envelope <c>Advisor.UnwrapEnvelope</c> knows how to peel. An unlisted kind is
    /// passed through raw, so a typo here means the answer arrives still wrapped.</summary>
    public static IReadOnlyList<string> OutputKinds { get; } = ["text", "json", "stream-json"];

    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "claude";
    public List<string> Args { get; set; } = [.. DefaultArgs];
    /// <summary>"text" (raw stdout), "json" (claude <c>-p --output-format json</c> envelope), or
    /// "stream-json" (NDJSON whose final result line carries the answer).</summary>
    public string Output { get; set; } = "text";
    public int TimeoutMinutes { get; set; } = 6;
    /// <summary>P3: optional shell command run when the advisor returns ApplyFix.
    /// Example: "taskkill /f /im opencode.exe" or "git clean -fdx". Executed via
    /// the default shell with a 5-minute timeout; runs in the repo root.</summary>
    public string? RemediationScript { get; set; }

    /// <summary>SC3.4 / bug 7: keys the advisor block does not have, kept so plan load can name them.
    /// <c>advisor.provider</c> was set in five shipped plans, copied from the agent block, where it
    /// really does select an adapter — here nothing reads it, so a plan could say one model answers
    /// while another one did. Unknown keys are reported by <c>PlanConfig.CollectErrors</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }

    public static bool IsKnownOutput(string? kind)
        => kind is not null && OutputKinds.Contains(kind, StringComparer.OrdinalIgnoreCase);
}
