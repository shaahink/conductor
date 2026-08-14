using System.Text.Json;

using Conductor.Core.Watch;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF5.2 — the supervisor plan block. Three things are worth being sure of, and none of them is
/// "the command runs" (SF5.1 already proved a hook runs with the brief on stdin):
/// <list type="number">
/// <item>PRECEDENCE — which of <c>--hook</c>, the block, and nothing wins, in every combination.</item>
/// <item>THE FUSE — the hourly cap survives process death, because every wake is a fresh process.</item>
/// <item>THE ORDERS TRAVEL — standing orders in the plan reach the agent's stdin, not just a doc.</item>
/// </list>
/// </summary>
public sealed class SF5_2SupervisorTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "sf52-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _dir;   // the run's .conductor, where the fire ledger lives
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    public SF5_2SupervisorTests()
    {
        _dir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (IOException) { }
    }

    private PlanConfig Plan(SupervisorConfig? sup) => new()
    {
        Name = "sf52",
        Repo = _repo,
        Supervisor = sup,
    };

    // ── 1. Precedence ──

    [Fact]
    public void No_supervisor_block_and_no_hook_runs_nothing_and_says_nothing()
    {
        var d = SupervisorPolicy.Decide(Plan(null), hookOverride: null, TimeSpan.FromMinutes(10), Now);

        Assert.False(d.ShouldRun);
        Assert.Null(d.Command);
        // Silence here is correct, not a skip: there was never a supervisor to skip. SF5.1's shape
        // (watch --json with no hook) must stay exactly as quiet as it was.
        Assert.Null(d.Skipped);
        Assert.Equal("none", d.Source);
    }

    [Fact]
    public void Plan_block_supplies_the_command_when_no_hook_is_given()
    {
        var d = SupervisorPolicy.Decide(
            Plan(new SupervisorConfig { Command = "claude -p night-watch", TimeoutMinutes = 7 }),
            hookOverride: null, TimeSpan.FromMinutes(10), Now);

        Assert.True(d.ShouldRun);
        Assert.Equal("claude -p night-watch", d.Command);
        Assert.Equal("plan.supervisor", d.Source);
        // Its own timeout, not --hook-timeout's default: the block is self-contained or it is not a
        // place you can actually keep the supervisor.
        Assert.Equal(TimeSpan.FromMinutes(7), d.Timeout);
    }

    [Fact]
    public void Hook_flag_beats_the_plan_block()
    {
        var d = SupervisorPolicy.Decide(
            Plan(new SupervisorConfig { Command = "plan-command" }),
            hookOverride: "typed-command", TimeSpan.FromMinutes(3), Now);

        Assert.Equal("typed-command", d.Command);
        Assert.Equal("--hook", d.Source);
        Assert.Equal(TimeSpan.FromMinutes(3), d.Timeout);
    }

    [Fact]
    public void Hook_flag_is_not_bound_by_the_plans_hourly_fuse()
    {
        // The operator is at the keyboard making a one-off decision; the fuse exists to bound an
        // unattended loop, and applying it here would refuse the human who came to look.
        for (var i = 0; i < 20; i++) SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-i));

        var d = SupervisorPolicy.Decide(
            Plan(new SupervisorConfig { Command = "plan-command", MaxPerHour = 2 }),
            hookOverride: "typed-command", TimeSpan.FromMinutes(3), Now);

        Assert.True(d.ShouldRun);
        Assert.Equal("typed-command", d.Command);
    }

    [Theory]
    [InlineData(false, "x", "supervisor disabled in the plan")]
    [InlineData(true, "", "supervisor has no command")]
    [InlineData(true, "   ", "supervisor has no command")]
    public void A_block_that_cannot_supervise_says_which_way_it_failed(bool enabled, string cmd, string expect)
    {
        var d = SupervisorPolicy.Decide(
            Plan(new SupervisorConfig { Enabled = enabled, Command = cmd }),
            hookOverride: null, TimeSpan.FromMinutes(10), Now);

        Assert.False(d.ShouldRun);
        Assert.Equal(expect, d.Skipped);
    }

    // ── 2. The fuse ──

    [Fact]
    public void The_hourly_cap_is_reached_by_fires_written_by_earlier_processes()
    {
        var plan = Plan(new SupervisorConfig { Command = "c", MaxPerHour = 3 });

        // Three fires from three previous `conductor watch` invocations, all inside the window.
        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-50));
        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-20));
        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-1));

        var d = SupervisorPolicy.Decide(plan, null, TimeSpan.FromMinutes(10), Now);

        Assert.False(d.ShouldRun);
        Assert.Contains("rate limited", d.Skipped, StringComparison.Ordinal);
        Assert.Contains("cap 3", d.Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public void Fires_older_than_the_window_do_not_count()
    {
        var plan = Plan(new SupervisorConfig { Command = "c", MaxPerHour = 2 });
        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-61));
        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-90));
        SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-5));

        Assert.Equal(1, SupervisorPolicy.CountRecentFires(_dir, TimeSpan.FromHours(1), Now));
        Assert.True(SupervisorPolicy.Decide(plan, null, TimeSpan.FromMinutes(10), Now).ShouldRun);
    }

    [Fact]
    public void MaxPerHour_zero_means_no_fuse()
    {
        var plan = Plan(new SupervisorConfig { Command = "c", MaxPerHour = 0 });
        for (var i = 0; i < 40; i++) SupervisorPolicy.RecordFire(_dir, Now.AddMinutes(-i / 2.0));

        Assert.True(SupervisorPolicy.Decide(plan, null, TimeSpan.FromMinutes(10), Now).ShouldRun);
    }

    [Fact]
    public void An_unreadable_ledger_leaves_the_run_supervised_rather_than_silent()
    {
        // Fail-open on purpose: a corrupt counter must never be the reason nobody is watching.
        File.WriteAllText(Path.Combine(_dir, SupervisorPolicy.FiresFile), "not-a-date\n\nalso not one\n");

        Assert.Equal(0, SupervisorPolicy.CountRecentFires(_dir, TimeSpan.FromHours(1), Now));
        Assert.True(SupervisorPolicy.Decide(
            Plan(new SupervisorConfig { Command = "c", MaxPerHour = 1 }), null, TimeSpan.FromMinutes(10), Now).ShouldRun);
    }

    [Fact]
    public void The_ledger_is_trimmed_so_a_week_long_run_does_not_read_a_weeks_file_every_wake()
    {
        for (var i = 0; i < 600; i++) SupervisorPolicy.RecordFire(_dir, Now.AddDays(-3));
        SupervisorPolicy.RecordFire(_dir, Now);

        var lines = File.ReadAllLines(Path.Combine(_dir, SupervisorPolicy.FiresFile));
        Assert.Single(lines);
    }

    // ── 3. The orders travel ──

    [Fact]
    public void Standing_orders_from_the_plan_are_in_the_brief_the_supervisor_reads()
    {
        const string orders = "You may approve an owner gate whose checkpoint has evidence. Escalate anything that merges.";
        var plan = Plan(new SupervisorConfig { Command = "c", StandingOrders = orders });
        var wake = new WatchWake(WatchReason.CircuitBreaker, "3 failures on SF5", "SF5", 12) { FiredFrom = "circuitBreakerTripped" };

        var brief = WatchBrief.Build(wake, plan, state: null, status: null, engineAlive: true, Now);
        var text = WatchBrief.Render(brief);

        Assert.Equal(orders, brief["standingOrders"]!.GetValue<string>());
        // Through the render, because the render is what actually reaches stdin.
        Assert.Equal(orders, JsonDocument.Parse(text).RootElement.GetProperty("standingOrders").GetString());
    }

    /// <summary>KS5.4 — the brief hands the night watch the ceiling IN FORCE and the billed spend the
    /// cap actually compares, through the same one function every other surface reads
    /// (<c>BudgetCeiling</c>). Round 2 caught it still quoting the plan's raw caps: after an approval
    /// the supervisor was told the run was governed by $3.00 while the ceiling in force was $6.00 —
    /// a reader whose whole job is money, briefed with the one number every other surface had stopped
    /// saying.</summary>
    [Fact]
    public void The_brief_quotes_the_ceiling_in_force_and_the_billed_spend_not_the_plans_figures()
    {
        var plan = Plan(new SupervisorConfig { Command = "c" });
        plan.Limits.MaxRunCostUsd = 3.00m;
        plan.Limits.MaxRunTokens = 100_000;
        var state = new RunState
        {
            RunId = "r1",
            Status = RunStatus.AwaitingOwner,
            AwaitingOwnerReason = AwaitingOwnerReason.Budget,
            PerRunCostUsd = 3.00m,
            PerRunSideCostUsd = 0.50m,     // KS5.2: lanes/advisor money is cap money — the brief agrees
            PerRunTokens = 120_000,
            BudgetGrantUsd = 3.00m,
            BudgetGrantTokens = 100_000,
        };
        var wake = new WatchWake(WatchReason.OwnerPark, "budget", "S1", 3) { FiredFrom = "ownerApprovalRequested" };

        var brief = WatchBrief.Build(wake, plan, state, status: null, engineAlive: true, Now);

        Assert.Equal("budget-park", brief["reason"]!.GetValue<string>());
        Assert.Equal(6.00m, brief["costCapUsd"]!.GetValue<decimal>());
        Assert.Equal(200_000L, brief["tokenCap"]!.GetValue<long>());
        Assert.Equal(3.50m, brief["spendUsd"]!.GetValue<decimal>());
        Assert.Equal(120_000L, brief["tokens"]!.GetValue<long>());
    }

    [Fact]
    public void No_orders_means_no_key_rather_than_an_empty_one()
    {
        var wake = new WatchWake(WatchReason.RunEnded, "done", null, 9) { FiredFrom = "runFinished" };

        foreach (var sup in new SupervisorConfig?[]
                 {
                     null,
                     new SupervisorConfig { Command = "c" },                                    // orders unset
                     new SupervisorConfig { Command = "c", StandingOrders = "   " },            // orders blank
                     new SupervisorConfig { Command = "c", StandingOrders = "x", Enabled = false }, // block off
                 })
        {
            var brief = WatchBrief.Build(wake, Plan(sup), null, null, true, Now);
            Assert.False(brief.ContainsKey("standingOrders"));
        }
    }

    [Fact]
    public void The_supervisor_block_round_trips_through_the_plan_file()
    {
        // The block is only real if a plan on disk carries it — camelCase in, typed object out, and
        // through Validate(), which is where an unknown block would have been rejected.
        var path = Path.Combine(_repo, "conductor.plan.json");
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "| id | title | status |\n");
        File.WriteAllText(path, $$"""
        {
          "version": "1.0",
          "name": "sf52",
          "repo": "{{_repo.Replace("\\", "/", StringComparison.Ordinal)}}",
          "tracker": "TRACKER.md",
          "agent": { "command": "echo", "args": ["{prompt}"] },
          "stages": [ { "id": "S1", "title": "one", "sessions": 1 } ],
          "supervisor": {
            "command": "claude -p \"night watch\"",
            "timeoutMinutes": 4,
            "maxPerHour": 2,
            "standingOrders": "approve gates with evidence; escalate merges"
          }
        }
        """);

        var plan = PlanConfig.Load(path);

        Assert.NotNull(plan.Supervisor);
        Assert.Equal("claude -p \"night watch\"", plan.Supervisor!.Command);
        Assert.Equal(4, plan.Supervisor.TimeoutMinutes);
        Assert.Equal(2, plan.Supervisor.MaxPerHour);
        Assert.True(plan.Supervisor.Enabled); // defaulted on, so a block you wrote is a block that runs
        Assert.Equal("approve gates with evidence; escalate merges", plan.Supervisor.StandingOrders);
    }

    /// <summary>The end-to-end shape, in-process: plan block → decision → the command actually running
    /// with the brief on its stdin. The live proof under <c>.conductor/evidence/SF5</c> drives the same
    /// path through a real run; this one keeps it from regressing without one.</summary>
    [Fact]
    public async Task A_plan_named_supervisor_receives_the_brief_on_stdin()
    {
        var sink = Path.Combine(_repo, "stdin-capture.json");
        var plan = Plan(new SupervisorConfig
        {
            // Reads stdin to end and writes it out — the smallest possible stand-in for a headless model.
            Command = OperatingSystem.IsWindows()
                ? $"$input | Out-File -FilePath '{sink}' -Encoding utf8"
                : $"cat > '{sink}'",
            StandingOrders = "escalate anything that merges",
        });

        var wake = new WatchWake(WatchReason.OwnerPark, "gate on SF5", "SF5", 21) { FiredFrom = "ownerApprovalRequested" };
        var brief = WatchBrief.Render(WatchBrief.Build(wake, plan, null, null, true, Now));

        var d = SupervisorPolicy.Decide(plan, null, TimeSpan.FromMinutes(10), Now);
        Assert.True(d.ShouldRun);
        var r = await WatchHook.RunAsync(d.Command!, _repo, brief, TimeSpan.FromMinutes(2));

        Assert.Equal(0, r.ExitCode);
        var captured = JsonDocument.Parse(await File.ReadAllTextAsync(sink)).RootElement;
        Assert.Equal("owner-park", captured.GetProperty("reason").GetString());
        Assert.Equal("escalate anything that merges", captured.GetProperty("standingOrders").GetString());
    }
}
