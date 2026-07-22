namespace Pos.Domain.Entities;

public class BranchSettings
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid BranchId { get; set; }

    public required string Currency { get; set; }

    public decimal TaxRate { get; set; }

    public bool TaxApplies { get; set; }

    public decimal ServiceChargeRate { get; set; }

    public bool ServiceApplies { get; set; }

    public bool TaxOnService { get; set; }

    public string? PrinterIp { get; set; }

    public int? PrinterPort { get; set; }
}
