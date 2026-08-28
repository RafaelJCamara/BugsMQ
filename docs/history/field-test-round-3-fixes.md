# History: field test round 3 — packaging, docs, and dashboard fixes

A third end-user field test (after the UX field test and round 2 — see `signalr-hub-and-polling-service-tests.md`
and `dashboard-pagination-sorting-signalr-fix.md` for their own fix passes) deliberately covered paths
the first two hadn't: consuming locally-packed NuGet/npm packages from fresh throwaway projects, the
in-memory-to-real-broker upgrade path, CommonJS TypeScript consumers, a full docs-vs-code sweep, and
dashboard failure modes (an API restart while the tab stayed open). It surfaced 26 findings — 6 high,
12 medium, 8 low — all fixed the same day.

## Packaging

**All seven `@vsaga/*` npm packages were "masquerading as ESM" for CommonJS consumers.** Each
`package.json`'s `exports` map declared a single `types` condition pointing at the ESM `index.d.ts`,
so the shipped `index.d.cts` (built and present in every tarball) was never referenced — a CommonJS
TypeScript project (`module: node16`/`nodenext`) importing any package failed with **TS1479**
("cannot be imported with require") even though the runtime `require()` call worked fine. Fixed by
nesting `types` per condition: `import` resolves `index.d.ts`, `require` resolves `index.d.cts`.
Verified with `arethetypeswrong --pack` on all seven repacked tarballs (all now report "No problems
found 🌟", down from 👺 FalseESM on every one) and by typechecking a real CJS TS 7 consumer project
against the fix — TS1479 gone. Worst-hit package was `@vsaga/nestjs`, since NestJS projects default to
CommonJS.

**All 16 NuGet packages packed with no readme, no symbols, no Source Link.** `dotnet pack` warned
"missing a readme" on every package, and there was no `.snupkg`/debuggable-sources story at all. Fixed:
a `README.md` per packable `dotnet/src/*` project (16 total, each with an install snippet and a link
into the real docs), wired via a shared `Directory.Build.props` conditional (`PackageReadmeFile` +
`None Include` gated on `Exists($(MSBuildProjectDirectory)\README.md)`, so no per-project csproj
edits were needed) — plus repo-wide `IncludeSymbols`/`SymbolPackageFormat=snupkg` and
`PublishRepositoryUrl`/`EmbedUntrackedSources` (Source Link's required pair) with
`Microsoft.SourceLink.GitHub` added to central package management. `ContinuousIntegrationBuild` is
gated on `GITHUB_ACTIONS` so it doesn't strip local machine paths from an ordinary local `dotnet pack`.
No `PackageIcon` yet — that needs real artwork, deliberately not fabricated here. Verified by packing
the whole solution: 16 `.nupkg` + 16 `.snupkg`, zero "missing a readme" warnings.

**npm tarballs carried no LICENSE text and no `bugs` field.** `npm pack --dry-run` confirmed npm
already includes a package's `LICENSE` file in the tarball automatically, even without listing it in
`files` — so the actual gap was that no package directory *had* one. Copied the repo's MIT `LICENSE`
into each of the seven packages and added `bugs.url` to every `package.json`.

## `.NET` code and configuration

**`SagaOrchestratorOptions`/`SagaOutboxOptions` had no documented, discoverable configuration path.**
`AddVSagaEngine` takes no options delegate of its own; the only way to override `MaxDeliveryAttempts`
or the outbox `Mode` was pre-registering your own singleton *before* calling it — an idiom only the
test suite demonstrated. Added `SagaEngineBuilder.ConfigureOrchestrator`/`ConfigureOutbox`, two
additive fluent methods that register a configured options instance inside the same
`AddVSagaEngine(...)` delegate (safe because they run after `AddVSagaEngine`'s own `TryAddSingleton`
calls, so the configured instance is the one resolved). `docs/configuration.md` also claimed every
options class binds via `services.Configure<T>(...)` — untrue anywhere in this codebase; every options
class is a plain singleton, not `IOptions<T>`. Rewrote the intro to say so and documented the new
fluent methods. Covered by `EngineOptionsConfigurationTests.cs`.

**The RabbitMQ unroutable-publish exception named no likely cause.** The single most common way to
hit it — moving a saga from the in-memory transport to a real broker for the first time, with nothing
yet subscribed to the message being published — produced only "was returned as unroutable by the
broker.", with no hint. Added a `detail` string naming the unbound routing key/queue and asking
whether anything has called `SubscribeAsync` for that message type yet. `getting-started.md` and
`transports/rabbitmq.md` both got a callout explaining the upgrade-path failure mode before a reader
hits it blind. Covered by a new assertion in `RabbitMqTransportTests.Publish_ToUnboundRoutingKey_ThrowsUnroutablePublishException`.

**A malformed correlation id in a dashboard detail-page URL crashed the SignalR hub invocation.**
`SagaHub.SubscribeToSaga(string, Guid)` let SignalR's own model binder reject a non-Guid argument by
failing the whole RPC — surfacing client-side as "Failed to invoke 'SubscribeToSaga' due to an error on
the server", needless noise for what's usually a stale or hand-edited URL. Changed the parameter to
`string` and parse it with `Guid.TryParse` inside the method, joining no group (not throwing) on a
parse failure — matching how the REST endpoint for the same id already degrades to a clean `404`.
Covered by two new `SagaHubTests`.

**`persistence.md`'s EF Core wiring snippet silently produced an empty database.** It showed
`AddVSagaEfCore(db => db.UseNpgsql(connectionString))` with no `MigrationsAssembly`, so
`MigrateAsync()` looked for migrations in the wrong assembly, logged "no migrations were applied", and
the first saga died with `relation "SagaInstances" does not exist`. Fixed the snippet to match
`VSaga.Dashboard.Api`'s own real registration (`npgsql.MigrationsAssembly("VSaga.Persistence.EFCore.Postgres")`)
and added the missing scope + `MigrateAsync()` startup block.

## Dashboard (Angular)

**Live updates died permanently after a dashboard API restart, with no indication.** Three
compounding gaps in `saga-hub.service.ts`: no `onreconnected` handler ever re-invoked
`SubscribeToList`/`SubscribeToSaga`, so even a successful reconnect rejoined zero SignalR groups
(group membership is server-side state, lost on every reconnect); the default
`withAutomaticReconnect()` gives up for good after ~30s of backoff; and there was no connection
indicator anywhere. Reproduced live: the saga count sat frozen for 90+ seconds after the API was
healthy again, looking perfectly alive while orders kept flowing. Fixed with a custom `IRetryPolicy`
that never gives up (a short backoff, then every 30s indefinitely), `onreconnected` re-subscribing to
the list group and every tracked saga-detail group, and a `connectionState$` observable driving a
"reconnecting…"/"disconnected" banner in both `saga-list` and `saga-detail` — gated on a
`hasEverConnected` flag so it doesn't flash during the ordinary brief window before the very first
connect resolves. Covered by new tests in `saga-hub.service.spec.ts` (indefinite retry, resubscribe on
reconnect, connection-state transitions).

**Filters, search, and page never synced with the URL, in either direction.** Navigating to
`/sagas?sagaType=X` showed the unfiltered list, and choosing filters wrote nothing back to the address
bar — no shareable filtered link, and a refresh (previously the only recovery from the frozen-list bug
above) threw away the current view. `saga-list.ts` now reads its initial filter/page/sort state from
the route's query params on load and writes them back after every change.

**Search only fired on Enter, with no indication that's how it worked.** Typing a term produced no
request and no spinner — indistinguishable from search being broken. Replaced the `(keyup.enter)`
trigger with a 300ms-debounced live search on every keystroke.

**A saga that failed on its very first outbound publish rendered a bare, failure-free map.** No edge
was ever logged (the publish itself threw before an outbound entry was recorded), so the map correctly
had nothing to draw — but nothing on screen said so either, until the user manually scrubbed or
pressed Play. Added a `failedWithNothingToShow` indicator that surfaces the failure immediately when
the map has essentially nothing else on the canvas to tell that story.

## Verification

Full local reproduction of every CI job before pushing: `dotnet build`/`dotnet test` (Release, zero
warnings, 349/349 passing), `npx ng build`/`npx ng test --watch=false` (135/135 passing), and
`npm run lint && npm run format:check && npm run typecheck && npm run build && npm run test` (122/122
passing) from `typescript/`. An adversarial multi-agent review pass across the `.NET`/dashboard/
packaging/docs changesets ran before commit, independently re-verifying every reviewer finding against
the actual source rather than trusting the review's own wording.
