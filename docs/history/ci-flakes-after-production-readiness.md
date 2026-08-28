# History: three CI flakes, found and fixed after production-readiness shipped

> Written fresh, not preserved from `README.md` — this happened after the docs restructure
> (`design/production-readiness.md` item 19) that created this directory, so there was no README
> section to carry forward. Describes work done 2026-08-28, the same day items 12–19 of
> `design/production-readiness.md` §8 landed, once CI turned red on the very next push.

---

## Three CI flakes, found and fixed

The push that closed out `design/production-readiness.md`'s item-14/15/18 bug-fix round failed CI. Not
because anything shipped that day was wrong — because three separate, previously-invisible bugs all
happened to become visible at once, two of them pre-existing and one introduced by the fix round's own
new test. All three were root-caused from the actual CI logs (not guessed at, not "just rerun it") and
fixed the same session.

### 1. A new test that wasn't actually isolated

`SagaOrchestratorConcurrencyRedeliveryTests.LostRaceOnATerminalTransition_DoesNotRecordAPhantomSagaDuration`
(added that session, verifying the item-18 `SagaDuration` ordering fix) used a `MeterListener` with no
tag filter, on the theory that only this file's own two saga fixtures could ever record against it.
That reasoning missed the actual hazard: `MeterListener` is process-wide, and xUnit runs test classes in
parallel by default, so the listener could capture a `SagaDuration` recording from *any* saga in *any*
concurrently-running test, not just this file's own. Passed locally every time; failed CI with 2
recordings captured instead of 1. Fixed by filtering on `VSagaDiagnostics.TagSagaType`, matching the
pattern `SagaOrchestratorTracingTests`'s own duration listener already used correctly.

### 2. A pooled-memory bug in the RabbitMQ adapter

`RabbitMqTransportTests.PublishAndSubscribe_DeliversMessageWithCorrelationAndType` had been failing
intermittently on every .NET-touching CI run for the ~19 hours before this session (predates it
entirely — first seen on the packaging-metadata commit, item 8.1) with
`System.Text.Json.JsonException: '0x01' is an invalid start of a value` — garbage bytes where the JSON
payload should be.

Root cause: `RabbitMqTransport.DispatchReceivedAsync` passed `BasicDeliverEventArgs.Body` straight into
`ReceivedMessage`. RabbitMQ.Client 7.x backs that body with pooled/rented memory, valid only for the
duration of the delivery event handler — the client can reuse the buffer for a later frame on the same
connection the moment a handler awaits something, or (as here) returns while something else retains the
`ReceivedMessage` and reads its body later. The test captures the delivery into a
`TaskCompletionSource` and reads `received.Body` only after `await tcs.Task` completes — well outside
the handler's own synchronous scope. More concurrent message traffic (CI under load) makes the reuse
race land more often; the local/light-load case rarely hits it, which is exactly why this only ever
showed up in CI and never locally.

This was a latent production risk too, not just a test artifact — nothing about the orchestrator's own
message-handling path guarantees `received.Body` gets read before another delivery arrives and the
underlying buffer gets recycled.

Fixed by copying into a freshly-owned array (`ea.Body.ToArray()`) at the one place every caller's
`ReceivedMessage.Body` comes from, rather than asking every handler to know to copy defensively.
Verified with 8 consecutive full runs of the Testcontainers-backed suite, all green.

### 3. A test harness that raced its own determinism promise

`SagaTestHarnessTests.Timeout_FiresDeterministicallyWithoutRealWaiting` — named for exactly the property
it turned out not to have — failed intermittently with "Expected saga ... to be in state 'Failed' but
it was in 'AwaitingShipment'", despite `AdvanceTimeByAsync` explicitly claiming and handling the due
timeout itself immediately before the assertion. (Noted once before, in the item-14 session, as "a
pre-existing timing flake unrelated to this change" — this session is where it actually got
root-caused.)

`SagaTestHarness`'s constructor started every registered `IHostedService`, including
`SagaTimeoutDispatcherHostedService` and `SagaOutboxDispatcherHostedService` — both production
crash-recovery pollers whose `PeriodicTimer` is built against the harness's own `FakeTimeProvider`.
`TimeProvider.Advance()` inside `AdvanceTimeByAsync` wakes their timers too, so their own background
poll can race the harness's explicit `ClaimDueAsync`/`HandleTimeoutAsync` call for the *same* due
timeout: if the poller's untracked background `Task` claims it first, `AdvanceTimeByAsync`'s own claim
comes back empty and returns before that `Task` has actually finished handling it — so an assertion
immediately after can observe a stale, not-yet-transitioned state. More background scheduling noise (CI
under load) makes the poller's timer more likely to win that race, which is why it only ever showed up
there.

Neither poller does anything `AdvanceTimeByAsync` doesn't already do deterministically for the saga
under test, and the harness's in-process, no-real-crash nature means neither poller's actual
crash-recovery purpose ever applies inside it anyway — so the fix is to simply not start them, not to
try to make the race safe. Required `VSaga.Testing` to see the two `internal` hosted-service types to
skip; added via `InternalsVisibleTo`, following the precedent `VSaga.Http` already has for
`ISagaContextLogSink` (`VSaga.Core.csproj`). Verified with 20 consecutive runs, all green.

### Why this is worth remembering

None of these three were caught by code review, by the extensive adversarial-review workflow that
shipped items 13–19, or by any local `dotnet test` run in this session — every one of them needed the
actual CI environment (real parallelism, real CPU contention, a Linux runner with different scheduling
behavior than the local Windows/Docker Desktop setup) to surface. `docs/design/production-readiness.md`
§9's own verification section already says "run [the Testcontainers suites] before trusting" a commit;
this is the concrete version of why — not just "Docker might be unavailable," but "Docker being
available and under real load surfaces races nothing else will."
