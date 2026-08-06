namespace ToolUseFunctions.Services;

/// <summary>
/// Stand-in for a real backend (Azure SQL / Cosmos DB / an internal API).
/// This is the "Tool" in Tool Use — the model never sees this code, it only
/// ever sees the JSON result of calling it.
/// </summary>
public class OrderLookupService
{
    private static readonly Dictionary<string, (string Status, string Eta)> Orders = new()
    {
        ["1001"] = ("Shipped", "2026-08-07"),
        ["1042"] = ("Processing", "2026-08-09"),
        ["2077"] = ("Delivered", "2026-08-01"),
    };

    public Task<OrderStatusResult> GetOrderStatusAsync(string orderId)
    {
        if (Orders.TryGetValue(orderId, out var order))
        {
            return Task.FromResult(new OrderStatusResult(orderId, order.Status, order.Eta, Found: true));
        }

        return Task.FromResult(new OrderStatusResult(orderId, "Unknown", null, Found: false));
    }
}

public record OrderStatusResult(string OrderId, string Status, string? EstimatedDelivery, bool Found);
