using Conductor.Core.Integrations.Messaging;

namespace Conductor.Core.Integrations;

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

    /// <summary>KS11.1 — Telegram's own message and caption ceilings, moved out of
    /// <c>HtmlChunker</c>. The chunker splits HTML at a limit; WHICH limit is a fact about one
    /// messenger, and leaving it as a constant named <c>TelegramMaxChars</c> in the middle of the
    /// channel-agnostic seam is exactly the confusion CH-1 exists to remove.</summary>
    public const int MaxMessageChars = 4096;

    /// <summary>A caption is a quarter of a message, which is why an evidence caption is composed
    /// short rather than clipped from a body.</summary>
    public const int MaxCaptionChars = 1024;

    /// <summary>DV3.1 — the ceiling on what a bot may DOWNLOAD: 20 MB, roughly fifteen to twenty
    /// minutes of Opus. Not a conductor policy and not tunable: above it <c>getFile</c> refuses, and
    /// nothing but a self-hosted Bot API server changes that. Stated once so the pre-flight check,
    /// the post-getFile check, the streaming cap and the sentence the sender reads are all the same
    /// number.</summary>
    public const long MaxDownloadBytes = 20L * 1024 * 1024;

    /// <summary>The cap as a human says it, for the sentence the sender reads.</summary>
    public const string MaxDownloadLabel = "20 MB";

    /// <summary>Why an oversize file never arrived, in words that say what the sender can DO about
    /// it. The size and the cap are both named: "too big" without a number is a refusal nobody can
    /// act on.</summary>
    public static string TooBigReason(long sizeBytes) =>
        $"({InboundAck.Size(sizeBytes)}) is over Telegram's {MaxDownloadLabel} limit on what a bot may "
        + "download, so the file itself never reached this machine - the Bot API declines to serve it "
        + $"at all. Re-send it under {MaxDownloadLabel}, split it, or put it somewhere I can be pointed at.";

    /// <summary>Why a fetch failed for any other reason, carrying the API's own words — the same
    /// argument bug #38 settled: the one sentence that explains it is already on the wire, and
    /// throwing it away is what made a 409 unreadable for a week.</summary>
    public static string NotFetchedReason(string why) =>
        "could not be downloaded: " + MessageComposer.EscapeHtml(why) + ".";
}
