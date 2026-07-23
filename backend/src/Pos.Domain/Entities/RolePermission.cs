namespace Pos.Domain.Entities;

public class RolePermission
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Role Role { get; set; }

    public Permission Permission { get; set; }
}
