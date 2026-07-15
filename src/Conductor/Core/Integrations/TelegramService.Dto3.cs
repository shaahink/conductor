namespace Conductor.Core.Integrations;

/// <summary>M8.2: response shape for the getMe call TestConnectionAsync uses to validate a token.
/// Split into its own file (architecture ratchet: 3 types max per file).</summary>
public sealed class TgGetMeResponse
{
    public bool Ok { get; set; }
    public TgUser? Result { get; set; }
}
