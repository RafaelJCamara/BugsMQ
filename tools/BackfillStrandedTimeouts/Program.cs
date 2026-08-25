using BugsMQ.Abstractions.Persistence;
using BugsMQ.Abstractions.Sagas;
using BugsMQ.Persistence.EFCore;
using Microsoft.EntityFrameworkCore;

// One-time operational fix for OrderSaga instances that entered AwaitingInventory/AwaitingShipment
// before those states carried a WithTimeout — see README.md "Timeout coverage for every awaiting
// state". Those instances have no SagaTimeouts row and never will, since SagaOrchestrator only
// schedules a timeout on a real transition *into* a state. This tool does not hand-roll the
// unwind itself: it schedules a due SagaTimeouts row for each stranded instance and leaves it to
// the already-running SagaTimeoutDispatcherHostedService to pick up on its normal poll and run
// through the exact same tested SagaOrchestrator.HandleTimeoutAsync path a real timeout takes.
const string SagaType = "OrderSaga";
var strandedStates = new[] { "AwaitingInventory", "AwaitingShipment" };

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__BugsMQ")
    // Host-mapped port (docker-compose.yml maps host 5433 -> container 5432), not the 5432 default
    // BugsMQ.Dashboard.Api falls back to when it runs *inside* the compose network under its own name.
    // This tool runs from the host against that same compose stack.
    ?? "Host=localhost;Port=5433;Database=bugsmq;Username=postgres;Password=postgres";

var options = new DbContextOptionsBuilder<BugsMqDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new BugsMqDbContext(options);

var pendingTimeoutKeys = await db.SagaTimeouts
    .Where(t => t.SagaType == SagaType && t.Status == SagaTimeoutStatus.Pending && strandedStates.Contains(t.ForState))
    .Select(t => new { t.CorrelationId, t.ForState })
    .ToListAsync();
var alreadyScheduled = pendingTimeoutKeys.Select(k => (k.CorrelationId, k.ForState)).ToHashSet();

var runningInStrandedStates = await db.SagaInstances
    .Where(s => s.SagaType == SagaType && s.Status == SagaStatus.Running && strandedStates.Contains(s.CurrentState))
    .ToListAsync();

var stranded = runningInStrandedStates
    .Where(s => !alreadyScheduled.Contains((s.CorrelationId, s.CurrentState)))
    .ToList();

Console.WriteLine($"{runningInStrandedStates.Count} {SagaType} instance(s) Running in {string.Join('/', strandedStates)}.");
foreach (var state in strandedStates)
{
    var strandedInState = stranded.Count(s => string.Equals(s.CurrentState, state, StringComparison.Ordinal));
    var coveredInState = runningInStrandedStates.Count(s => string.Equals(s.CurrentState, state, StringComparison.Ordinal)) - strandedInState;
    Console.WriteLine($"  {state}: {strandedInState} stranded (no pending timeout), {coveredInState} already covered by a normal pending timeout.");
}

if (stranded.Count == 0)
{
    Console.WriteLine("Nothing to backfill.");
    return;
}

var now = DateTimeOffset.UtcNow;
foreach (var instance in stranded)
{
    db.SagaTimeouts.Add(new SagaTimeoutEntity
    {
        CorrelationId = instance.CorrelationId,
        SagaType = SagaType,
        ForState = instance.CurrentState,
        DueAtUtc = now,
        Status = SagaTimeoutStatus.Pending,
    });
}

await db.SaveChangesAsync();

Console.WriteLine($"Scheduled {stranded.Count} backfill timeout(s), due now. The SagaTimeoutDispatcherHostedService will claim them on its next poll (every 5s) and run them through the normal timeout path.");
