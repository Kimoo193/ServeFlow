using Pos.Domain.Combos;

namespace Pos.Domain.Tests.Combos;

/// <summary>
/// The one invariant that matters: the allocated unit prices sum to the bundle price exactly.
/// A cent lost here is a cent wrong on a real bill, so every case asserts the total.
/// </summary>
public class ComboAllocationTests
{
    [Fact]
    public void Splits_evenly_when_components_share_a_list_price()
    {
        var allocated = ComboAllocation.Allocate(10.00m, [5.00m, 5.00m]);

        Assert.Equal([5.00m, 5.00m], allocated);
    }

    [Fact]
    public void Weights_the_allocation_by_list_price()
    {
        // Burger 8.00 + Fries 2.00 = 10.00 of list value, discounted to 5.00.
        var allocated = ComboAllocation.Allocate(5.00m, [8.00m, 2.00m]);

        Assert.Equal([4.00m, 1.00m], allocated);
        Assert.Equal(5.00m, allocated.Sum());
    }

    [Fact]
    public void Totals_exactly_when_the_price_does_not_divide_evenly()
    {
        // The case that forced per-unit expansion: 10.00 over three equal units. No 2-decimal
        // unit price works on a single quantity-3 line — 3.33x3 is 9.99, 3.34x3 is 10.02.
        var allocated = ComboAllocation.Allocate(10.00m, [4.00m, 4.00m, 4.00m]);

        Assert.Equal(10.00m, allocated.Sum());
        Assert.Equal([3.34m, 3.33m, 3.33m], allocated);
    }

    [Fact]
    public void Totals_exactly_for_awkward_prices_and_a_quantity_three_component()
    {
        // 9.99 across a 7.49 main and three 2.50 sides (one component of quantity 3, already
        // expanded to one entry per unit by the caller).
        var allocated = ComboAllocation.Allocate(9.99m, [7.49m, 2.50m, 2.50m, 2.50m]);

        Assert.Equal(9.99m, allocated.Sum());
        Assert.Equal(4, allocated.Count);
    }

    [Theory]
    [InlineData(10.00, 3)]
    [InlineData(9.99, 7)]
    [InlineData(0.01, 3)]
    [InlineData(33.33, 6)]
    [InlineData(19.95, 11)]
    public void Always_totals_the_bundle_price_across_equal_units(double bundle, int units)
    {
        var price = (decimal)bundle;
        var listPrices = Enumerable.Repeat(3.50m, units).ToArray();

        var allocated = ComboAllocation.Allocate(price, listPrices);

        Assert.Equal(price, allocated.Sum());
        Assert.Equal(units, allocated.Count);
    }

    [Fact]
    public void Never_allocates_a_fraction_of_a_cent()
    {
        var allocated = ComboAllocation.Allocate(10.00m, [4.00m, 4.00m, 4.00m]);

        Assert.All(allocated, price => Assert.Equal(price, decimal.Round(price, 2)));
    }

    [Fact]
    public void Gives_the_whole_price_to_a_single_unit()
    {
        var allocated = ComboAllocation.Allocate(7.77m, [12.00m]);

        Assert.Equal([7.77m], allocated);
    }

    [Fact]
    public void Splits_evenly_when_every_component_is_free()
    {
        // Weighting by list price is meaningless here; without a fallback this divides by zero.
        var allocated = ComboAllocation.Allocate(10.00m, [0m, 0m, 0m]);

        Assert.Equal(10.00m, allocated.Sum());
        Assert.Equal([3.34m, 3.33m, 3.33m], allocated);
    }

    [Fact]
    public void Allocates_nothing_to_a_free_component_alongside_priced_ones()
    {
        var allocated = ComboAllocation.Allocate(10.00m, [10.00m, 0m]);

        Assert.Equal([10.00m, 0.00m], allocated);
    }

    [Fact]
    public void Allocates_zero_to_every_unit_for_a_free_combo()
    {
        var allocated = ComboAllocation.Allocate(0m, [5.00m, 3.00m]);

        Assert.Equal(0m, allocated.Sum());
        Assert.All(allocated, price => Assert.Equal(0m, price));
    }

    [Fact]
    public void Is_deterministic_for_the_same_input()
    {
        var first = ComboAllocation.Allocate(10.00m, [4.00m, 4.00m, 4.00m]);
        var second = ComboAllocation.Allocate(10.00m, [4.00m, 4.00m, 4.00m]);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Rejects_a_combo_that_expands_to_no_units()
    {
        Assert.Throws<ArgumentException>(() => ComboAllocation.Allocate(10.00m, []));
    }

    [Fact]
    public void Rejects_a_negative_bundle_price()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ComboAllocation.Allocate(-1.00m, [5.00m]));
    }

    [Fact]
    public void Rejects_a_negative_component_price()
    {
        Assert.Throws<ArgumentException>(() => ComboAllocation.Allocate(10.00m, [5.00m, -1.00m]));
    }
}
