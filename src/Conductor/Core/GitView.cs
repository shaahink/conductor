using System.Text;

namespace Conductor.Core;

/// <summary>Read-only git snapshot for the dashboard's git view — recent commits + working tree.</summary>
public static class GitView
{
    public static string Summary(string repo, int commits = 15)
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine($"branch: {Git.Branch(repo)}   HEAD: {Short(Git.Head(repo))}");
            sb.AppendLine();

            var status = Git.Exec(repo, "status", "--short").Output.Trim();
            sb.AppendLine("working tree:");
            sb.AppendLine(string.IsNullOrEmpty(status) ? "  (clean)" : Indent(status));
            sb.AppendLine();

            var log = Git.Exec(repo, "log", "--oneline", "--decorate", "-n", commits.ToString()).Output.Trim();
            sb.AppendLine($"recent commits (last {commits}):");
            sb.AppendLine(string.IsNullOrEmpty(log) ? "  (none)" : Indent(log));
        }
        catch (Exception ex) { sb.AppendLine($"git unavailable: {ex.Message}"); }
        return sb.ToString().TrimEnd();
    }

    private static string Indent(string s) => string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => "  " + l));
    private static string Short(string sha) => sha.Length >= 7 ? sha[..7] : sha;
}
