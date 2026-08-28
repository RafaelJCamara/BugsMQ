# History: dashboard list pagination, sorting, and a SignalR live-updates fix

> Preserved verbatim from the original `README.md`. Describes commit `0750072` ("Add saga service
> map, dashboard pagination/sorting, and fix live-updates auth").

---

## Dashboard list: pagination, sorting, and a SignalR live-updates fix

**SignalR hub negotiate returning 401.** Live updates (the saga list refreshing, a saga detail page's
Map/Timeline re-fetching) were silently broken — the hub connection never completed, so the UI only
ever showed what it had at page load. Root cause: `ApiKeyAuthenticationHandler` originally only
checked the `X-Api-Key` header and the `?access_token=` query string, but SignalR's JS client sends
its `accessTokenFactory` token as an `Authorization: Bearer` header on the negotiate HTTP call, and
only falls back to the query string for the actual WebSocket/SSE upgrade (which can't carry custom
headers). Fixed by adding the `Authorization: Bearer` check (see "Dashboard API authentication"
above); verified live via the browser's own network/console logs — negotiate returns `200` and the
WebSocket connects, e.g. `WebSocket connected to ws://localhost:5080/hubs/saga?...&access_token=...`.
Regression-covered by `HubNegotiate_*` tests in `ApiKeyAuthTests.cs`, which the auth handler
previously had no coverage for at all (only `/api/sagas` was tested).

**Pagination.** The saga list previously hardcoded `page: 1, pageSize: 50` with no way to reach
anything past the first 50 sagas. `saga-list.ts` now tracks `page` and a selectable `pageSize`
(25/50/75/100, default 25) as signals, with Previous/Next controls disabled appropriately
(`page() * pageSize < totalCount()`). Any filter or page-size change resets back to page 1. A live
SignalR update for a saga not already in the loaded page only prepends into view while on page 1;
elsewhere it just bumps `totalCount` and surfaces a "N new sagas — Refresh" banner, rather than
silently showing the wrong rows for the page the user is looking at.

**Sortable Status/Updated columns.** Clicking either column header sorts — first click ascending,
second click reverses, switching columns resets to ascending. This was initially implemented as a
client-side sort over whichever page happened to already be loaded, which turned out to be a bug:
clicking a header only ever reordered the current page, so page 2+ kept showing rows in their
original server order. Fixed by pushing the sort down to the backend: `SagaListFilter` gained
`SortBy`/`SortDescending`, both `EfCoreSagaSummaryReader` and `InMemorySagaStore` apply the ordering
before `Skip`/`Take` (ties broken by `UpdatedAtUtc` descending, so paging through a sort stays
stable), and `GET /api/sagas` accepts `sortBy`/`sortDescending` query params. Status sorts by domain
progression (Running → Completed → Failed → Compensating → Compensated → TimedOut → Cancelled), not
alphabetically — the enum's declared order already matches, so `ORDER BY` on the stored int column
does the right thing for free. Changing the sort resets to page 1, like a filter change. The one
piece that legitimately stays client-side: keeping a live-pushed SignalR update inserted/repositioned
correctly within the already-loaded page between refetches, instead of always prepending regardless
of the active sort. Regression-covered by endpoint tests that split a sorted result set across two
pages to prove the ordering isn't just being reapplied to whatever page was requested, plus a
Testcontainers-backed Postgres test verifying the EF Core translation.
