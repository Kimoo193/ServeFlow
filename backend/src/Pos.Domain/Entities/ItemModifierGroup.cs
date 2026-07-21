namespace Pos.Domain.Entities;

public class ItemModifierGroup
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid ItemId { get; set; }
    public Guid GroupId { get; set; }
}
