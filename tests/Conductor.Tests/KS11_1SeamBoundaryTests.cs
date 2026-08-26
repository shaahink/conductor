using System.Reflection;
using System.Text.RegularExpressions;

using Conductor.Core.Integrations.Messaging;

namespace Conductor.Tests;

/// <summary>
/// KS11.1's third exit — the architecture rule that stops the seam becoming Telegram-shaped again.
///
/// <para>CH-1 puts composition, chat profiles and evidence browsing on the channel-agnostic side of
/// a seam and leaves <c>TelegramService</c> as the transport adapter. That arrangement is one
/// convenient edit away from collapsing: the next feature that needs a chat id, a caption limit or
/// an <c>inline_keyboard</c> will reach for the nearest one, and by the third such edit the seam is
/// a folder rather than a boundary. This is what makes that edit fail.</para>
///
/// <para>Two rules, and the first has NO allowlist — not one entry, not one exception. Comments are
/// excluded exactly as <see cref="ArchitectureBoundaryTests"/> excludes them: a doc comment that
/// explains why the seam does NOT do something Telegram-shaped is the most valuable line on the
/// page, and a rule that cannot tell prose from code teaches the next session to delete it.</para>
/// </summary>
public sealed class KS11_1SeamBoundaryTests
{
    /// <summary>The seam. Everything in this folder is defined without knowing what will carry it.</summary>
    private static readonly string[] Seam = ["src", "Conductor.Core", "Integrations", "Messaging"];

    /// <summary>The adapter, and the only place in the engine that may name a Telegram type. Anything
    /// added to this list is a claim that the file IS part of the Telegram transport.</summary>
    private static readonly string[] AdapterFiles =
    [
        "TelegramService.cs",
        "TelegramService.Channel.cs",
        "TelegramService.Lifecycle.cs",
        "TelegramService.Polling.cs",   // DV2.3: the inbound long-poll and its 409 handling (#38)
        "TelegramService.Inbound.cs",    // DV3.1: which kind arrived, and the note the seam is handed
        "TelegramMediaFetcher.cs",       // DV4.1: getFile, the download and the 20 MB cap, shared with the courier
        "TelegramService.Transport.cs",
        "TelegramService.TestConnection.cs",   // DV3 fix: the Test button's leg, split off the 500-line ceiling
        "TelegramReadiness.cs",
        "TelegramLimits.cs",
        "NoOpRunNotifier.cs",
        "SecretsStore.cs",       // reads the bot token out of the state dir; named for what it stores
        "ParkNotifier.cs",       // wraps IRunNotifier to mute a channel; never touches the wire
        "WatchRemote.cs",        // SF5.3's own Telegram push, outside the run loop — see below
        // DV1.1 — the per-channel health probe. It names telegram and github BY DESIGN: its whole
        // job is to answer "which configured channel is dead", which cannot be asked without naming
        // the channels. It touches no transport — it reads TelegramReadiness' verdict, the same one
        // doctor and GET /telegram/status read — so the seam below it is intact.
        "ChannelHealth.cs",
    ];

    /// <summary>A Telegram identifier: the word itself, or one of the Bot API DTO prefixes this repo
    /// uses (<c>TgUpdate</c>, <c>TgMessage</c>, <c>TgCallbackQuery</c>…).</summary>
    private static readonly Regex TelegramIdentifier = new(
        @"\bTelegram|\bTg[A-Z]\w*", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    private static readonly Regex Comments = new(
        @"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    // ── rule one: the seam names no messenger, with no exceptions ──

    /// <summary>The whole point of KS11.1. If this fails, read what it names: a Telegram fact has
    /// been written into channel-agnostic code, and it belongs in the adapter instead. Do NOT add an
    /// allowlist to this test — there is deliberately no mechanism for one.</summary>
    [Fact]
    public void The_seam_contains_no_telegram_identifier_anywhere()
    {
        var offenders = new List<string>();
        foreach (var file in SourcesUnder(Seam))
        {
            var code = Comments.Replace(File.ReadAllText(file.FullName), " ");
            foreach (Match m in TelegramIdentifier.Matches(code))
                offenders.Add($"{file.Name}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "the messenger seam has grown a Telegram fact — it belongs in the adapter:\n  "
            + string.Join("\n  ", offenders.Distinct(StringComparer.Ordinal)));
    }

    /// <summary>The compiled truth, which a text scan cannot give: no type the seam is made of
    /// mentions a Telegram type in any member signature. A field typed <c>TelegramConfig</c> or a
    /// method returning a <c>TgUpdate</c> fails here even if the source spelled it with a
    /// <c>using</c> alias.</summary>
    [Fact]
    public void No_seam_type_names_a_telegram_type_in_any_signature()
    {
        var seamTypes = typeof(RemoteSurface).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(RemoteSurface).Namespace)
            .ToList();
        Assert.NotEmpty(seamTypes);

        var offenders = new List<string>();
        foreach (var t in seamTypes)
        {
            foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var used in SignatureTypes(m))
                {
                    if (used.FullName is { } n && TelegramIdentifier.IsMatch(n))
                        offenders.Add($"{t.Name}.{m.Name} -> {used.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "a seam type carries a Telegram type in its signature:\n  " + string.Join("\n  ", offenders));
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo m) => m switch
    {
        FieldInfo f => [f.FieldType],
        PropertyInfo p => [p.PropertyType],
        MethodInfo mi => [mi.ReturnType, .. mi.GetParameters().Select(x => x.ParameterType)],
        ConstructorInfo ci => ci.GetParameters().Select(x => x.ParameterType),
        _ => [],
    };

    // ── rule two: the adapter's file list is a ratchet, not a wishlist ──

    /// <summary>Every engine file that names a Telegram type, checked against the declared adapter.
    ///
    /// <para>This is a two-way ratchet on purpose. A NEW file naming a Telegram type fails, which is
    /// the regression this exists to catch. A file that has been CLEANED also fails, until it is
    /// struck off the list — so the list can only shrink, and cannot quietly keep granting an
    /// exception to code that no longer needs one.</para>
    ///
    /// <para>Three entries are the honest residue of KS11.1 rather than transport: <c>SecretsStore</c>
    /// reads the bot token, <c>ParkNotifier</c> wraps the notifier interface to mute it, and
    /// <c>WatchRemote</c> sends SF5.3's own push outside the run loop and has never been behind any
    /// seam. Folding that last one in is a later checkpoint's work, and this list is where it is
    /// recorded instead of forgotten.</para></summary>
    [Fact]
    public void Only_the_declared_adapter_files_name_a_telegram_type()
    {
        var naming = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourcesUnder(["src", "Conductor.Core"]))
        {
            if (file.FullName.Contains($"{Path.DirectorySeparatorChar}TelegramApi{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
                continue;   // the Bot API DTOs are the wire's own shape, by definition

            var code = Comments.Replace(File.ReadAllText(file.FullName), " ");
            if (TelegramIdentifier.IsMatch(code)) naming.Add(file.Name);
        }

        // Config and contract types are named for the messenger they CONFIGURE, which is what they
        // are; renaming TelegramConfig would rename a plan file key, and renaming the DTOs would
        // rename an HTTP contract. This list is exact - every entry is a file that really does name
        // one, so a stale entry fails the assertion below rather than sitting here unnoticed.
        var config = new SortedSet<string>(
        [
            "TelegramConfig.cs", "TelegramStatusDtos.cs", "TelegramTokenDtos.cs",
            "ControlPlaneJsonContext.cs", "PlanConfig.cs", "SupervisorConfig.cs", "WatchRoster.cs",
        ], StringComparer.Ordinal);
        Assert.True(config.IsSubsetOf(naming),
            "a config exception no longer names a Telegram type - strike it off: "
            + string.Join(", ", config.Except(naming)));
        naming.ExceptWith(config);

        Assert.Equal(new SortedSet<string>(AdapterFiles, StringComparer.Ordinal), naming);
    }

    private static List<FileInfo> SourcesUnder(string[] parts) =>
        new DirectoryInfo(Path.Combine([RepoRoot(), .. parts]))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
