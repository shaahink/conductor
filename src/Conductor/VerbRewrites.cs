namespace Conductor;

/// <summary>
/// KS8.2 — argv rewrites that spell a two-word verb as the hidden one-word command that implements
/// it. A separate type rather than another local function in <c>Program.cs</c>: top-level statements
/// compile into one <c>Program</c> class, and CA1505's maintainability index is measured on it — the
/// third rewrite is what pushed it under the bar, so this is where any further ones go.
/// </summary>
internal static class VerbRewrites
{
    /// <summary>
    /// <c>conductor history export &lt;run&gt; --atif</c> → the hidden <c>history-export</c> command.
    /// Spectre cannot have <c>history</c> be both a branch holding subcommands and the verb that
    /// lists the catalogue, and listing the catalogue is what <c>history</c> is for — the same bind
    /// <c>run close|adopt</c> hit, resolved the same way.
    /// <para>It fires only on the literal second word, so the only thing it takes from the listing is
    /// a repo or slug genuinely called <c>export</c>. Everything else about <c>history</c> — filters,
    /// a run selector, <c>--json</c> — parses byte-identically.</para>
    /// </summary>
    public static string[] HistoryExport(string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Length < 2 || !string.Equals(argv[0], "history", StringComparison.Ordinal)) return argv;
        if (!string.Equals(argv[1], "export", StringComparison.Ordinal)) return argv;
        return ["history-export", .. argv[2..]];
    }

    /// <summary>
    /// KS2.1: typing nothing is a question, and the answer used to be forty-one verbs — a table of
    /// contents handed to someone who asked to come in. An empty argv opens the hub instead: what is
    /// running on this machine, what it remembers, what plans are here.
    /// <para>A REWRITE, NOT <c>SetDefaultCommand</c>. Spectre's default command changes how an unknown
    /// first token parses: with one configured, <c>conductor nosuchverb</c> is no longer an unknown
    /// command, it is the default command with a stray argument — and <c>UseStrictParsing</c> cannot
    /// help, because a bare word is not an option. Rewriting only a genuinely EMPTY argv leaves the
    /// parser byte-identical for everything else.</para>
    /// <para>Moved here from a local function in <c>Program.cs</c> at DV4.1, for this file's own
    /// stated reason: adding the <c>courier</c> registration put <c>Program</c> back on CA1505's
    /// floor, and argv rewrites are exactly what this type exists to hold.</para>
    /// </summary>
    public static string[] HubWhenBare(string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        return argv.Length == 0 ? ["hub"] : argv;
    }

    /// <summary>
    /// KS0.2: <c>conductor run close &lt;id&gt;</c> and <c>conductor run adopt &lt;id&gt;</c> read as
    /// two words and are one command. Spectre cannot have both — a branch named <c>run</c> could hold
    /// subcommands but could no longer BE the verb that starts a run, and <c>run</c> starting a run is
    /// the whole CLI's front door. So the two record verbs are a hidden top-level command, and the
    /// only thing that knows they are spelled with a space is this rewrite.
    /// <para>Nothing else is touched: <c>run</c>, <c>run --paused</c>, and a plan path that happens to
    /// be called <c>close</c> all reach RunCommand exactly as before, because the rewrite fires only
    /// on the literal second word.</para>
    /// </summary>
    public static string[] RunRecordVerbs(string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Length < 2 || !string.Equals(argv[0], "run", StringComparison.Ordinal)) return argv;
        if (argv[1] is not ("close" or "adopt")) return argv;
        return ["run-record", .. argv[1..]];
    }
}
