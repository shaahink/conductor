namespace Conductor.Models;

/// <summary>DV3.3 / findings §1.6 — how this machine turns speech into text.
///
/// <para>A COMMAND, not a service. This machine already runs faster-whisper on its GPU, offline,
/// with no API key, and the rest of this repo holds that posture on purpose (payesh anonymises
/// fails-closed; nothing about a run leaves the machine unless the owner publishes it). Shelling
/// out keeps the model choice, the language and the hardware in the owner's hands, and keeps the
/// engine from growing a dependency on anybody's cloud.</para>
///
/// <para>Unset is a supported state, not a broken one: with no command the note still files and the
/// audio is still kept — the reply says it was not transcribed. A bot that silently drops a voice
/// note is the failure mode findings §1.2 gap 2 describes, and "transcription is not configured"
/// dropped silently is the same failure wearing a different hat.</para></summary>
public sealed class TranscribeConfig
{
    /// <summary>The command line. <c>{audio}</c> is replaced with the path to the file; with no
    /// placeholder the path is appended as the last argument, which is what every ASR CLI expects
    /// anyway.
    ///
    /// <para>It prints the transcript on stdout. JSON in the documented contract
    /// (<c>{"text":…,"segments":[{"start","end","text","confidence"}]}</c>) carries per-segment
    /// confidence and gets the doubt marks; anything else is read as plain text, unmarked. See
    /// <c>tools/transcribe/whisper-json.py</c> for the faster-whisper wrapper this repo ships.</para></summary>
    public string? Command { get; set; }

    /// <summary>Kill the command after this long. Default 15 minutes: the bot API will not serve a
    /// file over 20 MB at all (<c>TelegramLimits</c>), which caps a voice note at roughly 20 minutes
    /// of Opus, and this machine transcribes at about 0.4x realtime on the GPU — so the default is
    /// about double the worst honest case, and a command that blows through it is stuck rather than
    /// slow.</summary>
    public int TimeoutSeconds { get; set; } = 900;

    /// <summary>Segments below this are marked in the stored note. See
    /// <c>Transcript.DefaultConfidenceFloor</c> for where the number comes from; a different command
    /// normalises its numbers differently, which is why it is a dial and not a constant.</summary>
    public double ConfidenceFloor { get; set; } = Core.Inbox.Transcript.DefaultConfidenceFloor;

    /// <summary>The env override, so a rig (and, at DV4, a machine-level courier that has no plan in
    /// front of it) can point at a command without editing a plan file. Env wins, the same
    /// precedence <c>CONDUCTOR_TELEGRAM_TOKEN</c> already has.</summary>
    public const string CommandEnvVar = "CONDUCTOR_TRANSCRIBE_COMMAND";

    /// <summary>The command actually in force: the env var if it is set, else the plan's. Null or
    /// blank means transcription is not configured, which is a state with a defined behaviour and
    /// not an error.</summary>
    public string? ResolvedCommand()
    {
        var fromEnv = Environment.GetEnvironmentVariable(CommandEnvVar)?.Trim();
        if (fromEnv is { Length: > 0 }) return fromEnv;
        var configured = Command?.Trim();
        return configured is { Length: > 0 } ? configured : null;
    }

    /// <summary>The plan-load refusal, in the shape <c>GithubConfig.BoardRefusal</c> established: a
    /// named complaint about the actual value, or null. A nonsense timeout is a run that either
    /// never transcribes or hangs the poll loop forever, and both are worth refusing at load.</summary>
    public string? Refusal()
    {
        if (TimeoutSeconds <= 0)
            return $"courier.transcribe.timeoutSeconds is {TimeoutSeconds}; it has to be a positive "
                 + "number of seconds (the default is 900).";
        if (ConfidenceFloor is < 0 or > 1)
            return $"courier.transcribe.confidenceFloor is {ConfidenceFloor}; it is a probability "
                 + "between 0 and 1 (the default is 0.45).";
        return null;
    }
}

/// <summary>DV3.3 — the courier block. Today it holds transcription; DV4 adds the daemon's own
/// settings beside it, which is why the key is <c>courier</c> and not <c>transcribe</c>: the thing
/// that will own polling, routing and transcription on this machine is one component with one block,
/// even while only part of it exists.</summary>
public sealed class CourierConfig
{
    /// <summary>Speech to text. Never null — an absent block behaves exactly like a configured one
    /// with no command, which is the untranscribed path.</summary>
    public TranscribeConfig Transcribe { get; set; } = new();

    /// <summary>Every refusal this block can raise, or null.</summary>
    public string? Refusal() => Transcribe.Refusal();
}
