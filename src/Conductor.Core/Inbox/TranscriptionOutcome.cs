using Conductor.Models;

namespace Conductor.Core.Inbox;

// DV3.3 — the shape of a transcription attempt, kept apart from the thing that makes one.
// The status and the outcome are what a CALLER handles; ITranscriber and the local-command
// implementation live in Transcriber.cs. One file, one job (ArchitectureTests).

/// <summary>What happened when we tried to turn audio into words.</summary>
public enum TranscriptionStatus
{
    /// <summary>Words came back.</summary>
    Ok,

    /// <summary>No command is configured. Not an error — see <see cref="TranscribeConfig"/>.</summary>
    NotConfigured,

    /// <summary>A command ran and produced nothing usable: non-zero exit, a timeout, empty output,
    /// or an executable that is not on this machine.</summary>
    Failed,

    /// <summary>The command ran clean and heard nothing. A silent recording is a real thing to send
    /// by accident, and it is not the same as a broken transcriber.</summary>
    NoSpeech,
}

/// <summary>DV3.3 — the answer, with the sentence that can be said to whoever sent the audio. The
/// failure sentence is not optional decoration: every path out of here except <see
/// cref="TranscriptionStatus.Ok"/> ends with a human being told what happened to their voice note.</summary>
/// <param name="Status">Which of the four outcomes.</param>
/// <param name="Transcript">The words, when there are any.</param>
/// <param name="Detail">Why not, in words that can be shown to the sender. Null when Ok.</param>
public sealed record TranscriptionOutcome(
    TranscriptionStatus Status,
    Transcript? Transcript,
    string? Detail)
{
    /// <summary>Whether there is a transcript worth storing.</summary>
    public bool HasWords => Status == TranscriptionStatus.Ok && Transcript is { Text.Length: > 0 };
}

