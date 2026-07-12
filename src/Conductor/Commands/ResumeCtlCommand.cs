using Spectre.Console.Cli;

namespace Conductor.Commands;

public sealed class ResumeCtlCommand() : CtlCommand("resume", "a paused/needs-human conductor will continue");
