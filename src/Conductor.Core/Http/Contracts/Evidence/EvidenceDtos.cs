namespace Conductor.Core.Http;

// K5.3: the evidence registry, surfaced to the Face (GET /evidence). The wire shape is the artifact
// model verbatim plus one derived flag — `visual` is the question every consumer actually asks (can
// this be shown inline, or does it have to be sent as a file), and it is answered here rather than
// re-derived from a kind string by each surface and by K5.4's sendPhoto/sendDocument choice.

public sealed record EvidenceArtifactDto(
    string Path, string Kind, string? CheckpointId, string? StageId, int? SessionNumber,
    string Sha256, long Bytes, string CreatedAt, string Source, bool Visual);

// Count is the WHOLE registry; Artifacts is what this response carries after ?limit / ?checkpoint,
// so a surface can say "12 of 40" instead of silently showing a truncated list as if it were all.
public sealed record EvidenceDto(IReadOnlyList<EvidenceArtifactDto> Artifacts, int Count);
