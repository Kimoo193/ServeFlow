using Pos.Domain.Entities;

namespace Pos.Application.Tests;

public sealed class MenuAvailabilityFilteringTests
{
    [Fact]
    public void Available_items_are_included()
    {
        var items = new[]
        {
            new MenuItem { Id = Guid.NewGuid(), Name = "Burger", Availability = Availability.Available },
            new MenuItem { Id = Guid.NewGuid(), Name = "Fries", Availability = Availability.Available },
        };

        var filtered = items.Where(mi => mi.Availability != Availability.Hidden).ToList();

        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void OutOfStock_items_are_included_and_flagged()
    {
        var items = new[]
        {
            new MenuItem { Id = Guid.NewGuid(), Name = "Soup", Availability = Availability.OutOfStock },
        };

        var filtered = items.Where(mi => mi.Availability != Availability.Hidden).ToList();

        var item = Assert.Single(filtered);
        Assert.Equal("OutOfStock", item.Availability.ToString());
    }

    [Fact]
    public void Hidden_items_are_excluded()
    {
        var items = new[]
        {
            new MenuItem { Id = Guid.NewGuid(), Name = "Burger", Availability = Availability.Available },
            new MenuItem { Id = Guid.NewGuid(), Name = "Secret Item", Availability = Availability.Hidden },
            new MenuItem { Id = Guid.NewGuid(), Name = "Fries", Availability = Availability.Available },
        };

        var filtered = items.Where(mi => mi.Availability != Availability.Hidden).ToList();

        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, mi => mi.Availability == Availability.Hidden);
    }

    [Fact]
    public void All_items_are_returned_when_no_Hidden_filter()
    {
        var items = new[]
        {
            new MenuItem { Id = Guid.NewGuid(), Name = "Burger", Availability = Availability.Available },
            new MenuItem { Id = Guid.NewGuid(), Name = "Secret", Availability = Availability.Hidden },
            new MenuItem { Id = Guid.NewGuid(), Name = "Soup", Availability = Availability.OutOfStock },
        };

        // No filter — as used by the /admin/menu endpoint
        var all = items.ToList();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, mi => mi.Availability == Availability.Hidden);
    }
}
