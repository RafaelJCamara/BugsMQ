# Contributing to vSaga

## Build

```bash
dotnet build dotnet/VSaga.slnx
```

Must stay clean with **zero warnings** — `TreatWarningsAsErrors` is on
(`dotnet/Directory.Build.props`) across every project, backed by SonarAnalyzer.CSharp,
Meziantou.Analyzer, and AsyncFixer.

```bash
cd typescript && npm install && npm run lint && npm run typecheck && npm run build
cd typescript/dashboard-web && npm install && npx ng build
```

The first command covers `typescript/packages/*` (the SDK) and `typescript/samples/*` (runnable
participants), which are one npm workspace. `dashboard-web` is deliberately not a member of it and
needs its own `npm install` — hence the second line.

## Test

```bash
dotnet test dotnet/VSaga.slnx
```

300+ tests across every `dotnet/tests/*` project. Five suites are Testcontainers-backed (RabbitMQ,
MassTransit, Wolverine, Brighter, Postgres) and need Docker; everything else runs without it. If
Docker isn't available, say so rather than skipping silently — this repo's own history treats "these
five suites were only compiled, never run" as an explicit, carried-forward caveat, not a pass.

```bash
cd typescript && npm run test
cd typescript/dashboard-web && npx ng test --watch=false
```

**Live verification**, for anything touching message flow, envelope headers, or timing (a new
transport adapter, a change to correlation/causation, anything outbox- or timeout-related):

```bash
docker compose up -d --build
# and, for fault-injection-relevant changes:
docker compose -f docker-compose.yml -f docker-compose.chaos.yml up -d --build
```

Filter queries by `createdAtUtc`/`updatedAtUtc` after the container's own start timestamp — the named
Postgres volume is **not** reset by `docker compose up` (see
[`docs/persistence.md`](docs/persistence.md#the-volume-caveat)). Use `docker compose down -v` for a
genuinely clean read.

**Mutation testing**, for anything envelope/header/linkage-adjacent: deliberately break the change
(comment out a header copy, revert a scoping predicate, remove a guard), confirm *exactly* the tests
written for it fail and nothing else does, then restore. This repo's commit history
(`docs/history/`) is full of concrete examples of this discipline and the bugs it caught that a
"does a test exist" check alone would have missed.

## Commit conventions

One logical change per commit. A subject line in imperative present tense, specific about what
changed, e.g.:

```
Add SagaState.BusinessKey with a partial-unique reservation index
Fix SagaTimeoutDispatcherHostedService's captive-dependency bug
Route PublishChildSagaFinishedAsync through the outbox
```

The body explains **what** changed and, more importantly, **why** — the design trade-off, the bug
being fixed, or the constraint that forced the shape. `git log --oneline` is this repo's own best
reference for the expected tone and level of detail.

Never skip pre-commit hooks or a failing check to get a commit in. If a build or test is red, fix the
underlying issue before committing, not after.

## Before opening a PR

- `dotnet build dotnet/VSaga.slnx` is clean (zero warnings) and `dotnet test dotnet/VSaga.slnx` passes.
- If you touched `typescript/packages/*`: `npm run lint && npm run typecheck && npm run build && npm
  run test` all pass from `typescript/`.
- If you touched `typescript/dashboard-web`: `npx ng build && npx ng test --watch=false` pass from
  that directory.
- If your change touches message flow, headers, correlation, or timing, you've live-verified it
  against `docker compose up` (and the chaos overlay, where relevant) — not just unit tests.
- New reference behaviour is documented in `docs/`, not left only in a commit message or code comment.

CI (`.github/workflows/ci.yml`) runs three independent jobs on every push/PR to `main`: `.NET build &
test`, `Angular build & test` (`typescript/dashboard-web`), and `TypeScript SDK build & test`
(`typescript/`, lint + format check + typecheck + build + test). All three must pass.
