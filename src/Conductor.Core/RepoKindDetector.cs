namespace Conductor.Core;

/// <summary>The build system a repo advertises at its root.</summary>
public enum RepoKind { Generic, Dotnet, Node, Go, Rust, Python }

/// <summary>
/// W4.1: repo-type detection and the gate commands that follow from it, in Core so both entry
/// points can use them. `conductor init` has proposed build+test gates from a marker file since
/// M8.2; `plan import` proposed none, so an imported plan arrived gateless and every session
/// verdict fell back to commits alone. Same signal, same answer, one implementation.
/// </summary>
public static class RepoKindDetector
{
    /// <summary>Cheapest reliable signal: the presence of a build-system marker file at the repo root.</summary>
    public static RepoKind Detect(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo) || !Directory.Exists(repo)) return RepoKind.Generic;
        bool Any(params string[] globs) => globs.Any(g =>
            g.Contains('*', StringComparison.Ordinal)
                ? Directory.EnumerateFiles(repo, g, SearchOption.TopDirectoryOnly).Any()
                : File.Exists(Path.Combine(repo, g)));

        if (Any("*.sln", "*.slnx", "*.csproj", "*.fsproj")) return RepoKind.Dotnet;
        if (Any("go.mod")) return RepoKind.Go;
        if (Any("Cargo.toml")) return RepoKind.Rust;
        if (Any("package.json")) return RepoKind.Node;
        if (Any("pyproject.toml", "setup.py", "requirements.txt")) return RepoKind.Python;
        return RepoKind.Generic;
    }

    public static (string Build, string Tests) GatesFor(RepoKind kind) => kind switch
    {
        RepoKind.Dotnet => ("dotnet build", "dotnet test"),
        RepoKind.Node => ("npm run build", "npm test"),
        RepoKind.Go => ("go build ./...", "go test ./..."),
        RepoKind.Rust => ("cargo build", "cargo test"),
        RepoKind.Python => ("python -m compileall -q .", "pytest -q"),
        _ => ("", ""),
    };
}
