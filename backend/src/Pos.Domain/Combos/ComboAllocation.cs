namespace Pos.Domain.Combos;

/// <summary>
/// Splits a combo's single bundle price across its component units (BR-O-06, BR-B-01).
/// </summary>
/// <remarks>
/// A combo is expanded one <c>OrderLine</c> per component <em>unit</em>, each with quantity 1,
/// because <c>order_lines.unit_price</c> is <c>numeric(10,2)</c>: a single line of quantity 3
/// cannot total 10.00, since 3.33×3 is 9.99 and 3.34×3 is 10.02. One line per unit lets the
/// leftover cents be handed out individually, so the sum is exact.
/// </remarks>
public static class ComboAllocation
{
    /// <summary>
    /// Allocates <paramref name="bundlePrice"/> across one entry per component unit, weighted by
    /// each unit's list price.
    /// </summary>
    /// <returns>
    /// One price per unit, in the same order as <paramref name="unitListPrices"/>, each rounded
    /// to whole cents and together summing to exactly <paramref name="bundlePrice"/>.
    /// </returns>
    public static IReadOnlyList<decimal> Allocate(decimal bundlePrice, IReadOnlyList<decimal> unitListPrices)
    {
        ArgumentNullException.ThrowIfNull(unitListPrices);

        if (unitListPrices.Count == 0)
            throw new ArgumentException("A combo must expand to at least one unit.", nameof(unitListPrices));

        if (bundlePrice < 0)
            throw new ArgumentOutOfRangeException(nameof(bundlePrice), "A combo price cannot be negative.");

        for (var i = 0; i < unitListPrices.Count; i++)
        {
            if (unitListPrices[i] < 0)
                throw new ArgumentException("A component price cannot be negative.", nameof(unitListPrices));
        }

        var count = unitListPrices.Count;
        var targetCents = (long)decimal.Round(bundlePrice * 100m, 0, MidpointRounding.AwayFromZero);

        var listTotal = 0m;
        for (var i = 0; i < count; i++) listTotal += unitListPrices[i];

        // Weight by list price so a steak carries more of the discount than a side. When every
        // component is free the weights are meaningless, so fall back to an even split rather
        // than dividing by zero.
        var exactCents = new decimal[count];
        for (var i = 0; i < count; i++)
        {
            exactCents[i] = listTotal > 0m
                ? targetCents * unitListPrices[i] / listTotal
                : (decimal)targetCents / count;
        }

        var cents = new long[count];
        var assigned = 0L;
        for (var i = 0; i < count; i++)
        {
            cents[i] = (long)decimal.Floor(exactCents[i]);
            assigned += cents[i];
        }

        // Flooring always undershoots. Give the shortfall away one cent at a time, largest
        // fractional part first, so the unit that lost the most gets compensated first. Ties go
        // to the earlier unit purely so the result is reproducible.
        var byFractionDescending = Enumerable.Range(0, count)
            .OrderByDescending(i => exactCents[i] - decimal.Floor(exactCents[i]))
            .ThenBy(i => i)
            .ToArray();

        for (var k = 0; k < targetCents - assigned; k++)
        {
            cents[byFractionDescending[k % count]]++;
        }

        var allocated = new decimal[count];
        for (var i = 0; i < count; i++) allocated[i] = cents[i] / 100m;
        return allocated;
    }
}
