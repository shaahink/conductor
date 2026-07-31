using System.ComponentModel;
using Conductor.Core;
using Conductor.Core.Update;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// SC8.3 — <c>conductor update</c>. Asks the release feed what the latest engine is, compares it to
/// the running one by semver precedence, and — unless a run is live — downloads the archive for this
/// platform, proves it is what it claims, and rename-dances it into place beside the running binary.
///
/// <para>Plan-free, like <c>version</c>: it takes an optional <c>-p</c> only to widen the live-run
/// check to a plan whose state directory is not <c>./.conductor</c>. The moment you need this verb is
/// the moment the installed engine is the thing in question, so it must not depend on a plan loading.</para>
///
/// <para><b>What "verifies" means here</b>, in order of strength: the archive's SHA-256 is checked
/// against the release's <c>SHA256SUMS.txt</c> when the release has one; then the extracted engine is
/// EXECUTED and must answer <c>version --short</c> with the release's own tag. The second check is
/// the one that cannot be fooled by a correct checksum over the wrong file.</para>
/// </summary>
public sealed class UpdateCommand : AsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-p|--plan <PLAN>")]
        [Description("Plan whose state directory is also checked for a live run (default: ./.conductor)")]
        public string? Plan { get; init; }

        [CommandOption("--check")]
        [Description("Report what is available and change nothing")]
        public bool CheckOnly { get; init; }

        [CommandOption("-y|--yes")]
        [Description("Do not ask before swapping the binary")]
        public bool Yes { get; init; }
    }

    /// <summary>Long enough for a self-contained single-file build to unpack its native libraries on
    /// first run, which is what the exec-verify pays for.</summary>
    private static readonly TimeSpan ExecVerifyTimeout = TimeSpan.FromSeconds(90);

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var current = BuildInfo.Current;
        AnsiConsole.MarkupLine($"[bold aqua]conductor update[/]");
        AnsiConsole.MarkupLine($"  [grey]running [/]{Markup.Escape(current.Full)}");
        AnsiConsole.MarkupLine($"  [grey]binary  [/]{Markup.Escape(BuildInfo.BinaryPath)}");
        if (ReleaseClient.FeedIsOverridden)
            AnsiConsole.MarkupLine($"  [yellow]feed    [/]{Markup.Escape(ReleaseClient.FeedUrl)} [yellow](overridden by {ReleaseClient.FeedEnvVar})[/]");
        AnsiConsole.WriteLine();

        if (!SemVer.TryParse(current.Version, out var running))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] this binary reports version '{Markup.Escape(current.Version)}', which is not a semantic version — nothing to compare against");
            return 1;
        }

        using var client = new ReleaseClient(TimeSpan.FromSeconds(30));
        var (release, error) = await client.LatestAsync().ConfigureAwait(false);
        var status = UpdateStatus.Decide(running, release, error);

        if (!status.Known)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] could not check for updates — {Markup.Escape(status.Detail)}");
            return 1;
        }
        UpdateCheckCache.Write(release!, DateTimeOffset.UtcNow);

        if (!status.Available)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(status.Detail)} (latest release {Markup.Escape(status.Tag ?? "?")})");
            return 0;
        }

        AnsiConsole.MarkupLine($"[aqua]→[/] {Markup.Escape(status.Detail)}");
        if (settings.CheckOnly)
        {
            AnsiConsole.MarkupLine("  [grey]run `conductor update` to install it[/]");
            return 0;
        }

        // The refusal comes BEFORE the download: refusing after pulling 40MB is technically correct
        // and reads as a bug, and the answer is the same either way.
        var blockers = UpdateSafety.Blockers(BuildInfo.BinaryPath, StateDirs(settings));
        if (blockers.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]✗[/] [bold]refusing to update while a run is live.[/]");
            foreach (var b in blockers) AnsiConsole.MarkupLine($"  [red]·[/] {Markup.Escape(b)}");
            AnsiConsole.MarkupLine("  [grey]swapping the engine mid-run means the rest of the session is driven by a different[/]");
            AnsiConsole.MarkupLine("  [grey]binary than the start of it — every task claim and bg start spawns the engine again.[/]");
            return 2;
        }

        return await InstallAsync(client, release!, status, settings).ConfigureAwait(false);
    }

    private static async Task<int> InstallAsync(
        ReleaseClient client, Core.Update.GithubRelease release, UpdateStatus status, Settings settings)
    {
        var target = UpdateTarget.ForThisMachine();
        if (target is null)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] no release is published for {Markup.Escape(UpdateTarget.DescribeThisMachine())} — build from source (tools/install.sh)");
            return 1;
        }

        var asset = release.Asset(target.AssetName);
        if (asset is null)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] release {Markup.Escape(release.TagName)} has no asset named {Markup.Escape(target.AssetName)} " +
                $"(it has: {Markup.Escape(string.Join(", ", release.Assets.Select(a => a.Name)))})");
            return 1;
        }

        var destination = BuildInfo.BinaryPath;
        var installDir = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(installDir))
        {
            AnsiConsole.MarkupLine("[red]✗[/] cannot work out which directory this binary lives in");
            return 1;
        }

        if (!settings.Yes && !ConfirmSwap(status, installDir)) return 0;

        var work = Directory.CreateTempSubdirectory("conductor-update-");
        try
        {
            var archive = Path.Combine(work.FullName, asset.Name);
            AnsiConsole.MarkupLine($"  [grey]download[/] {Markup.Escape(asset.Name)} ({ArchiveUnpacker.Size(asset.Size)})");
            await client.DownloadAsync(asset, archive).ConfigureAwait(false);

            var verified = await VerifyArchiveAsync(client, release, asset, archive).ConfigureAwait(false);
            if (verified is not null) { AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(verified)}"); return 1; }

            var unpacked = Path.Combine(work.FullName, "unpacked");
            ArchiveUnpacker.Extract(archive, unpacked);
            var engine = ArchiveUnpacker.Find(unpacked, target.EngineFileName);
            if (engine is null)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(asset.Name)} contains no {Markup.Escape(target.EngineFileName)} — refusing to install it");
                return 1;
            }
            ArchiveUnpacker.MakeExecutable(engine);

            // The verification that cannot be faked: run it and ask.
            var (ok, answer) = BinarySwap.AskVersion(engine, ExecVerifyTimeout);
            if (!ok)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] the downloaded engine did not answer `version --short` — {Markup.Escape(answer)}");
                return 1;
            }
            if (!SemVer.TryParse(answer, out var answered) || answered.CompareTo(status.Latest!.Value) != 0)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] the downloaded engine reports [bold]{Markup.Escape(answer)}[/] but release {Markup.Escape(release.TagName)} was expected — refusing to install it");
                return 1;
            }
            AnsiConsole.MarkupLine($"  [grey]verified[/] it runs and answers {Markup.Escape(answer)}");

            return Swap(installDir, destination, engine, unpacked, target, status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] update failed before anything was replaced — {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            try { work.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Checksum first, and a stated skip rather than a silent one when the release predates
    /// the manifest. Returns null when the archive is acceptable, else the reason it is not.</summary>
    private static async Task<string?> VerifyArchiveAsync(
        ReleaseClient client, Core.Update.GithubRelease release, GithubAsset asset, string archive)
    {
        var manifest = await client.TryReadTextAsync(release.Asset(ArchiveUnpacker.ChecksumAssetName)).ConfigureAwait(false);
        var expected = ArchiveUnpacker.ExpectedSha(manifest, asset.Name);
        if (expected is null)
        {
            AnsiConsole.MarkupLine($"  [yellow]checksum[/] release {release.TagName} publishes no {ArchiveUnpacker.ChecksumAssetName} entry for this asset — " +
                "relying on the exec-verify below");
            return null;
        }
        var actual = ArchiveUnpacker.Sha256(archive);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return $"checksum mismatch on {asset.Name}: expected {expected}, got {actual} — refusing to install it";
        AnsiConsole.MarkupLine($"  [grey]checksum[/] sha256 {Markup.Escape(actual[..12])}… matches {ArchiveUnpacker.ChecksumAssetName}");
        return null;
    }

    private static int Swap(
        string installDir, string destination, string engine, string unpacked,
        UpdateTarget target, UpdateStatus status)
    {
        BinarySwap.SweepRetired(installDir);

        var engineSwap = BinarySwap.Replace(destination, engine);
        AnsiConsole.MarkupLine(engineSwap.Ok
            ? $"  [green]swap    [/] {Markup.Escape(engineSwap.Detail)}"
            : $"[red]✗[/] {Markup.Escape(engineSwap.Detail)}");
        if (!engineSwap.Ok) return 1;

        // The face ships in the same archive and the engine finds it by looking beside itself, so a
        // half-swap would leave a new engine driving an old TUI. Not fatal on its own — the engine
        // runs headless — but it is said out loud.
        var face = ArchiveUnpacker.Find(unpacked, target.FaceFileName);
        if (face is not null)
        {
            var faceSwap = BinarySwap.Replace(Path.Combine(installDir, target.FaceFileName), face);
            AnsiConsole.MarkupLine(faceSwap.Ok
                ? $"  [green]swap    [/] {Markup.Escape(faceSwap.Detail)}"
                : $"  [yellow]⚠ face  [/] {Markup.Escape(faceSwap.Detail)} — the engine still runs, the TUI is the old one");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] updated [bold]{Markup.Escape(status.Current.ToString())}[/] → [bold]{Markup.Escape(status.Tag ?? "")}[/]");
        AnsiConsole.MarkupLine("  [grey]confirm it with `conductor version` in a new shell[/]");
        return 0;
    }

    private static bool ConfirmSwap(UpdateStatus status, string installDir)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            AnsiConsole.MarkupLine("[yellow]⚠[/] not an interactive console — re-run with [bold]--yes[/] to install without asking");
            return false;
        }
        return AnsiConsole.Confirm(
            $"Replace the conductor in {Markup.Escape(installDir)} ({Markup.Escape(status.Current.ToString())} → {Markup.Escape(status.Tag ?? "?")})?");
    }

    /// <summary>Where to look for a live engine lock. The cwd's state directory always, plus the one
    /// belonging to an explicitly named plan — never a discovered one, because a verb that must work
    /// when everything else is broken cannot start by prompting for a plan file.</summary>
    private static IEnumerable<string> StateDirs(Settings settings)
    {
        yield return Path.Combine(Directory.GetCurrentDirectory(), ".conductor");
        if (string.IsNullOrWhiteSpace(settings.Plan)) yield break;

        string? dir = null;
        try { dir = Models.PlanConfig.Load(settings.Plan).StateDir; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠[/] --plan {Markup.Escape(settings.Plan)} does not load ({Markup.Escape(ex.Message)}) — checking ./.conductor only");
        }
        if (dir is { Length: > 0 }) yield return dir;
    }
}
