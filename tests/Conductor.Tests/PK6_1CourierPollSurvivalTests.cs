using Conductor.Core.Courier;

namespace Conductor.Tests;

/// <summary>PK6.1 / bug #100 - what the read-out of PK2.3's window found: the poll loop let
/// HttpClient's own timeout out, and the courier died of it twice in twenty-four hours
/// (2026-09-17 12:23:18Z, 2026-09-18 10:42:33Z; see .conductor/evidence/PK6/pk6.1.md).
///
/// <para>The first test is a property, not an example, because the vocabulary moves: the poll's
/// failure modes grow every time the source learns a new call, and the rule is the same for all of
/// them - nothing a poll throws may end the process. The negative control is the clause this
/// widening had to leave standing: a real cancellation still stops the loop, and stops it
/// silently.</para></summary>
public sealed class PK6_1CourierPollSurvivalTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pk61-" + Guid.NewGuid().ToString("N"));

    public PK6_1CourierPollSurvivalTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The exception the real courier died of, built the way HttpClient builds it: a
    /// TaskCanceledException wrapping a TimeoutException, thrown while nobody cancelled anything.</summary>
    private static Exception TheDeath() =>
        new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 65 seconds elapsing.",
            new TimeoutException("The operation was canceled."));

    public static TheoryData<string, Func<Exception>> WhatAPollCanThrow() => new()
    {
        { "HttpClient's own timeout (bug #100, the measured death)", TheDeath },
        { "a bare TaskCanceledException", () => new TaskCanceledException() },
        { "a bare OperationCanceledException on a token nobody holds", () => new OperationCanceledException() },
        { "a foreign token's cancellation", () => new OperationCanceledException(new CancellationToken(canceled: true)) },
        { "a transport failure", () => new HttpRequestException("connection reset") },
        { "a timeout as itself", () => new TimeoutException("too slow") },
        { "a socket giving up", () => new IOException("the I/O operation has been aborted") },
        { "malformed JSON from the API", () => new InvalidOperationException("unexpected token") },
    };

    [Theory]
    [MemberData(nameof(WhatAPollCanThrow))]
    public async Task NothingAPollThrowsEndsTheProcess(string what, Func<Exception> throws)
    {
        var source = new ThrowingSource(throws);
        var said = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var beats = 0;
        var daemon = new CourierDaemon(source, new CourierSettings { PollIntervalSeconds = 1 }, _home,
            log: said.Add, beat: _ => { if (++beats == 3) stop.Cancel(); });

        // No try/catch on purpose: before the fix this call THREW for the timeout case, which is
        // exactly how the process died - RunAsync unwound into Main and the runtime ended it.
        await daemon.RunAsync(stop.Token);

        Assert.True(source.Polls >= 3, what + ": the loop stopped coming round after " + source.Polls + " poll(s)");
        Assert.Equal(3, beats);
        Assert.Contains(said, l => l.StartsWith("courier poll error: ", StringComparison.Ordinal));
    }

    /// <summary>The negative control for the widened catch: our own cancellation must still break the
    /// loop, and must not be logged as a poll error - a courier asked to stop is not a courier
    /// failing. Without this, "catch everything" would quietly turn shutdown into an error streak.</summary>
    [Fact]
    public async Task OurOwnCancellationStillEndsTheLoopAndIsNotAPollError()
    {
        var said = new List<string>();
        using var stop = new CancellationTokenSource();
        var source = new ThrowingSource(() => throw new OperationCanceledException(stop.Token), onPoll: stop.Cancel);
        var daemon = new CourierDaemon(source, new CourierSettings { PollIntervalSeconds = 1 }, _home, log: said.Add);

        await daemon.RunAsync(stop.Token);

        Assert.Equal(1, source.Polls);
        Assert.DoesNotContain(said, l => l.StartsWith("courier poll error: ", StringComparison.Ordinal));
    }

    /// <summary>A source whose every poll throws what the test says, after doing what the test says.</summary>
    private sealed class ThrowingSource(Func<Exception> throws, Action? onPoll = null) : ICourierSource
    {
        public int Polls { get; private set; }

        public string Describe => "throwing";

        public Task<IReadOnlyList<CourierDelivery>> FetchAsync(long offset, CancellationToken ct)
        {
            Polls++;
            onPoll?.Invoke();
            throw throws();
        }

        public Task ReplyAsync(string chatId, string text, long? threadId, CancellationToken ct,
            IReadOnlyList<CourierButton>? buttons = null) => Task.CompletedTask;

        public Task<CourierAck> SendAsync(CourierPush push, CancellationToken ct) => Task.FromResult(new CourierAck(true));

        public Task<CourierAck> SendAsync(CourierSend send, string chatId, CancellationToken ct) =>
            Task.FromResult(new CourierAck(true));

        public Task<string?> ReactAsync(string chatId, long messageId, string emoji, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string?> DeleteAsync(string chatId, long messageId, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }
}
