using Erp.Abstractions.Context;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Events;
using Erp.Abstractions.Sequences;
using Erp.Infrastructure.Persistence;
using Erp.Modules.Inventory;
using Erp.Modules.Products.Contracts;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Sales.Contracts;
using Erp.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
namespace Erp.IntegrationTests;

public sealed class InventoryPostgresTests : IAsyncLifetime
{
    private readonly System.Collections.Concurrent.ConcurrentBag<PlatformDbContext> _platformContexts = [];
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        while (_platformContexts.TryTake(out var context)) await context.DisposeAsync();
    }
    [Fact] public async Task LedgerIsCompanyIsolatedAndCompletedMovesAreImmutable() { var pg = await IntegrationContainers.StartPostgresAsync("inventory_tests"); await using (pg) { var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var location = Guid.NewGuid(); var product = Guid.NewGuid(); var moveId = Guid.NewGuid(); var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(pg.GetConnectionString()).Options; await using (var migration = new InventoryDbContext(options)) await migration.Database.MigrateAsync(); await using (var write = new InventoryDbContext(options, new T(tenant), new C(company))) { write.Locations.Add(new() { Id = location, TenantId = tenant, CompanyId = company, Code = "MAIN", Name = "Main", Type = LocationType.Internal, IsActive = true, ConcurrencyStamp = Guid.NewGuid().ToString("N") }); write.StockMoves.Add(new() { Id = moveId, TenantId = tenant, CompanyId = company, Number = "MOVE-TEST-1", Type = StockMoveType.Adjustment, State = StockMoveState.Completed, ProductId = product, ProductSku = "SKU", UnitCode = "EA", Quantity = 10, DestinationLocationId = location, BusinessDate = DateOnly.FromDateTime(DateTime.Today), CreatedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow, ConcurrencyStamp = Guid.NewGuid().ToString("N") }); await write.SaveChangesAsync(); } await using var visible = new InventoryDbContext(options, new T(tenant), new C(company)); await using var hidden = new InventoryDbContext(options, new T(tenant), new C(Guid.NewGuid())); Assert.Single(await visible.StockMoves.ToListAsync()); Assert.Empty(await hidden.StockMoves.ToListAsync()); var completed = await visible.StockMoves.SingleAsync(); completed.Notes = "illegal edit"; await Assert.ThrowsAsync<InvalidOperationException>(() => visible.SaveChangesAsync()); } }
    [Fact]
    public async Task ConcurrentReservationsCannotExceedStockAndDeliveryConsumesOnlyItsOrderReservation()
    {
        var pg = await IntegrationContainers.StartPostgresAsync("inventory_reservation_tests");
        await using (pg)
        {
            var tenant = Guid.NewGuid(); var company = Guid.NewGuid(); var warehouse = Guid.NewGuid(); var location = Guid.NewGuid(); var supplier = Guid.NewGuid(); var customer = Guid.NewGuid(); var gain = Guid.NewGuid(); var loss = Guid.NewGuid(); var product = Guid.NewGuid(); var secondProduct = Guid.NewGuid(); var order = Guid.NewGuid(); var purchaseOrder = Guid.NewGuid();
            var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(pg.GetConnectionString()).Options;
            await using (var platformMigration = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseNpgsql(pg.GetConnectionString()).Options)) await platformMigration.Database.MigrateAsync();
            await using (var migration = new InventoryDbContext(options)) await migration.Database.MigrateAsync();
            await using (var seed = new InventoryDbContext(options, new T(tenant), new C(company)))
            {
                seed.Warehouses.Add(new() { Id = warehouse, TenantId = tenant, CompanyId = company, Code = "MAIN", Name = "Main", IsActive = true, ConcurrencyStamp = Stamp() });
                seed.Locations.Add(new() { Id = location, TenantId = tenant, CompanyId = company, WarehouseId = warehouse, Code = "PICK", Name = "Pick", Type = LocationType.Internal, IsActive = true, ConcurrencyStamp = Stamp() });
                seed.Locations.AddRange(
                    new() { Id = supplier, TenantId = tenant, CompanyId = company, Code = "SUPPLIER", Name = "Supplier", Type = LocationType.Supplier, IsActive = true, ConcurrencyStamp = Stamp() },
                    new() { Id = customer, TenantId = tenant, CompanyId = company, Code = "CUSTOMER", Name = "Customer", Type = LocationType.Customer, IsActive = true, ConcurrencyStamp = Stamp() },
                    new() { Id = gain, TenantId = tenant, CompanyId = company, Code = "GAIN", Name = "Inventory Gain", Type = LocationType.InventoryGain, IsActive = true, ConcurrencyStamp = Stamp() },
                    new() { Id = loss, TenantId = tenant, CompanyId = company, Code = "LOSS", Name = "Inventory Loss", Type = LocationType.InventoryLoss, IsActive = true, ConcurrencyStamp = Stamp() });
                seed.StockMoves.Add(new() { Id = Guid.NewGuid(), TenantId = tenant, CompanyId = company, Number = "OPENING", Type = StockMoveType.Adjustment, State = StockMoveState.Completed, ProductId = product, ProductSku = "SKU-1", UnitCode = "EA", Quantity = 10, DestinationLocationId = location, BusinessDate = new(2026, 9, 15), CreatedAtUtc = Clock.Now, CompletedAtUtc = Clock.Now, ConcurrencyStamp = Stamp() });
                await seed.SaveChangesAsync();
            }

            async Task<Exception?> Reserve(string key)
            {
                try { await using var context = new InventoryDbContext(options, new T(tenant), new C(company)); await Service(context, tenant, company, product, order, purchaseOrder).ReserveAsync(new(product, location, 7, key, "SalesOrder", order), CancellationToken.None); return null; }
                catch (Exception ex) { return ex; }
            }
            var attempts = await Task.WhenAll(Reserve("reserve-a"), Reserve("reserve-b"));
            Assert.Single(attempts, x => x is null);
            Assert.Single(attempts.OfType<InventoryValidationException>());

            await using var deliveryContext = new InventoryDbContext(options, new T(tenant), new C(company));
            var service = Service(deliveryContext, tenant, company, product, order, purchaseOrder);
            await Assert.ThrowsAsync<InventoryValidationException>(() => service.CreateAsync(new(StockMoveType.Receipt, product, 1, customer, location, new(2026, 9, 15)), CancellationToken.None));
            await Assert.ThrowsAsync<InventoryValidationException>(() => service.CreateAsync(new(StockMoveType.Delivery, product, 1, location, supplier, new(2026, 9, 15)), CancellationToken.None));
            var delivery = await service.CreateAsync(new(StockMoveType.Delivery, product, 4, location, customer, new(2026, 9, 15), "SalesOrder", order), CancellationToken.None);
            var completed = await service.CompleteAsync(delivery.Id, new(delivery.ConcurrencyStamp, "deliver-1"), CancellationToken.None);
            var repeated = await service.CompleteAsync(delivery.Id, new(completed.ConcurrencyStamp, "deliver-1"), CancellationToken.None);
            Assert.Equal(completed.Id, repeated.Id);
            var reservation = Assert.Single((await service.ReservationsAsync(new(ReferenceDocumentId: order), CancellationToken.None)).Items);
            Assert.Equal(4, reservation.ConsumedQuantity);
            Assert.Equal(3, reservation.RemainingQuantity);
            Assert.Equal(ReservationState.Active, reservation.State);
            var balance = await service.BalanceAsync(product, location, CancellationToken.None);
            Assert.Equal(6, balance.OnHand); Assert.Equal(3, balance.Reserved); Assert.Equal(3, balance.Available);
            var locationDto = Assert.Single(await service.LocationsAsync(CancellationToken.None), x => x.Id == location);
            await Assert.ThrowsAsync<InventoryValidationException>(() => service.SaveLocationAsync(location, new(warehouse, locationDto.Code, locationDto.Name, locationDto.Type, false, locationDto.ConcurrencyStamp), CancellationToken.None));
            var released = await service.ReleaseAsync(reservation.Id, new(reservation.ConcurrencyStamp, "release-remaining"), CancellationToken.None);
            var repeatedRelease = await service.ReleaseAsync(reservation.Id, new(released.ConcurrencyStamp, "release-remaining"), CancellationToken.None);
            Assert.Equal(ReservationState.Released, repeatedRelease.State);
            Assert.Equal(released.Id, repeatedRelease.Id);

            var idempotentAudit = new Audit(); var idempotentOutbox = new Outbox();
            async Task<ReservationDto> ReserveSameKey()
            {
                await using var context = new InventoryDbContext(options, new T(tenant), new C(company));
                return await Service(context, tenant, company, product, order, purchaseOrder, idempotentAudit, idempotentOutbox).ReserveAsync(new(product, location, 2, "reserve-same", "SalesOrder", order), CancellationToken.None);
            }
            var idempotentReservations = await Task.WhenAll(ReserveSameKey(), ReserveSameKey());
            Assert.Equal(idempotentReservations[0].Id, idempotentReservations[1].Id);
            Assert.Equal(1, await deliveryContext.Reservations.CountAsync(x => x.IdempotencyKey == "reserve-same"));
            Assert.Single(idempotentAudit.Entries, x => x.Action == "inventory.stock.reserved");
            Assert.Single(idempotentOutbox.Events, x => x is StockReservedEvent e && e.ReservationId == idempotentReservations[0].Id);
            await Assert.ThrowsAsync<InventoryValidationException>(() => service.ReserveAsync(new(product, location, 1, "reserve-same", "SalesOrder", order), CancellationToken.None));
            await service.ReleaseAsync(idempotentReservations[0].Id, new(idempotentReservations[0].ConcurrencyStamp, "release-same"), CancellationToken.None);

            var receiptA = await service.CreateAsync(new(StockMoveType.Receipt, product, 6, supplier, location, new(2026, 9, 15), "PurchaseOrder", purchaseOrder), CancellationToken.None);
            var receiptB = await service.CreateAsync(new(StockMoveType.Receipt, product, 6, supplier, location, new(2026, 9, 15), "PurchaseOrder", purchaseOrder), CancellationToken.None);
            async Task<Exception?> CompleteReceipt(StockMoveDto move, string key)
            {
                try { await using var context = new InventoryDbContext(options, new T(tenant), new C(company)); await Service(context, tenant, company, product, order, purchaseOrder).CompleteAsync(move.Id, new(move.ConcurrencyStamp, key), CancellationToken.None); return null; }
                catch (Exception ex) { return ex; }
            }
            var receipts = await Task.WhenAll(CompleteReceipt(receiptA, "receipt-a"), CompleteReceipt(receiptB, "receipt-b"));
            Assert.Single(receipts, x => x is null);
            Assert.Single(receipts.OfType<InventoryValidationException>());
            Assert.Equal(6, await deliveryContext.StockMoves.Where(x => x.ReferenceDocumentId == purchaseOrder && x.State == StockMoveState.Completed).SumAsync(x => x.Quantity));

            var outboundA = await service.CreateAsync(new(StockMoveType.Delivery, product, 7, location, customer, new(2026, 9, 15)), CancellationToken.None);
            var outboundB = await service.CreateAsync(new(StockMoveType.Delivery, product, 7, location, customer, new(2026, 9, 15)), CancellationToken.None);
            async Task<Exception?> CompleteOutbound(StockMoveDto move, string key)
            {
                try { await using var context = new InventoryDbContext(options, new T(tenant), new C(company)); await Service(context, tenant, company, product, order, purchaseOrder).CompleteAsync(move.Id, new(move.ConcurrencyStamp, key), CancellationToken.None); return null; }
                catch (Exception ex) { return ex; }
            }
            var outbound = await Task.WhenAll(CompleteOutbound(outboundA, "outbound-a"), CompleteOutbound(outboundB, "outbound-b"));
            Assert.Single(outbound, x => x is null);
            Assert.Single(outbound.OfType<InventoryValidationException>());

            var destination = Guid.NewGuid();
            await using var transferContext = new InventoryDbContext(options, new T(tenant), new C(company));
            transferContext.Locations.Add(new() { Id = destination, TenantId = tenant, CompanyId = company, WarehouseId = warehouse, Code = "SECOND", Name = "Second", Type = LocationType.Internal, IsActive = true, ConcurrencyStamp = Stamp() });
            await transferContext.SaveChangesAsync();
            var audit = new Audit(); var outbox = new Outbox();
            var transferService = Service(transferContext, tenant, company, product, order, purchaseOrder, audit, outbox);
            var transfer = await transferService.CreateAsync(new(StockMoveType.Transfer, product, 2, location, destination, new(2026, 9, 15)), CancellationToken.None);
            transfer = await transferService.CompleteAsync(transfer.Id, new(transfer.ConcurrencyStamp, "transfer-1"), CancellationToken.None);
            Assert.Equal(3, (await transferService.BalanceAsync(product, location, CancellationToken.None)).OnHand);
            Assert.Equal(2, (await transferService.BalanceAsync(product, destination, CancellationToken.None)).OnHand);

            async Task<StockMoveDto> Correct()
            {
                await using var context = new InventoryDbContext(options, new T(tenant), new C(company));
                return await Service(context, tenant, company, product, order, purchaseOrder, audit, outbox).CorrectAsync(transfer.Id, CancellationToken.None);
            }
            var corrections = await Task.WhenAll(Correct(), Correct());
            Assert.Equal(corrections[0].Id, corrections[1].Id);
            Assert.Equal(StockMoveType.Transfer, corrections[0].Type);
            Assert.Equal(destination, corrections[0].SourceLocationId);
            Assert.Equal(location, corrections[0].DestinationLocationId);
            Assert.Equal(1, await transferContext.StockMoves.CountAsync(x => x.ReversesMoveId == transfer.Id));
            var correction = await transferService.CompleteAsync(corrections[0].Id, new(corrections[0].ConcurrencyStamp, "correction-1"), CancellationToken.None);
            Assert.Equal(StockMoveState.Completed, correction.State);
            Assert.Equal(5, (await transferService.BalanceAsync(product, location, CancellationToken.None)).OnHand);
            Assert.Equal(0, (await transferService.BalanceAsync(product, destination, CancellationToken.None)).OnHand);
            Assert.Contains(audit.Entries, x => x.Action == "inventory.move.correction_created");
            Assert.Contains(outbox.Events, x => x is TransferCompletedEvent e && e.MoveId == correction.Id);
            var transfers = await transferService.SearchAsync(new(Search: "SKU-1", WarehouseId: warehouse, Type: StockMoveType.Transfer, State: StockMoveState.Completed, FromDate: new(2026, 9, 15), ToDate: new(2026, 9, 15), Sort: "number", Descending: false, PageSize: 1), CancellationToken.None);
            Assert.True(transfers.Total >= 2);
            Assert.Single(transfers.Items);
            Assert.All(transfers.Items, x => Assert.Equal(StockMoveType.Transfer, x.Type));
            var purchaseReceipts = await transferService.SearchAsync(new(State: StockMoveState.Completed, ReferenceDocumentType: "purchaseorder", ReferenceDocumentId: purchaseOrder), CancellationToken.None);
            Assert.Single(purchaseReceipts.Items);
            var gainAdjustment = await transferService.CreateAsync(new(StockMoveType.Adjustment, product, 3, gain, destination, new(2026, 9, 15)), CancellationToken.None);
            await transferService.CompleteAsync(gainAdjustment.Id, new(gainAdjustment.ConcurrencyStamp, "adjust-gain"), CancellationToken.None);
            var lossAdjustment = await transferService.CreateAsync(new(StockMoveType.Adjustment, product, 1, destination, loss, new(2026, 9, 15)), CancellationToken.None);
            await transferService.CompleteAsync(lossAdjustment.Id, new(lossAdjustment.ConcurrencyStamp, "adjust-loss"), CancellationToken.None);
            Assert.Equal(2, (await transferService.BalanceAsync(product, destination, CancellationToken.None)).OnHand);
            Assert.Contains(outbox.Events, x => x is StockAdjustedEvent e && e.MoveId == gainAdjustment.Id && e.Quantity == 3);
            Assert.Contains(outbox.Events, x => x is StockAdjustedEvent e && e.MoveId == lossAdjustment.Id && e.Quantity == -1);
            var balanceRows = await transferService.BalancesAsync(new(Search: "SKU-1", WarehouseId: warehouse, Sort: "location", PageSize: 10), CancellationToken.None);
            Assert.Equal(2, balanceRows.Total);
            Assert.All(balanceRows.Items, x => { Assert.Equal(7, x.WarehouseOnHand); Assert.Equal(7, x.WarehouseAvailable); });
            Assert.Contains(balanceRows.Items, x => x.LocationId == location && x.OnHand == 5);
            Assert.Contains(balanceRows.Items, x => x.LocationId == destination && x.OnHand == 2);
            var batchService = Service(transferContext, tenant, company, product, order, purchaseOrder, additionalProduct: secondProduct);
            var beforeInvalidBatch = await transferContext.StockMoves.CountAsync();
            await Assert.ThrowsAsync<InventoryValidationException>(() => batchService.CreateBatchAsync(new(StockMoveType.Adjustment, gain, destination, new(2026, 9, 15), [new(product, 1), new(secondProduct, 0)], Notes: "invalid batch"), CancellationToken.None));
            Assert.Equal(beforeInvalidBatch, await transferContext.StockMoves.CountAsync());
            var batch = await batchService.CreateBatchAsync(new(StockMoveType.Adjustment, gain, destination, new(2026, 9, 15), [new(product, 1), new(secondProduct, 2)], Notes: "cycle count batch"), CancellationToken.None);
            Assert.Equal(2, batch.Items.Count);
            Assert.All(batch.Items, x => Assert.Equal(StockMoveState.Draft, x.State));
            Assert.Equal(2, await transferContext.StockMoves.CountAsync(x => x.Notes == "cycle count batch"));
            var reporting = new InventoryReportingData(transferContext);
            var onHandReport = await reporting.OnHandAsync(new(2026, 9, 15), CancellationToken.None);
            Assert.Contains(onHandReport, row => row.ProductId == product && row.LocationId == location && row.OnHand == 5);
            Assert.Contains(onHandReport, row => row.ProductId == product && row.LocationId == destination && row.OnHand == 2);
            var movementReport = await reporting.MovementsAsync(new(2026, 9, 15), new(2026, 9, 15), 1, 2, CancellationToken.None);
            Assert.True(movementReport.Total >= 6);
            Assert.Equal(2, movementReport.Items.Count);
        }
    }

    private InventoryService Service(InventoryDbContext db, Guid tenant, Guid company, Guid product, Guid order, Guid purchaseOrder, Audit? audit = null, Outbox? outbox = null, Guid? additionalProduct = null)
    {
        var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(db.Database.GetDbConnection()).Options, new T(tenant));
        _platformContexts.Add(platform);
        return new(db, new T(tenant), new C(company), new Products(product, additionalProduct), new Sales(order, product),
            new Purchasing(purchaseOrder, product), new Sequences(), new Clock(), audit ?? new Audit(), outbox ?? new Outbox(),
            new HttpContextAccessor(), new BusinessTransactionCoordinator(platform));
    }
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private sealed class T(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class C(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
    private sealed class Clock : IClock { public static DateTimeOffset Now => new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero); public DateTimeOffset UtcNow => Now; }
    private sealed class Products(Guid id, Guid? additionalId = null) : IProductDirectory { public Task<ProductReference?> FindAsync(Guid productId, CancellationToken t) => Task.FromResult<ProductReference?>(productId == id ? new(id, ProductReferenceType.Product, "SKU-1", "Product", 1, "USD", "EA", true, false) : productId == additionalId ? new(productId, ProductReferenceType.Product, "SKU-2", "Second product", 1, "USD", "EA", true, false) : null); public Task<IReadOnlyDictionary<Guid, ProductReference>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken t) => throw new NotSupportedException(); }
    private sealed class Sales(Guid id, Guid product) : ISalesOrderDirectory { public Task<SalesOrderReference?> FindAsync(Guid orderId, CancellationToken t) => Task.FromResult<SalesOrderReference?>(orderId == id ? new(id, "SO-1", Guid.NewGuid(), "USD", 10, "Confirmed", [new(Guid.NewGuid(), product, "SKU-1", "EA", 10)]) : null); }
    private sealed class Purchasing(Guid id, Guid product) : IPurchaseOrderDirectory { public Task<PurchaseOrderReference?> FindAsync(Guid orderId, CancellationToken t) => Task.FromResult<PurchaseOrderReference?>(orderId == id ? new(id, "PO-1", Guid.NewGuid(), "USD", 10, "Confirmed", [new(Guid.NewGuid(), product, "SKU-1", "EA", 10)]) : null); }
    private sealed class Sequences : ISequenceService { public Task<string> NextAsync(SequenceRequest request, CancellationToken t) => Task.FromResult($"MOVE-{Guid.NewGuid():N}"); }
    private sealed class Audit : IAuditLogWriter { public System.Collections.Concurrent.ConcurrentBag<AuditWriteEntry> Entries { get; } = []; public void Append(AuditWriteEntry entry) => Entries.Add(entry); public Task FlushAsync(CancellationToken t) => Task.CompletedTask; }
    private sealed class Outbox : IOutboxWriter { public System.Collections.Concurrent.ConcurrentBag<IIntegrationEvent> Events { get; } = []; public void Add<TEvent>(TEvent integrationEvent) where TEvent : IIntegrationEvent => Events.Add(integrationEvent); }
}
