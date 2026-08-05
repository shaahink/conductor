namespace Conductor.Core.Events;

/// <summary>
/// SC7.1 — one agent tool call, captured as STRUCTURE rather than as a cut-off JSON fragment: the
/// tool's name as the wire gave it, plus the fields worth keeping (see
/// <see cref="Conductor.Core.Providers.ToolEventExtractor"/> for the vocabulary — <c>path</c>,
/// <c>command</c>, <c>taskId</c>, <c>purpose</c>, <c>bytes</c>/<c>lines</c>, …).
/// </summary>
/// <remarks>
/// Every value is truncated on its own, so the object as a whole is always complete, well-formed
/// JSON. That is the whole point of this type: the previous capture stored
/// <c>Trunc(input.GetRawText(), 150)</c>, one raw argument blob cut mid-string, and a
/// <c>file_path</c> that happened to sit past character 150 was simply gone — unrecoverable by any
/// reader, forever, because the loss happened at capture and not at display.
/// <para><see cref="Fields"/> preserves insertion order, and the extractor emits the canonical keys
/// first, so a renderer can walk it and get a stable line without re-sorting.</para>
/// </remarks>
public sealed record ToolCall(string Name, Dictionary<string, string> Fields)
{
    /// <summary>The value stored under <paramref name="key"/>, or null when this call carried none.</summary>
    public string? Field(string key) => Fields.TryGetValue(key, out var v) ? v : null;
}
