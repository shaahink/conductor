using Conductor.Core.Providers;

namespace Conductor.Tests;

/// <summary>
/// DV2.4, bug #69 — a 429 storm burned a stage's whole attempt budget in minutes and ended
/// NEEDS HUMAN. Measured 2026-08-15, run <c>9647f1b8</c>, in this repo's own log
/// (<c>.conductor/logs/conductor-20260815.log:2154-2280</c>): <c>session #N exited (code 1, 0m,
/// $0.00)</c> every ~19 seconds, no "usage limit detected" line anywhere, attempts 2→8 spent in
/// three minutes, the circuit breaker firing on "identical failure pattern (AgentError ×2)" and the
/// advisor 429ing too — an account limit turned into a park needing a human.
///
/// <para>These pin the reset-time parser. The evidence-gate half — that a 429 reaching only the raw
/// stream is now seen at all — is driven end to end in <c>HarnessTests.Budget.cs</c>.</para>
/// </summary>
public sealed class DV2_4RateLimitBackoffTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The Claude CLI's own shape: the reset instant as a unix second after a pipe.</summary>
    [Fact]
    public void AUnixResetInTheClaudeCliShape_IsRead()
    {
        var at = new DateTimeOffset(Now.AddHours(3)).ToUnixTimeSeconds();

        var wait = ProviderText.ResetWait($"Claude AI usage limit reached|{at}", Now);

        Assert.NotNull(wait);
        Assert.Equal(180, Math.Round(wait!.Value.TotalMinutes));
    }

    [Theory]
    [InlineData("HTTP 429. Retry-After: 900", 15)]
    [InlineData("retry_after 90", 1.5)]
    [InlineData("rate limited — try again in 4h 32m", 272)]
    [InlineData("quota exhausted, resets in 45m", 45)]
    [InlineData("try again in 30s", 0.5)]
    public void AStatedResetIsReadFromEitherAHeaderOrEnglish(string evidence, double minutes)
        => Assert.Equal(minutes, ProviderText.ResetWait(evidence, Now)!.Value.TotalMinutes, 2);

    /// <summary>No reset named, so the caller keeps the plan's flat backoff. Answering "wait zero"
    /// here would be the storm this bug is about, one level down.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("429 Too Many Requests")]
    [InlineData("usage limit reached")]
    public void EvidenceWithNoResetTimeAnswersNothing(string evidence)
        => Assert.Null(ProviderText.ResetWait(evidence, Now));

    /// <summary>A reset already in the past is not "retry immediately" — a stale timestamp read that
    /// way is a retry storm with extra steps.</summary>
    [Fact]
    public void AResetAlreadyPastAnswersNothing()
    {
        var past = new DateTimeOffset(Now.AddHours(-2)).ToUnixTimeSeconds();
        Assert.Null(ProviderText.ResetWait($"Claude AI usage limit reached|{past}", Now));
    }

    /// <summary>And a backend claiming three days is a decision for the owner, not a sleep.</summary>
    [Fact]
    public void AnAbsurdResetIsClampedRatherThanObeyed()
    {
        var far = new DateTimeOffset(Now.AddDays(3)).ToUnixTimeSeconds();

        var wait = ProviderText.ResetWait($"Claude AI usage limit reached|{far}", Now);

        Assert.Equal(ProviderText.MaxResetWait, wait);
    }

    /// <summary>The classifier itself must still see every shape the field produced — this is the
    /// list the storm's evidence never reached, not a new one.</summary>
    [Theory]
    [InlineData("Claude AI usage limit reached|1755567600")]
    [InlineData("API Error: 429 Too Many Requests")]
    [InlineData("overloaded_error")]
    public void TheStormsOwnPhrasingStillClassifiesAsAUsageLimit(string evidence)
        => Assert.True(ProviderText.DetectsUsageLimit(evidence));
}
