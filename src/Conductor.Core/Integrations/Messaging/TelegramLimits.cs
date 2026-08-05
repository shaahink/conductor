namespace Conductor.Core.Integrations.Messaging;

/// <summary>K5.4 — the Bot API's own ceilings, in one place, because every one of them is a silent
/// failure when crossed: Telegram answers HTTP 400 and the message is simply never delivered. The
/// engine used to know none of them.</summary>
public static class TelegramLimits
{
    /// <summary>Above this, <c>sendPhoto</c> is refused outright. An artifact over the limit is sent
    /// as a document instead of being dropped — a 12 MB screenshot is still the evidence.</summary>
    public const long MaxPhotoBytes = 10L * 1024 * 1024;

    /// <summary>Above this, <c>sendDocument</c> is refused too. There is no larger call, so the
    /// artifact is announced as a path and the message says why it was not attached.</summary>
    public const long MaxDocumentBytes = 50L * 1024 * 1024;

    /// <summary>Which Bot API method carries this artifact — or null when it is too large for any of
    /// them. Kind comes from K5.3's <c>EvidenceKinds</c>; the size check is what stops a legitimately
    /// large capture turning into a 400 nobody sees.</summary>
    public static string? MethodFor(bool visual, long bytes) => bytes switch
    {
        > MaxDocumentBytes => null,
        _ when visual && bytes <= MaxPhotoBytes => "sendPhoto",
        _ => "sendDocument",
    };
}
