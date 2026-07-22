namespace Pos.Domain.Entities;

public enum OrderEventType
{
    Created,
    Fired,
    Modified,
    Voided,
    Refired,
    QuantityChanged,
    Completed,
    StartedPreparing,
    MarkedReady,
    Served,
}
