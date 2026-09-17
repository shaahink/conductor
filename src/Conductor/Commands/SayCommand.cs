using System.ComponentModel;
using System.Globalization;
using System.Text;

using Conductor.Core.Courier;
using Conductor.Core.Integrations;

using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>PK3.2 / D5 - <c>conductor say</c>: the one CLI verb for everything anyone sends to a chat -
/// a text or a body file, photos or documents, a reply, a reaction, a delete.
///
/// <para>It goes through the courier when one takes it (protocol 3, which answers with the message ids
/// and keeps <c>messages.jsonl</c>) and DIRECTLY otherwise (D3), with the environment's token - saying
/// which path delivered. Sending is outside Telegram's one-consumer rule, which constrains polling only,
/// so this verb never polls and never needs to.</para>
///
/// <para>Ceilings are refused by name before anything is sent (<see cref="TelegramSender.Refusal"/>),
/// and <c>--dry-run</c> prints the exact bytes, the resolved chat and the path, and sends nothing.</para>
///
/// <para>Machine-level like <c>courier</c>: no plan is read. A profile resolves against this machine's
/// courier chats; anything else is named by id.</para></summary>
public sealed class SayCommand : AsyncCommand<SayCommand.Settings>
{
    /// <summary>What <c>--help</c> and Program.cs print.</summary>
    public const string VerbDescription =
        "PK3.2: send to a chat through the courier, or directly when it is down - `say --to admin|observer|<chat-id> --text \"...\"|--file body.md "
      + "[[--photo a.png,b.png]] [[--document a.md]] [[--reply-to ID]] [[--parse-mode HTML|MarkdownV2|none]]`, `say --react EMOJI --message ID`, "
      + "`say --delete ID`, and `--dry-run` to print the exact bytes and the resolved chat.";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--to <CHAT>")]
        [Description("A chat id, or a profile this machine's courier lists exactly one chat under (admin, observer). Default: admin.")]
        public string? To { get; init; }

        [CommandOption("--text <TEXT>")]
        [Description("The message, as written. With files it is their caption.")]
        public string? Text { get; init; }

        [CommandOption("--file <PATH>")]
        [Description("Read the message from this file, byte for byte. Not with --text.")]
        public string? File { get; init; }

        [CommandOption("--photo <PATHS>")]
        [Description("Comma-separated images to show inline; two or more go as one media group (at most ten).")]
        public string? Photo { get; init; }

        [CommandOption("--document <PATHS>")]
        [Description("Comma-separated files to attach as they are; two or more go as one media group. Not with --photo.")]
        public string? Document { get; init; }

        [CommandOption("--reply-to <ID>")]
        [Description("The message id this answers.")]
        public long? ReplyTo { get; init; }

        [CommandOption("--parse-mode <MODE>")]
        [Description("HTML (default), MarkdownV2, or none for plain text.")]
        public string? ParseMode { get; init; }

        [CommandOption("--react <EMOJI>")]
        [Description("Put this reaction on --message instead of sending anything.")]
        public string? React { get; init; }

        [CommandOption("--message <ID>")]
        [Description("The message id --react reacts to.")]
        public long? Message { get; init; }

        [CommandOption("--delete <ID>")]
        [Description("Delete this message id instead of sending anything.")]
        public long? Delete { get; init; }

        [CommandOption("--dry-run")]
        [Description("Print the exact bytes, the resolved chat, the method and the path; send nothing.")]
        public bool DryRun { get; init; }
    }

    /// <summary>The origin every message this verb sends carries, in the ledger and the courier's log.</summary>
    internal const string Origin = "conductor say";

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        return await RunAsync(settings, Console.Out, stateHomeRoot: null, http,
            TelegramCourierSource.TokenFromEnvironment(), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The verb, with its seams exposed for a test: where the machine's courier lives, the
    /// client to send on, and the token the direct path would use.</summary>
    /// <returns>0 delivered (or dry run printed), 1 not delivered, 2 refused before anything was tried.</returns>
    internal static async Task<int> RunAsync(Settings s, TextWriter output, string? stateHomeRoot, HttpClient http,
        string? token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(output);

        var chat = string.IsNullOrWhiteSpace(s.To) ? "admin" : s.To.Trim();
        if (Usage(s) is { } usage) return Refuse(output, usage);

        var courierSettings = CourierSettings.Load(stateHomeRoot);
        var resolved = courierSettings.ChatFor(chat, out var chatRefusal);
        using var client = CourierClient.TryOpen(stateHomeRoot, out var unreachable, http: http);

        if (s.React is { } emoji) return await ReactAsync(s, chat, emoji, resolved, chatRefusal, client, unreachable, output, stateHomeRoot, http, token, ct).ConfigureAwait(false);
        if (s.Delete is { } doomed) return await DeleteAsync(s, chat, doomed, resolved, chatRefusal, client, unreachable, output, stateHomeRoot, http, token, ct).ConfigureAwait(false);

        string? text;
        try
        {
            text = s.File is { Length: > 0 } body ? await System.IO.File.ReadAllTextAsync(body, Encoding.UTF8, ct).ConfigureAwait(false) : s.Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(output, $"the body file {s.File} could not be read ({ex.Message}).");
        }

        var send = new CourierSend(chat, text, Paths(s.Photo), Paths(s.Document), s.ReplyTo, s.ParseMode, Origin: Origin);
        if (TelegramSender.Refusal(send) is { } ceiling) return Refuse(output, ceiling);

        if (s.DryRun)
        {
            await output.WriteAsync(DryRun(send, chat, resolved, chatRefusal, client, unreachable)).ConfigureAwait(false);
            return resolved is null && client is null ? 2 : 0;
        }

        if (client is not null)
        {
            var ack = await client.SendAsync(send, ct).ConfigureAwait(false);
            if (ack.Accepted)
            {
                await output.WriteLineAsync($"sent through the courier: chat {ack.ChatId}, message id(s) {Ids(ack.MessageIds)}").ConfigureAwait(false);
                return 0;
            }
            if (!ack.Unanswered) return Fail(output, ack.Detail);
            unreachable = ack.Detail;
        }

        if (Direct(output, token, resolved, chatRefusal, unreachable) is not { } chatId) return 1;
        var direct = await Sender(courierSettings, http, token!).SendAsync(send, chatId, ct).ConfigureAwait(false);
        if (!direct.Accepted) return Fail(output, $"courier unreachable ({unreachable}) and the direct send was refused: {direct.Detail}");

        Ledger(direct.MessageIds, chatId, CourierDesk.SendVerb, stateHomeRoot, output);
        await output.WriteLineAsync($"courier unreachable - sent directly: chat {chatId}, message id(s) {Ids(direct.MessageIds)} ({unreachable})").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> ReactAsync(Settings s, string chat, string emoji, string? resolved, string? chatRefusal,
        CourierClient? client, string? unreachable, TextWriter output, string? stateHomeRoot, HttpClient http, string? token, CancellationToken ct)
    {
        var message = s.Message!.Value;
        if (s.DryRun)
        {
            await output.WriteLineAsync($"say --dry-run: nothing is sent\n  verb:       react {emoji} on message {message.ToString(CultureInfo.InvariantCulture)}\n"
                + $"  chat:       {ChatLine(chat, resolved, chatRefusal)}\n  path:       {PathLine(client, unreachable)}").ConfigureAwait(false);
            return 0;
        }

        if (client is not null)
        {
            var ack = await client.ReactAsync(new CourierReact(chat, message, emoji, Origin), ct).ConfigureAwait(false);
            if (ack.Accepted) { await output.WriteLineAsync($"reacted through the courier: chat {ack.ChatId}, message id {message.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false); return 0; }
            if (!ack.Unanswered) return Fail(output, ack.Detail);
            unreachable = ack.Detail;
        }

        if (Direct(output, token, resolved, chatRefusal, unreachable) is not { } chatId) return 1;
        var why = await Sender(CourierSettings.Load(stateHomeRoot), http, token!).ReactAsync(chatId, message, emoji, ct).ConfigureAwait(false);
        if (why is not null) return Fail(output, $"courier unreachable ({unreachable}) and the direct reaction was refused: {why}");
        await output.WriteLineAsync($"courier unreachable - reacted directly: chat {chatId}, message id {message.ToString(CultureInfo.InvariantCulture)} ({unreachable})").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> DeleteAsync(Settings s, string chat, long message, string? resolved, string? chatRefusal,
        CourierClient? client, string? unreachable, TextWriter output, string? stateHomeRoot, HttpClient http, string? token, CancellationToken ct)
    {
        if (s.DryRun)
        {
            await output.WriteLineAsync($"say --dry-run: nothing is sent\n  verb:       delete message {message.ToString(CultureInfo.InvariantCulture)}\n"
                + $"  chat:       {ChatLine(chat, resolved, chatRefusal)}\n  path:       {PathLine(client, unreachable)}").ConfigureAwait(false);
            return 0;
        }

        if (client is not null)
        {
            var ack = await client.DeleteAsync(new CourierDelete(chat, message, Origin), ct).ConfigureAwait(false);
            if (ack.Accepted) { await output.WriteLineAsync($"deleted through the courier: chat {ack.ChatId}, message id {message.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false); return 0; }
            if (!ack.Unanswered) return Fail(output, ack.Detail);
            unreachable = ack.Detail;
        }

        if (Direct(output, token, resolved, chatRefusal, unreachable) is not { } chatId) return 1;
        var why = await Sender(CourierSettings.Load(stateHomeRoot), http, token!).DeleteAsync(chatId, message, ct).ConfigureAwait(false);
        if (why is not null) return Fail(output, $"courier unreachable ({unreachable}) and the direct delete was refused: {why}");
        Ledger([message], chatId, CourierDesk.DeleteVerb, stateHomeRoot, output);
        await output.WriteLineAsync($"courier unreachable - deleted directly: chat {chatId}, message id {message.ToString(CultureInfo.InvariantCulture)} ({unreachable})").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Why the switches do not make one request, or null.</summary>
    internal static string? Usage(Settings s)
    {
        var modes = (s.React is not null ? 1 : 0) + (s.Delete is not null ? 1 : 0);
        var sends = s.Text is not null || s.File is not null || s.Photo is not null || s.Document is not null;
        if (modes > 1 || (modes == 1 && sends))
            return "--react, --delete and a send (--text, --file, --photo, --document) are three different requests; name one.";
        if (s.React is not null && s.Message is null) return "--react needs --message <ID>: the message to react to.";
        if (s.Message is not null && s.React is null) return "--message names the message --react reacts to; add --react <EMOJI>.";
        if (s.Text is not null && s.File is not null) return "--text and --file are both the message; give one.";
        if (modes == 0 && !sends) return "nothing to say: give --text, --file, --photo or --document (or --react / --delete).";
        return null;
    }

    private static IReadOnlyList<string>? Paths(string? list) =>
        string.IsNullOrWhiteSpace(list)
            ? null
            // Full paths, because the courier opens them from ITS working directory, not this one.
            : [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath)];

    private static string DryRun(CourierSend send, string chat, string? resolved, string? chatRefusal,
        CourierClient? client, string? unreachable)
    {
        var files = send.Files;
        var kind = send.Photos is { Count: > 0 } ? "photo" : "document";
        var method = files.Count switch
        {
            0 => "sendMessage",
            1 => kind == "photo" ? "sendPhoto" : "sendDocument",
            _ => $"sendMediaGroup ({files.Count.ToString(CultureInfo.InvariantCulture)} {kind}s)",
        };
        var mode = TelegramSender.ParseModeFor(send.ParseMode, out _) ?? "none";
        var text = send.Text ?? "";

        var output = new StringBuilder();
        output.AppendLine("say --dry-run: nothing is sent");
        output.AppendLine("  verb:       send");
        output.AppendLine("  chat:       " + ChatLine(chat, resolved, chatRefusal));
        output.AppendLine("  path:       " + PathLine(client, unreachable));
        output.AppendLine("  method:     " + method);
        output.AppendLine("  parse mode: " + mode);
        if (send.ReplyTo is { } replyTo) output.AppendLine("  reply to:   " + replyTo.ToString(CultureInfo.InvariantCulture));
        foreach (var f in files)
            output.AppendLine($"  file:       {f} ({TelegramService.FileSize(f).ToString(CultureInfo.InvariantCulture)} B)");
        output.AppendLine($"  {(files.Count > 0 ? "caption" : "text")}:    {text.Length.ToString(CultureInfo.InvariantCulture)} characters, {Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture)} UTF-8 bytes");
        output.AppendLine("----- exact bytes -----");
        output.Append(text);
        if (!text.EndsWith('\n')) output.AppendLine();
        output.AppendLine("----- end -----");
        return output.ToString();
    }

    private static string ChatLine(string chat, string? resolved, string? chatRefusal) =>
        resolved is null
            ? $"{chat} -> not resolvable on this machine ({chatRefusal}); a courier that lists it may still resolve it"
            : string.Equals(chat, resolved, StringComparison.Ordinal) ? resolved : $"{chat} -> {resolved}";

    private static string PathLine(CourierClient? client, string? unreachable) =>
        client is not null
            ? $"through the courier on port {client.Port.ToString(CultureInfo.InvariantCulture)}"
            : $"directly, with this environment's token - courier unreachable: {unreachable}";

    /// <summary>The chat id for a direct send, or null with the reason already printed.</summary>
    private static string? Direct(TextWriter output, string? token, string? resolved, string? chatRefusal, string? unreachable)
    {
        if (token is not { Length: > 0 })
        {
            output.WriteLine($"say failed: courier unreachable ({unreachable}), and {TelegramCourierSource.TokenEnvVar} is not set, so nothing can send directly.");
            return null;
        }
        if (resolved is null) output.WriteLine($"say failed: courier unreachable ({unreachable}), and the chat does not resolve here: {chatRefusal}");
        return resolved;
    }

    private static TelegramSender Sender(CourierSettings settings, HttpClient http, string token)
    {
        // The machine's courier.json names the Bot API base a rig points everything at; a direct send
        // honours it so a rig's stub or relay sees the direct path too.
        var root = string.IsNullOrWhiteSpace(settings.ApiBaseUrl) ? TelegramService.DefaultApiRoot : settings.ApiBaseUrl.Trim();
        return new TelegramSender(http, root.TrimEnd('/') + "/bot", token);
    }

    /// <summary>A direct send is still ledgered: the ids are the point of D5, and the courier being down
    /// is exactly when somebody will need one to take a message back.</summary>
    private static void Ledger(IReadOnlyList<long>? ids, string chatId, string verb, string? stateHomeRoot, TextWriter output)
    {
        if (ids is not { Count: > 0 }) return;
        var why = CourierMessageLedger.Append(
            ids.Select(id => new CourierMessage(id, chatId, Origin + " (sent directly)", null, DateTimeOffset.UtcNow, verb)), stateHomeRoot);
        if (why is not null) output.WriteLine("warning: " + why);
    }

    private static string Ids(IReadOnlyList<long>? ids) =>
        ids is { Count: > 0 } ? string.Join(",", ids.Select(i => i.ToString(CultureInfo.InvariantCulture))) : "none";

    private static int Refuse(TextWriter output, string why)
    {
        output.WriteLine("say refused: " + why);
        return 2;
    }

    private static int Fail(TextWriter output, string why)
    {
        output.WriteLine("say failed: " + why);
        return 1;
    }
}
