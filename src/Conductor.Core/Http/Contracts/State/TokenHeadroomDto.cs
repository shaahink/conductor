namespace Conductor.Core.Http;

/// <summary>K4.4 — one gauge's worth of live token headroom: everything a surface needs to render
/// "session at 6.2M of 12M, nudge in 2.2M, 310k/min" without doing arithmetic of its own.</summary>
/// <remarks>
/// <para><b>Why a block rather than nine more fields on <see cref="StateDto"/>.</b> This is the first
/// widget a lane-aware Face has to MULTIPLY. One self-contained value per gauge is the shape that
/// survives that; nine loose siblings on the state record is the shape that does not.</para>
/// <para><b>Why the engine computes <see cref="Tokens"/> instead of letting the Face add up what it
/// already has.</b> The rail counts cache-read (<c>SessionRunner.LiveTokens</c>); the wire's
/// <c>sessionTokensInput/Output/Reasoning</c> do not. On this project cache reads are 98% of every
/// token ever spent, so a surface that summed the three fields it could see would show a session
/// sitting at 2% of its ceiling right up to the moment the engine killed it for hitting 100%.</para>
/// <para><b>Honesty when there is no cap.</b> Every cap-dependent field is null when no ceiling
/// resolves — the same rule <c>CostRemaining</c> follows. "This plan sets no ceiling" and "there is
/// plenty left" are different facts and must never render the same.</para>
/// </remarks>
/// <param name="Tokens">Every token this session has been charged for, counted the way the rail
/// counts them: input + output + reasoning + cache-read.</param>
/// <param name="Cap">The resolved per-session ceiling (<see cref="SoftBreak.EffectiveCap"/>), not the
/// raw this-run override. null = no ceiling.</param>
/// <param name="NudgeAt">Where the cooperative wrap-up notice fires. null when there is no cap.</param>
/// <param name="ToNudge">Distance to the nudge; negative once the nudge has already been raised.</param>
/// <param name="ToCap">Distance to the hard ceiling; negative would mean the session is already
/// over and about to be ended.</param>
/// <param name="UsedRatio">Tokens over cap. null when there is no cap — NOT 0, which a bar would
/// render as a comfortable, entirely fictional 100% headroom.</param>
/// <param name="BurnPerMinute">The session's mean spend rate so far. null until the session has run
/// long enough for a rate to mean anything, and null once it has ended: a burn rate for a session
/// that is not burning is the same class of untruth as a cost that outlives its session.</param>
/// <param name="MinutesToNudge">Projection at the mean rate. Optimistic by construction — a
/// session's bill is roughly turns × context and context only grows, so the rate ahead is higher
/// than the rate behind. Treat it as a ceiling on the time left, not a promise.</param>
/// <param name="MinutesToCap">Same projection, to the hard ceiling.</param>
/// <param name="Live">Whether a session is actually in flight. false = these are the last session's
/// closing numbers, and the rate and projections are null.</param>
public sealed record TokenHeadroomDto(
    long Tokens,
    long? Cap = null,
    long? NudgeAt = null,
    long? ToNudge = null,
    long? ToCap = null,
    double? UsedRatio = null,
    double? BurnPerMinute = null,
    double? MinutesToNudge = null,
    double? MinutesToCap = null,
    bool Live = false);
