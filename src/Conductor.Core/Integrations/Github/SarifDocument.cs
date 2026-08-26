using System.Globalization;
using System.Text;
using System.Text.Json;
using Conductor.Core.Store;

namespace Conductor.Core.Integrations.Github;

/// <summary>A bug that named a place in the tree, and the places it named.</summary>
public sealed record SarifBugFinding(BugRow Bug, string PlanName, IReadOnlyList<SarifBugLocation> Locations);

/// <summary>
/// DV6.4 — the bug ledger as one SARIF 2.1.0 run, for GitHub code scanning.
///
/// <para>Three properties are load-bearing and none of them is decoration:</para>
/// <list type="bullet">
/// <item><b>The category.</b> <c>automationDetails.id</c> puts conductor's alerts in their own
/// analysis; without it an upload would be read as the repository's ONLY analysis and would close
/// every alert another tool had raised.</item>
/// <item><b>The fingerprint.</b> <c>partialFingerprints.conductorBugId</c> is the bug's row id, so
/// the same bug re-uploaded from a different commit UPDATES its alert instead of raising a second
/// one — the duplicate-on-second-pass failure bug #79 records for the issue mirror.</item>
/// <item><b>The absence of a clock.</b> Nothing here is stamped with "now", so two renders of an
/// unchanged ledger are byte-identical and a golden can pin the whole document.</item>
/// </list>
///
/// <para>Only OPEN bugs are rendered, and that is the closing mechanism: code scanning resolves an
/// alert whose result stops appearing in a later upload of the same category, so
/// <c>conductor bug fix</c> closes the alert at the next boundary with no second call.</para>
/// </summary>
public static class SarifDocument
{
    /// <summary>The analysis category. The trailing slash is GitHub's own convention for a category
    /// that carries no sub-analysis.</summary>
    public const string Category = "conductor-bugs/";

    public const string SchemaUri =
        "https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json";

    private const string ToolName = "Conductor";
    private const string InformationUri = "https://github.com/shaahink/conductor";

    /// <summary>The open bugs that cite a place in this tree, oldest id first so the document is
    /// stable under re-render. A bug with no citation is not a code-scanning alert and is not
    /// forced into being one — it stays an issue, which DV6.1 already gets out.</summary>
    public static List<SarifBugFinding> Findings(
        IEnumerable<CarriedBugRow> bugs, Func<string, string?> resolve)
    {
        var found = new List<SarifBugFinding>();
        foreach (var carried in bugs)
        {
            var bug = carried.Bug;
            if (!string.Equals(bug.Status, "open", StringComparison.OrdinalIgnoreCase)) continue;
            var locations = SarifBugLocations.Find(bug.Title, resolve);
            foreach (var extra in SarifBugLocations.Find(bug.Detail, resolve))
            {
                if (!locations.Any(l => string.Equals(l.Cite(), extra.Cite(), StringComparison.Ordinal)))
                    locations.Add(extra);
            }
            if (locations.Count == 0) continue;
            found.Add(new SarifBugFinding(bug, carried.PlanName, locations));
        }
        found.Sort((a, b) => a.Bug.Id.CompareTo(b.Bug.Id));
        return found;
    }

    /// <summary>The document plus the two counts a pass reports: what became an alert, and what
    /// could not because nobody wrote down where it lived.</summary>
    public static SarifPayload Payload(
        IEnumerable<CarriedBugRow> bugs, Func<string, string?> resolve, string engineVersion)
    {
        var all = bugs as IReadOnlyList<CarriedBugRow> ?? [.. bugs];
        var open = all.Count(b => string.Equals(b.Bug.Status, "open", StringComparison.OrdinalIgnoreCase));
        var findings = Findings(all, resolve);
        return new SarifPayload(Render(findings, engineVersion), findings, open - findings.Count);
    }

    public static string RuleId(long bugId) => "conductor/bug/" + bugId.ToString(CultureInfo.InvariantCulture);

    /// <summary>high → error, medium → warning, low → note. Anything unrecognised is a warning: a
    /// severity nobody typed correctly must not silently become the loudest thing on the tab.</summary>
    public static string Level(string? severity) => severity?.ToLowerInvariant() switch
    {
        "high" => "error",
        "low" => "note",
        _ => "warning",
    };

    public static string Render(IReadOnlyList<SarifBugFinding> findings, string engineVersion)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("$schema", SchemaUri);
            w.WriteString("version", "2.1.0");
            w.WriteStartArray("runs");
            WriteRun(w, findings, engineVersion);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\n");
    }

    private static void WriteRun(Utf8JsonWriter w, IReadOnlyList<SarifBugFinding> findings, string engineVersion)
    {
        w.WriteStartObject();

        w.WriteStartObject("tool");
        w.WriteStartObject("driver");
        w.WriteString("name", ToolName);
        w.WriteString("semanticVersion", engineVersion);
        w.WriteString("informationUri", InformationUri);
        w.WriteStartArray("rules");
        foreach (var f in findings) WriteRule(w, f);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();

        w.WriteStartObject("automationDetails");
        w.WriteString("id", Category);
        w.WriteEndObject();

        w.WriteString("columnKind", "utf16CodeUnits");

        w.WriteStartArray("results");
        for (var i = 0; i < findings.Count; i++) WriteResult(w, findings[i], i);
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static void WriteRule(Utf8JsonWriter w, SarifBugFinding f)
    {
        var bug = f.Bug;
        w.WriteStartObject();
        w.WriteString("id", RuleId(bug.Id));
        w.WriteString("name", "ConductorBug" + bug.Id.ToString(CultureInfo.InvariantCulture));
        w.WriteStartObject("shortDescription");
        w.WriteString("text", Headline(bug.Title));
        w.WriteEndObject();
        w.WriteStartObject("fullDescription");
        w.WriteString("text", Headline(bug.Title));
        w.WriteEndObject();
        w.WriteStartObject("help");
        w.WriteString("text", Help(f));
        w.WriteEndObject();
        w.WriteStartObject("defaultConfiguration");
        w.WriteString("level", Level(bug.Severity));
        w.WriteEndObject();
        w.WriteStartObject("properties");
        w.WriteStartArray("tags");
        w.WriteStringValue("conductor");
        w.WriteStringValue("bug");
        if (!string.IsNullOrWhiteSpace(bug.StageId)) w.WriteStringValue("stage:" + bug.StageId);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteResult(Utf8JsonWriter w, SarifBugFinding f, int index)
    {
        var bug = f.Bug;
        w.WriteStartObject();
        w.WriteString("ruleId", RuleId(bug.Id));
        w.WriteNumber("ruleIndex", index);
        w.WriteString("level", Level(bug.Severity));
        w.WriteStartObject("message");
        w.WriteString("text", $"conductor bug #{bug.Id.ToString(CultureInfo.InvariantCulture)}: {Headline(bug.Title)}");
        w.WriteEndObject();
        w.WriteStartObject("partialFingerprints");
        w.WriteString("conductorBugId", bug.Id.ToString(CultureInfo.InvariantCulture));
        w.WriteEndObject();
        w.WriteStartArray("locations");
        foreach (var location in f.Locations) WriteLocation(w, location);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteLocation(Utf8JsonWriter w, SarifBugLocation location)
    {
        w.WriteStartObject();
        w.WriteStartObject("physicalLocation");
        w.WriteStartObject("artifactLocation");
        w.WriteString("uri", location.Path);
        w.WriteEndObject();
        w.WriteStartObject("region");
        w.WriteNumber("startLine", location.StartLine);
        if (location.EndLine is { } end) w.WriteNumber("endLine", end);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>The alert's one line. Bug titles are written as prose and some run long; the tab
    /// shows a line, so the first line is what a title means here.</summary>
    private static string Headline(string title)
    {
        var line = title.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 160 ? line : line[..157].TrimEnd() + "...";
    }

    /// <summary>What the alert says when opened: where the bug came from and how to close it. The
    /// closing command is spelled out because an alert nobody knows how to clear becomes furniture.
    /// </summary>
    private static string Help(SarifBugFinding f)
    {
        var bug = f.Bug;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(bug.Detail)) sb.Append(bug.Detail!.Trim()).Append("\n\n");
        sb.Append("Filed by: ").Append(f.PlanName);
        if (!string.IsNullOrWhiteSpace(bug.StageId)) sb.Append(", stage ").Append(bug.StageId);
        if (bug.FoundSession is { } session)
            sb.Append(", session ").Append(session.ToString(CultureInfo.InvariantCulture));
        sb.Append(" on ").Append(bug.CreatedAt).Append(".\n");
        sb.Append("Severity: ").Append(bug.Severity).Append(".\n");
        sb.Append("Closes with: conductor bug fix ").Append(bug.Id.ToString(CultureInfo.InvariantCulture));
        sb.Append(" — the alert resolves itself at the next upload, because a fixed bug stops being ");
        sb.Append("rendered and code scanning closes what a later analysis no longer reports.");
        return sb.ToString();
    }
}
