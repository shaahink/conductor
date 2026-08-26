using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Conductor.Tests;

/// <summary>
/// KS9.1 — a recording, STATEFUL fake of the slice of the GitHub issues API the mirror uses.
///
/// <para>Stateful on purpose. A handler that only records and answers <c>200 {}</c> can prove the
/// first pass's request bodies and nothing else; the claim that matters here is about the SECOND
/// pass — "re-running the backfill mints zero duplicates" — and that claim is only meaningful
/// against a server that serves back the issues it was asked to create, the way the real one does.
/// So this keeps issues, labels, milestones and comments, and answers <c>GET</c> from them.</para>
///
/// <para>It deliberately mimics two of GitHub's own behaviours that a naive fake would smooth over:
/// bodies come back with CRLF line endings, and an issue is always created <c>open</c> whatever the
/// request said. Both are exactly the kind of difference that makes a reconciler that passed its
/// tests re-PATCH every card forever against the real API.</para>
/// </summary>
internal sealed class FakeGithub : HttpMessageHandler
{
    internal sealed record Recorded(string Method, string Path, string Body, string UserAgent, string Accept, string? Authorization);

    public List<Recorded> Requests { get; } = [];

    private readonly Dictionary<int, Issue> _issues = [];
    private readonly Dictionary<int, List<string>> _comments = [];
    private readonly Dictionary<string, int> _milestones = new(StringComparer.Ordinal);
    private int _nextIssue = 100;
    private int _nextMilestone = 1;

    private sealed class Issue
    {
        public int Number { get; init; }
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string State { get; set; } = "open";
        public List<string> Labels { get; set; } = [];
        public int? Milestone { get; set; }
    }

    /// <summary>Every request body POSTed to one path, in order — what an acceptance clause about
    /// "what GitHub receives" is asserted against.</summary>
    public List<string> Posted(string path) =>
        [.. Requests.Where(r => r.Method == "POST" && r.Path == path).Select(r => r.Body)];

    public int NumberOfTask(string taskId) =>
        _issues.Values.First(i => i.Body.Contains($"<!-- conductor:task {taskId} -->", StringComparison.Ordinal)).Number;

    public int MilestoneNumberFor(string stage) => _milestones[stage];

    /// <summary>A label a human added, which conductor must carry through rather than strip.</summary>
    public void AddLabel(int issueNumber, string label) => _issues[issueNumber].Labels.Add(label);

    /// <summary>KS9.2 — the human's hand on the board: close a card, read its labels back, find the
    /// diary issue. The mirror must survive all three without any of them reaching the run.</summary>
    public void Close(int issueNumber) => _issues[issueNumber].State = "closed";

    public IReadOnlyList<string> LabelsOf(int issueNumber) => _issues[issueNumber].Labels;

    /// <summary>DV6.1 — the issue carrying an arbitrary marker. <see cref="NumberOfTask"/> knows the
    /// checkpoint marker's spelling; the ledger has two more of its own, and a fake that hard-coded
    /// each one would be a third place to keep the identity rule.</summary>
    public int NumberOfMarker(string marker) =>
        _issues.Values.Single(i => i.Body.Contains(marker, StringComparison.Ordinal)).Number;

    /// <summary>DV6.1 — is that issue still open? The whole ledger claim is a lifetime, so "was this
    /// one closed" has to be answerable about the SERVER's state, not about a request that was sent.</summary>
    public bool IsOpen(int issueNumber) => _issues[issueNumber].State == "open";

    /// <summary>DV6.1 — every comment body on an issue, in order.</summary>
    public IReadOnlyList<string> CommentsOn(int issueNumber) =>
        _comments.TryGetValue(issueNumber, out var list) ? list : [];

    /// <summary>The run diary's issue number, found the way the mirror finds it — by the marker in
    /// the body, never by assuming what the fake numbered it.</summary>
    public int RunIssueNumber =>
        _issues.Values.Single(i => i.Body.Contains("<!-- conductor:run ", StringComparison.Ordinal)).Number;

    /// <summary>KS9.2 — the outage switch. While this is set, every request fails the way a dead
    /// endpoint fails: an <see cref="HttpRequestException"/> out of the handler, which is what the
    /// client's transport catch turns into an error string. Requests are still RECORDED, because
    /// "how many requests did the mirror waste while GitHub was down" is a question the batching bar
    /// asks. Flip it back to null and the same fake serves the same issues — which is what makes
    /// "converges on reconnect" a real round trip rather than two unrelated fixtures.</summary>
    public string? Outage { get; set; }

    /// <summary>KS9.2 — per-request latency. A fake that answers instantly cannot tell a mirror that
    /// WAITS for its in-flight pass at shutdown from one that races it and usually wins.</summary>
    public TimeSpan Latency { get; set; }

    private TaskCompletionSource? _release;
    private TaskCompletionSource? _arrived;
    private int _holdArmed;

    /// <summary>KS9.2 — a HARD hold, and the reason latency alone is not one. A pass is fired onto the
    /// thread pool, and a pass that has not STARTED holds no gate: under the full suite's load the
    /// work item can still be queued when a test's own boundary reaches the gate, so the boundary wins
    /// it and RUNS where the claim says it must coalesce. Latency cannot close that window — it only
    /// slows a pass that already began. This parks the next request INSIDE the handler and hands back
    /// a task that completes once that request has genuinely arrived, so "a pass is in flight" is an
    /// observed fact rather than a bet on the scheduler. Call <see cref="Release"/> to let it go.</summary>
    public Task HoldNextRequest()
    {
        _arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _holdArmed, 1);
        return _arrived.Task;
    }

    /// <summary>Let the held request answer. Safe to call when nothing is held.</summary>
    public void Release() => _release?.TrySetResult();

    /// <summary>A failed assertion between the hold and the release would otherwise leave a request
    /// parked forever and turn a one-line failure into a hung test run.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing) _release?.TrySetResult();
        base.Dispose(disposing);
    }

    /// <summary>KS9.2 — GitHub's own eventual consistency, reproduced. MEASURED live: a pass created
    /// four issues and a list two seconds later showed none of them, so the next pass created four
    /// more. With this set the LIST endpoints answer empty while the by-number GET still reads
    /// through, which is exactly the shape that produced two copies of one board.</summary>
    public bool ListsAreStale { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(new Recorded(
            request.Method.Method, path, body,
            request.Headers.UserAgent.ToString(), request.Headers.Accept.ToString(),
            request.Headers.Authorization?.ToString()));

        if (Interlocked.Exchange(ref _holdArmed, 0) == 1)
        {
            _arrived!.TrySetResult();
            await _release!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        if (Outage is { } why) throw new HttpRequestException(why);

        var json = Route(request.Method.Method, path, request.RequestUri.Query, body);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private string Route(string method, string path, string query, string body)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /repos/{owner}/{repo}/...
        var tail = segments.Length > 3 ? segments[3..] : [];

        if (tail is ["issues"]) return method == "POST" ? CreateIssue(body) : ListIssues(query);
        if (tail is ["milestones"]) return method == "POST" ? CreateMilestone(body) : ListMilestones(query);
        if (tail is ["issues", var n] && int.TryParse(n, CultureInfo.InvariantCulture, out var number))
            // The by-number read is NOT stale even when the listing is — that asymmetry is the real
            // API's, and it is what makes reconciling against the actual document possible at all.
            return method == "GET" ? Serialize(_issues[number]) : PatchIssue(number, body);
        if (tail is ["issues", var m, "comments"] && int.TryParse(m, CultureInfo.InvariantCulture, out var owner))
            return method == "POST" ? AddComment(owner, body) : ListComments(owner, query);
        return "{}";
    }

    // GitHub pages from 1; a page past the end is an empty array, which is what stops the walk.
    private static bool PastFirstPage(string query) =>
        query.Contains("page=", StringComparison.Ordinal) && !query.Contains("page=1", StringComparison.Ordinal);

    private string ListIssues(string query) =>
        PastFirstPage(query) || ListsAreStale ? "[]" : "[" + string.Join(",", _issues.Values.Select(Serialize)) + "]";

    private string ListMilestones(string query) =>
        PastFirstPage(query)
            ? "[]"
            : "[" + string.Join(",", _milestones.Select(kv =>
                $"{{\"number\":{Num(kv.Value)},\"title\":{Str(kv.Key)},\"state\":\"open\"}}")) + "]";

    private string ListComments(int issue, string query) =>
        PastFirstPage(query) || ListsAreStale
            ? "[]"
            : "[" + string.Join(",", (_comments.TryGetValue(issue, out var list) ? list : [])
                .Select((b, i) => $"{{\"id\":{Num(i + 1)},\"body\":{Str(b)}}}")) + "]";

    private string CreateIssue(string body)
    {
        var doc = JsonDocument.Parse(body).RootElement;
        var issue = new Issue
        {
            Number = _nextIssue++,
            Title = Get(doc, "title"),
            Body = Get(doc, "body"),
            // The real API creates every issue open — `state` is ignored on POST. A fake that
            // honoured it would hide the mirror's follow-up close.
            State = "open",
            Labels = doc.TryGetProperty("labels", out var l)
                ? [.. l.EnumerateArray().Select(e => e.GetString() ?? "")] : [],
            Milestone = doc.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Number
                ? m.GetInt32() : null,
        };
        _issues[issue.Number] = issue;
        return Serialize(issue);
    }

    private string PatchIssue(int number, string body)
    {
        if (!_issues.TryGetValue(number, out var issue)) return "{}";
        var doc = JsonDocument.Parse(body).RootElement;
        if (doc.TryGetProperty("title", out var t)) issue.Title = t.GetString() ?? issue.Title;
        if (doc.TryGetProperty("body", out var b)) issue.Body = b.GetString() ?? issue.Body;
        if (doc.TryGetProperty("state", out var s)) issue.State = s.GetString() ?? issue.State;
        if (doc.TryGetProperty("milestone", out var m) && m.ValueKind == JsonValueKind.Number)
            issue.Milestone = m.GetInt32();
        if (doc.TryGetProperty("labels", out var l))
            issue.Labels = [.. l.EnumerateArray().Select(e => e.GetString() ?? "")];
        return Serialize(issue);
    }

    private string CreateMilestone(string body)
    {
        var title = Get(JsonDocument.Parse(body).RootElement, "title");
        if (!_milestones.TryGetValue(title, out var number))
        {
            number = _nextMilestone++;
            _milestones[title] = number;
        }
        return $"{{\"number\":{Num(number)},\"title\":{Str(title)},\"state\":\"open\"}}";
    }

    private string AddComment(int issue, string body)
    {
        var text = Get(JsonDocument.Parse(body).RootElement, "body");
        if (!_comments.TryGetValue(issue, out var list)) _comments[issue] = list = [];
        list.Add(text);
        return $"{{\"id\":{Num(list.Count)},\"body\":{Str(text)}}}";
    }

    /// <summary>Issue bodies come back with CRLF, exactly as GitHub stores them. This is what proves
    /// the reconciler's "is it already what we would write" comparison normalises line endings — a
    /// comparison that did not would report every card as changed on every pass.</summary>
    private static string Serialize(Issue i) =>
        $"{{\"number\":{Num(i.Number)},\"title\":{Str(i.Title)},\"body\":{Str(i.Body.ReplaceLineEndings("\r\n"))}," +
        $"\"state\":{Str(i.State)},\"html_url\":\"https://github.test/i/{Num(i.Number)}\"," +
        $"\"labels\":[{string.Join(",", i.Labels.Select(l => $"{{\"name\":{Str(l)}}}"))}]," +
        (i.Milestone is { } ms ? $"\"milestone\":{{\"number\":{Num(ms)},\"title\":\"\",\"state\":\"open\"}}" : "\"milestone\":null") +
        "}";

    private static string Get(JsonElement doc, string name) =>
        doc.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
    private static string Str(string s) => JsonSerializer.Serialize(s);
}
