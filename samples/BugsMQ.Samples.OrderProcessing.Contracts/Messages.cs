namespace BugsMQ.Samples.OrderProcessing.Contracts;

// Initiating event — published by whatever front-door accepts an order (a minimal console "Order API"
// in this sample). Carries no correlation id: the saga engine mints one and it becomes canonical for
// every message that follows.
public sealed record OrderSubmitted(string OrderId, string CustomerId, decimal Amount);

public sealed record ReserveInventory(Guid CorrelationId, string OrderId);
public sealed record InventoryReserved(Guid CorrelationId, string OrderId);
public sealed record InventoryReservationFailed(Guid CorrelationId, string OrderId, string Reason);
public sealed record ReleaseInventory(Guid CorrelationId, string OrderId);

public sealed record ChargePayment(Guid CorrelationId, string OrderId, decimal Amount);
public sealed record PaymentCharged(Guid CorrelationId, string OrderId);
public sealed record PaymentFailed(Guid CorrelationId, string OrderId, string Reason);
public sealed record RefundPayment(Guid CorrelationId, string OrderId);

public sealed record ShipOrder(Guid CorrelationId, string OrderId);
public sealed record OrderShipped(Guid CorrelationId, string OrderId, string TrackingNumber);
public sealed record ShipmentFailed(Guid CorrelationId, string OrderId, string Reason);
