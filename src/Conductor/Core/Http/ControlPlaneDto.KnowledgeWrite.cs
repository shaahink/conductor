namespace Conductor.Core.Http;

// Write-side knowledge DTOs: file a note, file a bug, and resolve a bug straight from the Face
// (POST /note, POST /bug, POST /bug/resolve) so the Knowledge tab isn't read-only. These write the
// same run.db rows the CLI `conductor note` / `bug new` / `bug fix` verbs do, which the prompt
// batteries and the audit phase then consume — knowledge captured while watching a run compounds
// into the next session's prompt without dropping to a second terminal.

public sealed record NoteRequestDto(string? Content, string? StageId, string? Kind);

public sealed record BugNewRequestDto(string? Title, string? Detail, string? Severity, string? StageId);

public sealed record BugResolveRequestDto(long Id, string? Status);

// KnowledgeWriteResultDto (the shared {ok,id?,error?} reply for all three writes) lives in
// ControlPlaneDto.Bugs.cs — one file, ≤3 types, per the architecture ratchet.
