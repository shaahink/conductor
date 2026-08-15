using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Github;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS9.1 — where the GitHub token comes from, and what the mirror says when it comes from nowhere.
///
/// <para>The precedence rule is not invented here: it is the rule <c>TelegramService.ResolveToken</c>
/// already uses for the bot token (env wins, local secrets file is the fallback). Two credentials
/// resolved by two different rules is the kind of difference nobody discovers until the wrong one is
/// in force, so the rule is asserted rather than described.</para>
///
/// <para>The refusal is tested as TEXT because "prints exactly what is missing rather than a stack
/// trace" is the acceptance, and a refusal that names only one of the two places it looked sends the
/// operator to set a variable that was never going to be read.</para>
/// </summary>
public sealed class KS9_1GithubTokenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ks9-1-token", Guid.NewGuid().ToString("N"));
    private readonly string? _restore = Environment.GetEnvironmentVariable(GithubIdentity.TokenEnvVar);

    /// <summary>StateDir is <c>&lt;repo&gt;/.conductor</c>, so the fixture's repo is the temp dir and
    /// the secrets file lands under it — never in this repo's own live state directory.</summary>
    private PlanConfig Plan() => new() { Name = "ks9", Repo = _dir };

    private string StateDir => Plan().StateDir;

    [Fact]
    public void EnvironmentWinsOverTheSecretsFile()
    {
        Directory.CreateDirectory(StateDir);
        SecretsStore.WriteGithubToken(StateDir, "from-the-file");
        Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, "from-the-env");

        var (token, source) = GithubIdentity.ResolveToken(Plan());

        Assert.Equal("from-the-env", token);
        Assert.Equal(GithubIdentity.TokenEnvVar, source);
    }

    [Fact]
    public void TheFileAnswersWhenTheEnvironmentIsUnset()
    {
        Directory.CreateDirectory(StateDir);
        Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, null);
        SecretsStore.WriteGithubToken(StateDir, "  from-the-file  ");

        var (token, source) = GithubIdentity.ResolveToken(Plan());

        Assert.Equal("from-the-file", token);
        Assert.Equal(GithubIdentity.SecretsPath(Plan()), source);
    }

    [Fact]
    public void NeitherSourceIsNullAndTheRefusalNamesBoth()
    {
        Directory.CreateDirectory(StateDir);
        Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, null);

        var (token, source) = GithubIdentity.ResolveToken(Plan());
        Assert.Null(token);
        Assert.Equal("", source);

        var refusal = string.Join("\n", GithubIdentity.MissingTokenRefusal(Plan()));
        Assert.Contains("CONDUCTOR_GITHUB_TOKEN", refusal, StringComparison.Ordinal);
        Assert.Contains("secrets.local.json", refusal, StringComparison.Ordinal);
        Assert.Contains("nothing was contacted", refusal, StringComparison.Ordinal);
    }

    /// <summary>The two credentials share one file. Writing the second must not drop the first —
    /// serializing a fresh document would, and the loss would only surface the next time Telegram
    /// was used, long after the change that caused it.</summary>
    [Fact]
    public void WritingTheGithubTokenLeavesTheTelegramTokenAlone()
    {
        Directory.CreateDirectory(StateDir);
        SecretsStore.WriteTelegramToken(StateDir, "bot-token");
        SecretsStore.WriteGithubToken(StateDir, "gh-token");

        Assert.Equal("bot-token", SecretsStore.TryReadTelegramToken(StateDir));
        Assert.Equal("gh-token", SecretsStore.TryReadGithubToken(StateDir));

        SecretsStore.WriteTelegramToken(StateDir, "bot-token-2");
        Assert.Equal("gh-token", SecretsStore.TryReadGithubToken(StateDir));
    }

    /// <summary>api.github.com by default; the override is an announced escape hatch, not a silent
    /// redirect. A write target that could be moved without a word is how a proof posts to the wrong
    /// repository.</summary>
    [Fact]
    public void TheApiBaseDefaultsToGithubAndAnOverrideIsVisible()
    {
        var before = Environment.GetEnvironmentVariable(GithubClient.ApiBaseEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(GithubClient.ApiBaseEnvVar, null);
            Assert.Equal("https://api.github.com", GithubClient.ApiBase);
            Assert.False(GithubClient.ApiBaseIsOverridden);

            Environment.SetEnvironmentVariable(GithubClient.ApiBaseEnvVar, "http://127.0.0.1:1/");
            Assert.Equal("http://127.0.0.1:1", GithubClient.ApiBase);
            Assert.True(GithubClient.ApiBaseIsOverridden);
        }
        finally { Environment.SetEnvironmentVariable(GithubClient.ApiBaseEnvVar, before); }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GithubIdentity.TokenEnvVar, _restore);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }
}
