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
}
