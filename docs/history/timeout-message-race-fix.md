# History: timeout/message race fix

> Preserved verbatim from the original `README.md`. Describes commit `c24928d` ("Fix saga
> timeout/message race that could ship and refund the same order"), which closes a race
> [`chaos-engineering-middleware.md`](chaos-engineering-middleware.md) found but deliberately left unfixed.

---

## Timeout/message race fix

Closes the race the chaos-testing pass above found but deliberately left unfixed. In
`SagaOrchestrator<TState>.HandleTimeoutAsync` (`dotnet/src/VSaga.Core/Runtime/SagaOrchestrator.cs`), a due
timeout used to call straight into the saga definition — which is what runs `.Compensate()`/`.Publish()`
side effects — *before* persisting anything. If a normal message read the same snapshot version
concurrently (the exact "reply landed mere seconds before the timeout" scenario above), the timeout's
side effects went out over the transport regardless of whether its own subsequent persist then lost
the optimistic-concurrency check against that message's write.

The fix: `HandleTimeoutAsync` now claims the timeout with a version-checked persist *before* calling
into the saga definition at all, reusing the exact same `SagaConcurrencyException` mechanism that
already protected the final write — just moved earlier. A stale timeout is now detected and abandoned
before anything can be published, not after. A second, narrower window remains — a concurrent write can
still land between the claim succeeding and the timeout's own final persist, since real
`Compensate()`/`Publish()` I/O (and any step-level `RetryPolicy` delay) sits in between, and that
persist has no further claim to fall back on — every design considered for this fix (a version recheck
just before publishing, a claim-then-persist split, or folding claim-and-persist into one envelope)
shares this same residual limitation, since none of them serialize against a write landing mid-step
without a lock or an outbox-style deferred-publish redesign, both bigger changes than this fix's scope.
What changed for that narrower case: it's now caught and logged distinctly
(`"...lost a second race after its Compensate()/Publish() side effects already ran..."`) instead of
propagating an uncaught `SagaConcurrencyException` out to `SagaTimeoutDispatcherHostedService`'s
generic catch-and-log, which is what happened before this fix for *any* race, wide or narrow.

`dotnet/tests/VSaga.Core.Tests/SagaOrchestratorTimeoutRaceTests.cs` covers both windows deterministically —
same controlled-fake technique as `SagaOrchestratorInfrastructureFailureTests`, decorating the
snapshot store to inject a concurrent reply synchronously at the exact call site that matters, rather
than relying on real timing. One test proves the pre-claim race no longer publishes any compensation
side effect; the other documents the accepted post-claim leak and proves it now fails gracefully
instead of throwing uncaught.

Verified live against the real chaos-enabled docker-compose stack with the fix applied: a ~20-minute
run processed 190 sagas (88 `Completed`, 37 `Failed`, 27 `TimedOut` via `AwaitingPayment` compensation,
38 still `Running`) with zero sagas that were both `Completed` and had a `RefundPayment`/
`ReleaseInventory` in their timeline, and zero `SagaConcurrencyException` anywhere in the logs — the
exact race window that surfaced the original bug is narrow enough that this run's real chaos timing
didn't happen to land in it (consistent with the original catch needing its own dedicated pass to
find), but the deterministic tests above force both interleavings directly rather than depending on
that timing.

Two secondary gaps the original chaos pass also flagged got picked up here:

- **Participant-level dedup.** `InventoryParticipant`/`PaymentParticipant`/`ShippingParticipant` had no
  idempotency guard of their own, unlike the saga orchestrator's `IsDuplicateAsync` — a chaos-duplicated
  command (or a genuine broker at-least-once redelivery) ran its business side effect twice, as the
  `Duplicate`+`Drop`/`Delay` finding above shows for `ReserveInventory`. Fixed with a small, bounded,
  process-local `MessageId` dedup guard added once to the shared `ParticipantService` base
  (`dotnet/samples/VSaga.Samples.OrderProcessing/Participants/ParticipantService.cs`), covering all three
  participants — not durable across restarts, which is an honest limitation of its own, but enough to
  absorb the near-immediate redelivery chaos testing (and a real broker) actually produces.
- **`AwaitingInventory`/`AwaitingShipment` have no `WithTimeout`.** Left as-is, deliberately: the
  "States without a configured timeout" paragraph above already documents this as an intentional
  choice — chaos testing surfacing it as a real, honest gap rather than something to quietly patch in
  the same pass that found it. Revisit only if that framing should change. **The framing changed** —
  see [`timeout-coverage-every-awaiting-state.md`](timeout-coverage-every-awaiting-state.md) below.
