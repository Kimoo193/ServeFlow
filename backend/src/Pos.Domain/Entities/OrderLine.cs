namespace Pos.Domain.Entities;

public class OrderLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ItemId { get; set; }
    public Guid StationId { get; set; }
    public Guid? FireBatchId { get; set; }
    public int Sequence { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public OrderLineStatus Status { get; set; }

    /// <summary>Which combo this line was expanded from, if any. Null for an ordinary line.</summary>
    /// <remarks>
    /// Kept so a receipt can say why a burger is priced 3.34 rather than 8.00 — the line carries
    /// an allocated share of a bundle price, not its own list price.
    /// </remarks>
    public Guid? ComboId { get; set; }

    /// <summary>
    /// Which *instance* of that combo, so two of the same combo on one order stay distinct.
    /// Client-supplied, and the idempotency key for combo expansion.
    /// </summary>
    public Guid? ComboInstanceId { get; set; }

    /// <summary>
    /// The combo's name as it was at order time.
    /// </summary>
    /// <remarks>
    /// Snapshotted rather than joined from <c>Combo</c>, for the same reason modifier names are
    /// (golden rule 6): renaming or deleting a combo must not rewrite a receipt that has already
    /// been printed. <see cref="ComboId"/> is kept alongside it for reporting, deliberately
    /// without a foreign key so deleting a retired combo stays possible.
    /// </remarks>
    public string? ComboName { get; set; }
}
