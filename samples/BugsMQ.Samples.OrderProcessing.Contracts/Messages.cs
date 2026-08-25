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

// --- Post-shipment fulfilment (choreographed) --------------------------------------------------
// Note the shape difference from everything above: there is no ...Please-do-X command here, only
// events. Once the order ships, the notification, loyalty, and invoicing services each react to
// OrderShipped on their own initiative and announce what they did. Nothing tells them to, and
// nothing waits on them in a particular order — which is what makes this leg a choreography rather
// than another orchestrated hop. PostShipmentChoreography only observes and records the result.
public sealed record CustomerNotified(Guid CorrelationId, string OrderId, string Channel);
public sealed record LoyaltyPointsAwarded(Guid CorrelationId, string OrderId, int Points);
public sealed record InvoiceIssued(Guid CorrelationId, string OrderId, string InvoiceNumber);

// --- Invoice delivery (a sub-saga) --------------------------------------------------------------
// DeliverInvoice carries no correlation id, for the same reason OrderSubmitted doesn't: it opens a
// saga rather than continuing one, so the engine mints the id. Here the minting is done by
// PostShipmentChoreography's StartChildAsync, which also stamps the parent link onto the envelope —
// so this message starts a genuinely separate instance under its own id, not another observer of the
// order's. Everything below it stays inside that child's correlation id.
public sealed record DeliverInvoice(string OrderId, string InvoiceNumber);
public sealed record SendInvoiceEmail(Guid CorrelationId, string OrderId, string InvoiceNumber);
public sealed record InvoiceEmailSent(Guid CorrelationId, string OrderId);
public sealed record InvoiceEmailBounced(Guid CorrelationId, string OrderId, string Reason);
