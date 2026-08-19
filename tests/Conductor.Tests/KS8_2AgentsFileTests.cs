using Conductor.Commands;
using Conductor.Core.Interop;

using Spectre.Console.Cli;

namespace Conductor.Tests;

/// <summary>
/// KS8.2 — the AGENTS.md courtesy, and the import that makes Claude Code honour it.
///
/// <para>The whole feature has exactly one way to be harmful — clobbering the file that steers
/// somebody's agent — so most of what is pinned here is what it does NOT write.</para>
/// </summary>
public sealed class KS8_2AgentsFileTests : IDisposable
{
    private readonly string _tmp;

    public KS8_2AgentsFileTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ks82a-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void A_repo_with_no_CLAUDE_md_gets_one_that_imports()
    {
        var written = AgentsFile.ClaudeMdWithImport(null);
        Assert.NotNull(written);
        Assert.Contains("@AGENTS.md", written, StringComparison.Ordinal);
        Assert.True(AgentsFile.ImportsAgents(written));
    }

    [Fact]
    public void An_existing_CLAUDE_md_is_appended_to_never_rewritten()
    {
        const string mine = "# CLAUDE.md\n\nRun the tests with `just test`. Never touch generated/.\n";
        var written = AgentsFile.ClaudeMdWithImport(mine);

        Assert.NotNull(written);
        Assert.StartsWith(mine, written, StringComparison.Ordinal);
        Assert.Contains("@AGENTS.md", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_CLAUDE_md_that_already_imports_is_left_exactly_alone()
    {
        Assert.Null(AgentsFile.ClaudeMdWithImport("# mine\n\n@AGENTS.md\n"));
        // Even buried mid-file: the directive is all Claude Code needs to see.
        Assert.Null(AgentsFile.ClaudeMdWithImport("intro\n@AGENTS.md\nmore text\n"));
    }

    [Fact]
    public void The_generated_AGENTS_md_names_the_plan_the_tracker_and_the_claiming_verb()
    {
        var text = AgentsFile.Generate("Karvansara edge", "EDGE-TRACKER.md");

        Assert.Contains("Karvansara edge", text, StringComparison.Ordinal);
        Assert.Contains("EDGE-TRACKER.md", text, StringComparison.Ordinal);
        Assert.Contains("conductor task --done <id> --evidence <path>", text, StringComparison.Ordinal);
        Assert.Contains("conductor note", text, StringComparison.Ordinal);
        // A brace token here would be harmless (this is not a plan template) but a rendered-looking
        // placeholder that never resolves is the exact confusion trap 8 is about.
        Assert.DoesNotContain("{", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ through init

    [Fact]
    public void Init_scaffolds_both_files_and_a_second_init_clobbers_neither()
    {
        var repo = Path.Combine(_tmp, "fresh");
        Directory.CreateDirectory(repo);
        Assert.Equal(0, RunInit(repo));

        var agents = Path.Combine(repo, "AGENTS.md");
        var claude = Path.Combine(repo, "CLAUDE.md");
        Assert.True(File.Exists(agents));
        Assert.True(File.Exists(claude));
        Assert.True(AgentsFile.ImportsAgents(File.ReadAllText(claude)));

        // Somebody edits both, then init runs again in the same tree (a re-scaffold after deleting
        // the plan). Neither file may lose a byte.
        File.WriteAllText(agents, "MY OWN INSTRUCTIONS\n");
        var edited = File.ReadAllText(claude) + "\nand my own note\n";
        File.WriteAllText(claude, edited);
        File.Delete(Path.Combine(repo, "conductor.plan.json"));
        File.Delete(Path.Combine(repo, "TRACKER.md"));

        Assert.Equal(0, RunInit(repo));
        Assert.Equal("MY OWN INSTRUCTIONS\n", File.ReadAllText(agents));
        Assert.Equal(edited, File.ReadAllText(claude));
    }

    private static int RunInit(string repo)
    {
        var app = new CommandApp();
        app.Configure(c =>
        {
            c.PropagateExceptions();
            c.AddCommand<InitCommand>("init");
        });
        return app.Run(["init", "--output", repo, "--name", "probe"]);
    }
}
