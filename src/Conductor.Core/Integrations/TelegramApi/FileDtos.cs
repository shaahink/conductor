using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

/// <summary>DV3.1 — one shape for every file-bearing thing a message can carry.
///
/// <para>The Bot API gives <c>voice</c>, <c>audio</c>, <c>document</c> and each entry of
/// <c>photo</c> their own object types, but the fields conductor needs are the same four in every
/// one of them — <c>file_id</c> to fetch it, <c>file_size</c> to refuse it, <c>mime_type</c> and
/// <c>file_name</c> to name it on disk — plus <c>duration</c>, which only the two audio kinds carry
/// and which reads as 0 for the rest. Unknown fields are ignored by the deserialiser, so one class
/// reads all four without inventing four that differ by nothing.</para>
///
/// <para>WHICH kind it was is not in here on purpose: it comes from the PROPERTY the object arrived
/// under (<see cref="TgMessage.Voice"/> and friends), which is the only place the wire states
/// it.</para></summary>
public sealed class TgFileRef
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_unique_id")] public string? FileUniqueId { get; set; }

    /// <summary>Optional on the wire — a message may announce a file without its size, which is why
    /// the cap is checked a second time against <see cref="TgFile.FileSize"/> after getFile.</summary>
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }

    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }

    /// <summary>The sender's own name for a document. NEVER used as a path — see
    /// <c>TelegramService.Inbound</c>, where it is reduced to a leaf and scrubbed.</summary>
    [JsonPropertyName("file_name")] public string? FileName { get; set; }

    public int Duration { get; set; }
}

/// <summary>The <c>result</c> of a <c>getFile</c> call: the temporary path under which the API will
/// serve the bytes for the next hour.</summary>
public sealed class TgFile
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = "";
    [JsonPropertyName("file_size")] public long? FileSize { get; set; }

    /// <summary>Relative, e.g. <c>voice/file_3.oga</c>. Absent when the API declines to serve the
    /// file at all, which is what it does above 20 MB.</summary>
    [JsonPropertyName("file_path")] public string? FilePath { get; set; }
}

/// <summary>A <c>getFile</c> envelope. <c>description</c> is deserialised for the same reason
/// <see cref="TgResponse.Description"/> is (DV2.3, bug #38): on a refusal it is the only sentence
/// that says WHY, and "file is too big" is exactly the case this checkpoint has to name back to the
/// sender.</summary>
public sealed class TgFileResponse
{
    public bool Ok { get; set; }
    public TgFile? Result { get; set; }
    public string? Description { get; set; }

    [JsonPropertyName("error_code")] public int? ErrorCode { get; set; }
}
