using GoalFlow.Device.Contracts;

namespace GoalFlow.Device.Harnesses.Adapters;

/// <summary>Tizen inventory adapter stub for the Family Hub integration.</summary>
public sealed class TizenInventoryApi : IInventoryApi
{
    public TizenInventoryApi()
    {
    }

    public Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken = default)
    {
        // TODO: query Samsung Family Hub inventory via SmartThings / Tizen device API.
        throw new NotImplementedException("Tizen inventory integration is not implemented yet.");
    }
}

/// <summary>Tizen calendar adapter stub for the Family Hub integration.</summary>
public sealed class TizenCalendarApi : ICalendarApi
{
    public TizenCalendarApi()
    {
    }

    public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        // TODO: query the signed-in family calendar through Tizen account/calendar APIs or a paired cloud service.
        throw new NotImplementedException("Tizen calendar integration is not implemented yet.");
    }
}

/// <summary>Tizen recipe adapter stub for the Family Hub integration.</summary>
public sealed class TizenRecipeApi : IRecipeApi
{
    public TizenRecipeApi()
    {
    }

    public Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default)
    {
        // TODO: load recipes from the Family Hub recipe catalog or the GoalFlow synced recipe store.
        throw new NotImplementedException("Tizen recipe integration is not implemented yet.");
    }
}

/// <summary>Tizen shopping-list adapter stub for the Family Hub integration.</summary>
public sealed class TizenShoppingListApi : IShoppingListApi
{
    public TizenShoppingListApi()
    {
    }

    public Task<IReadOnlyList<ShoppingListEntry>> GetListAsync(CancellationToken cancellationToken = default)
    {
        // TODO: read the Family Hub shopping list through SmartThings / Tizen device API.
        throw new NotImplementedException("Tizen shopping-list read integration is not implemented yet.");
    }

    public Task AddItemsAsync(
        IReadOnlyList<string> items,
        string? reason,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        // TODO: write approved shopping-list effects to the Family Hub shopping list API.
        throw new NotImplementedException("Tizen shopping-list write integration is not implemented yet.");
    }
}

/// <summary>Tizen reminder adapter stub for the Family Hub integration.</summary>
public sealed class TizenReminderApi : IReminderApi
{
    public TizenReminderApi()
    {
    }

    public Task<IReadOnlyList<Reminder>> GetRemindersAsync(CancellationToken cancellationToken = default)
    {
        // TODO: read reminders from Tizen Calendar/Alarm/Notification APIs or the GoalFlow synced reminder store.
        throw new NotImplementedException("Tizen reminder read integration is not implemented yet.");
    }

    public Task CreateReminderAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        // TODO: create approved prep reminders through Tizen Calendar/Alarm/Notification APIs.
        throw new NotImplementedException("Tizen reminder write integration is not implemented yet.");
    }
}
