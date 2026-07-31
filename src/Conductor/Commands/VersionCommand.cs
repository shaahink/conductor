using System.ComponentModel;
using System.Text.Json;
using Conductor.Core;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// SC8.1 — <c>conductor version</c>. Before this verb existed there was no way to ask an installed
/// binary what it was: the csproj said 2.0.0 forever, <c>install.ps1</c> published in silence, and
/// after every "rebuild before trusting it" the operator had to take the rebuild on faith (field
/// log, day one). "Is the run using stale engine code" is listed in GAP-ANALYSIS as a defect that
/// burned three sessions.
/// <para>Deliberately plan-free: it takes no <c>-p</c>, reads no state, and works in any directory,
/// because the moment you need it is the moment nothing else is working.</para>
/// </summary>
public sealed class VersionCommand : Command<VersionCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        [Description("Emit the build identity as a single JSON object.")]
        public bool Json { get; init; }

        [CommandOption("--short")]
        [Description("Emit only the version string (e.g. 2.0.0+abc123def456), for scripts.")]
        public bool Short { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var b = BuildInfo.Current;

        if (settings.Short)
        {
            // No markup: this one is parsed by tools/install.ps1 and by anything else that asks a
            // binary what it is. Console.WriteLine, not AnsiConsole, so nothing can colour it.
            Console.WriteLine(b.Full);
            return 0;
        }

        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                VersionReport.Current(), VersionJsonContext.Default.VersionReport));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold aqua]conductor[/] [bold]{Markup.Escape(b.Full)}[/]");
        Row("commit", b.CommitSha == BuildInfo.UnknownCommit
            ? "unknown (built without git — source archive or no git on PATH)"
            : b.Dirty ? $"{b.CommitSha} + uncommitted changes at build time" : b.CommitSha);
        Row("built", b.BuildDateIso ?? "unknown (this binary predates build stamping)");
        Row("runtime", BuildInfo.Runtime);
        Row("os", BuildInfo.Os);
        Row("binary", BuildInfo.BinaryPath);
        return 0;
    }

    private static void Row(string label, string value) =>
        AnsiConsole.MarkupLine($"  [grey]{label.PadRight(8)}[/] {Markup.Escape(value)}");
}
