# Architecture

> Started at K2.3 with the file-organisation convention, because that is the rule reviewers needed to be
> able to cite. K2.4 adds the rest of the map.

## The projects, and which way they point

```
tools/plan-lint          consumes Conductor.Planning ALONE - the standalone proof
        |
src/Conductor            CLI (Program.cs, Commands/**) + hosting (Hosting/, Http/ControlPlaneServer*)
        |  ProjectReference
src/Conductor.Core       domain, orchestration, store, events, providers, integrations  <- the engine
        |  ProjectReference
src/Conductor.Planning   pure decision logic: data in, decisions out. No IO, no clocks, no processes.
```

Each arrow points one way and only one way. `Conductor.Core` does not reference `Conductor`, so a
command cannot be called from the run loop and the store cannot format console output - not by
convention, but because it does not link. `tests/Conductor.Tests/ArchitectureBoundaryTests.cs` states
the rest of the rules as tests that name the offending type, and they run in the `engine-full` gate.

## The file-organisation convention

One rule, four cases. It exists because "no file over 500 lines" was already true when
`ControlPlaneDto` was **thirty files** and `ConductorEvent` was eleven: file size said healthy, and
finding anything still needed a grep.

### 1. A file is named after what it declares - always

`ControlPlaneDto.Lanes2.cs`, `TelegramService.Dto3.cs`: a number appended to a filename is the sound of
a name running out, and it means the file is a drawer rather than a subject. The types in those files
had nothing to do with `ControlPlaneDto` or `TelegramService` at all. **If the filename prefix names a
type the file does not declare, the prefix is a filing convention pretending to be a type.** Split it.

### 2. A partial is legitimate when it is one type with one identity, split for size

`ControlPlaneServer` (11 files), `VerdictEngine` (8), `SqliteRunStore` (7), `SessionRunner` (6),
`RunLoop` (5), `TelegramService` (5) are all genuinely `partial` declarations of a single type that
holds a single set of fields. Splitting *those* would mean inventing handler objects and threading
state through them - a redesign, not a reshape, and nothing about it would make the code easier to
find. They stay, and each partial is named for the aspect it holds (`.Endpoints`, `.Plan`, `.Tasks`).

**The test:** open the file. If everything in it operates on the parent type's fields, the partial is
real. If the file declares independent types that merely *relate* to the parent, it is a pile.

### 3. Endpoint contracts live in a folder per feature, under one namespace

```
src/Conductor.Core/Http/
  ControlPlaneMapper.cs        engine snapshot -> wire contract, the only place that conversion happens
  ControlPlaneJsonContext.cs   the source-generated serializer registry (every DTO is listed here)
  ControlPlaneDiscovery.cs     where a run advertises itself
  Contracts/
    State/  Sessions/  Plan/  Tasks/  Knowledge/  Telegram/  Processes/  Control/  OwnerQueue/
```

A new endpoint's request and result records go in the folder for its feature, in one file named for
the endpoint (`Tasks/TaskSplitDtos.cs` holds the request, the child and the result - they are one
exchange). Then add the type to `ControlPlaneJsonContext`, or it will not serialise.

The namespace stays flat - `Conductor.Core.Http` for every contract, whatever folder it is in. This is
a deliberate exception to "folders map to namespaces", and the reason is that the wire contract is a
**published protocol**: the Face, `conductor face` and the fleet scan all speak it as one vocabulary,
and ten namespaces would mean ten `using` lines in every server file to describe one payload. Folders
organise the *files*; the namespace describes the *protocol*.

### 4. Events live in `Events/Kinds/`, grouped by what they are about

Every durable fact the run records is a record deriving from `ConductorEvent`, and every one of them
lives in `src/Conductor.Core/Events/Kinds/` - `RunEvents.cs`, `SessionEvents.cs`, `StageEvents.cs`,
`GateEvents.cs`, `TaskEvents.cs`, `LaneEvents.cs`, `PlanEvents.cs`, `OwnerControlEvents.cs`,
`BlockedUntilEvents.cs`. `Events/` itself holds the machinery: the base type, the log, the projection,
the metrics.

A new event goes in the file for its subject, never next to the code that raises it -
`ArchitectureBoundaryTests.EventTypesStayInTheEventNamespace` fails the build if it does. The event log
is the run's only durable truth and every read endpoint folds it; an event declared beside its raiser
is invisible to anyone asking "what can happen here".

## What K2.3 split, and what it left

| Pile | Was | Now | Why |
|---|---|---|---|
| `ControlPlaneDto` | 30 files, 67 records, **no such type existed** except a static mapper | `Http/Contracts/<feature>/`, mapper renamed `ControlPlaneMapper` | the prefix named nothing; per-endpoint contracts wearing one type name |
| `ConductorEvent` | 11 files, independent records incl. a `Lanes2` | `Events/Kinds/<subject>.cs` | same fiction; only `ConductorEvent.cs` declared the type |
| `TelegramService.Dto{,2,3}` | 3 files, 7 Telegram Bot API records | `Integrations/TelegramApi/` | an external API's wire types, not parts of the service |
| `ControlPlaneServer` | 11 partials | **left** | one type, one field set, split by endpoint group - case 2 |
| `VerdictEngine`, `SessionRunner`, `RunLoop`, `SqliteRunStore`, `TelegramService` | 5-8 partials each | **left** | same - measured, not assumed: every file declares the parent type |
| `Models/` keeping the `Conductor.Models` namespace inside `Conductor.Core` | - | **left** | renaming it touches every file in the repo that names a config record, for no structural gain |
