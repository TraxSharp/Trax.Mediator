# Trax.Mediator

Decoupled dispatch: the train bus routes by input type instead of direct injection, plus
train discovery, the registry, and train-level authorization. It sits above `Trax.Effect`
and below `Trax.Scheduler`, `Trax.Api` and `Trax.Dashboard`.

This file is the entry point. It routes; it does not restate the rules.

## Architecture decisions

`docs/adr/` records **why** things are the way they are. A documentation page says what the
rule is; an ADR says whether it is a deliberate constraint or an accident, so you can tell
which ones are safe to change. Read the relevant one before proposing to change a rule, and
if your work contradicts one, say so rather than silently overriding it.

| Working on | Read first |
| --- | --- |
| `[TraxAuthorize]`, or anything in `TrainAuthorization/` | [0001](./docs/adr/0001-authorization-is-fail-closed.md), the default is fail-closed and the opt-out is a named call |

Decisions binding more than one repo live in the central corpus at `Trax.Docs/adr/`, whose
index lists them by repo. Nine name `mediator`. The one most likely to reach a change here
is `0007`, the canonical train name being the interface FullName, which
`InterfaceFullNameInvariantTests` in this repo enforces at the point of registration. In a
workspace checkout the index is at `../Trax.Docs/adr/README.md`; that path does not resolve
on GitHub, because it crosses a repository boundary.

## When your change makes a decision

Most changes do not. When one does (reversing it would cost something real, a future reader
would ask why it is like this, and there were real alternatives), it takes five steps and
the build enforces four. The `adr-guard` job runs on every pull request.

| | Step | Enforced |
| --- | --- | --- |
| 1 | Notice you made a decision, and write the ADR | no, this is the human step |
| 2 | Tag it `areas`, and add it to `docs/adr/README.md` | yes |
| 3 | Say where it stands in `## Status` and record it in `## Changelog` | yes |
| 4 | Give it `## Exemplars`: guards, `**Enforced elsewhere:**`, or `**Unenforced:**` with a reason | yes |
| 5 | Have each guard you named cite the ADR back, in its docstring and its failure message | yes |

Step 1 is the only one you have to remember, because no test can detect a decision you chose
not to record. The format is
[`.claude/skills/recording-decisions/ADR-FORMAT.md`](./.claude/skills/recording-decisions/ADR-FORMAT.md).

## Guards

`tests/Trax.Mediator.Tests.Meta/` holds thirteen convention guards. Eleven are shared with
other repos. Two are this repo's own: `InterfaceFullNameInvariantTests`, which pins the
canonical-name rule at registration, and `DICompositionSmokeTests`, which fails at the
registration point rather than at first use downstream when a wiring change drops a
required service.

The census is on: every guard class under that folder is either credited to an ADR or
carries `Not ADR-enforcing:` with a reason, and the `adr-guard` job checks it. A new guard is
unclassified until you choose, and the build says so. Opting out is a normal answer; a reason
that reads as a deferral is not.

## Running the tests

This repo ships no compose file. The Postgres integration suite reads a
`DatabaseConnectionString` and CI provides the database as a workflow service
(`postgres:17`, user `trax`, database `trax_data_tests`). Locally, point it at any Postgres
you already have, or bring one up from a sibling repo's compose file.

```bash
dotnet test                                   # everything that needs no database
dotnet test tests/Trax.Mediator.Tests.Meta    # the convention guards alone
```
