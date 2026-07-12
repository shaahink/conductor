using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class AbortCommand() : CtlCommand("abort", "the running conductor will kill the session and stop", dangerous: true);
