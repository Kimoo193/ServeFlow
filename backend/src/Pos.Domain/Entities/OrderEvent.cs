namespace Pos.Domain.Entities;

public class OrderEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid? OrderLineId { get; set; }
    public OrderEventType EventType { get; set; }
    public string Details { get; set; } = "{}";
    public string? Reason { get; set; }
    public Guid PerformedBy { get; set; }
    public DateTime PerformedAt { get; set; }
}
