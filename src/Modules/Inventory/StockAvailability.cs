using Erp.Modules.Inventory.Contracts;
namespace Erp.Modules.Inventory;

internal sealed class StockAvailability(IInventoryService service) : IStockAvailability { public async Task<StockBalanceReference> GetAsync(Guid productId, Guid locationId, CancellationToken t) { var x = await service.BalanceAsync(productId, locationId, t); return new(x.ProductId, x.LocationId, x.OnHand, x.Reserved, x.Available); } }
