# History: SignalR hub and polling service tests

> Preserved verbatim from the original `README.md`. Describes commit `55eb433` ("Test the SignalR
> hub, notifier, and polling service"). See [`../testing.md`](../testing.md) and
> [`../dashboard.md`](../dashboard.md) for current reference documentation.

---

## SignalR hub and polling service tests

Closes a gap the "out of scope for v1" note above had carried since the first commit: neither
`SagaHub`, `SignalRSagaChangeNotifier`, nor `SagaChangePollingService` had a single test. That was
tolerable while the hub's contract was stable. It stopped being tolerable once the saga-identity pass
changed `SubscribeToSaga` from `(correlationId)` to `(sagaType, correlationId)` and renamed the
per-saga group to `saga:{sagaType}:{correlationId}` — a regression to the old shape would have
compiled, passed CI, and broken live updates on every detail page at runtime. That change was verified
by driving a real hub connection by hand against the running stack, which is not something CI repeats.

**Coverage.** `SagaHubTests` pins the group-name format and the subscribe/unsubscribe membership
contract, including that two saga types sharing a correlation id join two distinct groups and that
leaving one doesn't remove the connection from the other's. `SignalRSagaChangeNotifierTests` pins the
in-process path: `SagaUpdated` reaches both the list group and the instance group, `TimelineEntryAdded`
reaches only the instance group and carries the saga type as a payload argument (the client filters on
it before appending). `SagaChangePollingServiceTests` covers the cross-process path that actually
delivers live updates in the deployed topology — sagas run in the OrderProcessing process, so the
notifier never fires in the dashboard and this diff-and-push loop is the only route.

**One small production change, for testability.** `SagaChangePollingService`'s tick body was extracted
into `PollOnceAsync(since, ct)`, returning the new watermark; `ExecuteAsync` is now just the timer loop
and its error handling. The alternative was a test that advances a clock and races a background task's
continuation. The class stays `internal` — it is composition-root wiring, not API surface — and the
test project reaches it through `InternalsVisibleTo` rather than being promoted to public.

Extracting it also made one thing explicit that was previously implicit: the watermark advances only
*after* the pushes succeed, so a tick that throws leaves it untouched and the next tick retries the same
window instead of skipping past it.

**Verified by mutation, not assumed.** Dropping the saga type from `GroupForSaga` fails six tests across
all three files; loosening the watermark comparison from `>` to `>=` fails exactly the two poller tests
that pin that boundary. Nothing else moves in either case.
