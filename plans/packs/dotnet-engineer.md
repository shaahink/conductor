## Pack: .NET engineer — house style for this codebase

C# 13 / .NET 10, `TreatWarningsAsErrors`, full Meziantou ruleset. The analyzer is not advisory: the build
fails. Write it right the first time and you will not fight it.

**Language.** File-scoped namespaces. `sealed` by default. Primary constructors for classes that take
dependencies and assign them. `record` for data-only types (verdicts, results, DTOs) — value semantics and
`with` expressions. Collection expressions (`[1, 2, 3]`). Raw string literals (`"""..."""`) for SQL and
multi-line templates. `using var` for every `IDisposable` (SqliteCommand, DataReader, Process).

**Async — this codebase is strict about it.**
- `ConfigureAwait(false)` on **every** await in engine code (not in tests).
- Thread a real `CancellationToken` everywhere. `CancellationToken.None` in an async method is a smell.
- `await Task.Delay`, never `Thread.Sleep`.
- **No `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.** The one sanctioned exception is a
  Spectre.Cli `Execute` that must return `int` — and it carries an inline `#pragma` with that
  justification. Adding a new pragma anywhere else fails the ratchet gate.
- Anything that blocks for more than a moment belongs in an `async` path or in `conductor bg`.

**Null safety.** Nullable reference types are on. `is { } x` for non-null checks, `is not null` in guards,
`ArgumentNullException.ThrowIfNull` at public entry points.

**Collections and strings.** Always pass an explicit comparer: `StringComparer.Ordinal` or
`OrdinalIgnoreCase` for dictionaries, `HashSet<string>`, and every `Contains`/`Equals`/`IndexOf`. The
analyzer will fail you for the culture-sensitive default, and it is right to.

**Correctness.** Regex needs an explicit timeout. SQL is always parameterised (`@param`) and lives only
behind the store. `System.Text.Json`, never Newtonsoft. Secrets come from env vars, never from source.

**Exceptions.** Catch what you can actually handle, and say why in the `when` clause or a comment. A bare
`catch (Exception)` that swallows is how this project ended up with a database whose writes silently did
nothing for eight months. If you swallow, log — and if it is on a hot path, emit an event.
