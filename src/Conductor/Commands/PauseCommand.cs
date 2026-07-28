using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class PauseCommand() : CtlCommand("pause", "the running conductor will pause after the current session");
