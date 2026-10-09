using CommunityHello;
using Erp.Infrastructure;
using Erp.ModuleSystem;
using Erp.Modules.Sample;
using Erp.Modules.Contacts;
using Erp.Modules.Contacts.Contracts;
using Erp.Modules.Products;
using Erp.Modules.Products.Contracts;
using Erp.Modules.Crm;
using Erp.Modules.Crm.Contracts;
using Erp.Modules.Sales;
using Erp.Modules.Sales.Contracts;
using Erp.Modules.Purchasing;
using Erp.Modules.Purchasing.Contracts;
using Erp.Modules.Inventory;
using Erp.Modules.Inventory.Contracts;
using Erp.Modules.Accounting;
using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Reporting;

namespace Erp.ArchitectureTests;

public sealed class DependencyBoundaryTests
{
    [Fact]
    public void SampleModuleDependsOnlyOnPublicPlatformContracts()
    {
        var references = typeof(SampleModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();

        Assert.Contains("Erp.Abstractions", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
        Assert.DoesNotContain("Erp.ModuleSystem", references);
        Assert.DoesNotContain("Erp.Core", references);
    }

    [Fact]
    public void InstallableCommunitySampleDependsOnlyOnPublicPlatformContracts()
    {
        var references = typeof(CommunityHelloModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();

        Assert.Contains("Erp.Abstractions", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
        Assert.DoesNotContain("Erp.ModuleSystem", references);
        Assert.DoesNotContain("Erp.Core", references);
        Assert.DoesNotContain("Erp.Modules.Sample", references);
    }

    [Fact]
    public void InfrastructureDoesNotDependOnModuleSystemOrSample()
    {
        var references = typeof(DependencyInjection).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();

        Assert.DoesNotContain(typeof(ModuleRegistry).Assembly.GetName().Name, references);
        Assert.DoesNotContain(typeof(SampleModule).Assembly.GetName().Name, references);
    }

    [Fact]
    public void ContactsModuleDependsOnlyOnPublicPlatformContracts()
    {
        var references = typeof(ContactsModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.Contains("Erp.Abstractions", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
        Assert.DoesNotContain("Erp.ModuleSystem", references);
        Assert.DoesNotContain("Erp.Core", references);
        Assert.DoesNotContain("Erp.Modules.Sample", references);
        Assert.Contains("Erp.Modules.Contacts.Contracts", references);
    }

    [Fact]
    public void ContactsContractsDoNotExposePersistenceDependencies()
    {
        var references = typeof(IContactDirectory).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.DoesNotContain("Erp.Abstractions", references);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", references);
        Assert.DoesNotContain("Erp.Modules.Contacts", references);
    }

    [Fact]
    public void ProductsModuleDependsOnlyOnPublicPlatformContracts()
    {
        var references = typeof(ProductsModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.Contains("Erp.Abstractions", references);
        Assert.Contains("Erp.Modules.Products.Contracts", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
        Assert.DoesNotContain("Erp.ModuleSystem", references);
        Assert.DoesNotContain("Erp.Core", references);
        Assert.DoesNotContain("Erp.Modules.Contacts", references);
    }

    [Fact]
    public void ProductsContractsExposeNoPersistenceDependencies()
    {
        var references = typeof(IProductDirectory).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.DoesNotContain("Erp.Abstractions", references);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", references);
        Assert.DoesNotContain("Erp.Modules.Products", references);
    }

    [Fact]
    public void CrmDependsOnContactsContractButNotContactsPersistence()
    {
        var references = typeof(CrmModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.Contains("Erp.Abstractions", references);
        Assert.Contains("Erp.Modules.Contacts.Contracts", references);
        Assert.Contains("Erp.Modules.Crm.Contracts", references);
        Assert.DoesNotContain("Erp.Modules.Contacts", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
        Assert.DoesNotContain("Erp.Modules.Products", references);
    }

    [Fact]
    public void CrmContractsExposeNoPersistenceDependencies()
    {
        var references = typeof(ICrmLeadDirectory).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", references);
        Assert.DoesNotContain("Erp.Modules.Crm", references);
    }

    [Fact]
    public void SalesDependsOnlyOnPublicContactsAndProductsContracts()
    {
        var references = typeof(SalesModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.Contains("Erp.Abstractions", references);
        Assert.Contains("Erp.Modules.Contacts.Contracts", references);
        Assert.Contains("Erp.Modules.Products.Contracts", references);
        Assert.DoesNotContain("Erp.Modules.Contacts", references);
        Assert.DoesNotContain("Erp.Modules.Products", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
    }

    [Fact]
    public void SalesContractsExposeNoPersistenceDependencies()
    {
        var references = typeof(IQuotationDirectory).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", references);
        Assert.DoesNotContain("Erp.Modules.Sales", references);
    }
    [Fact] public void PurchasingUsesOnlyPublicModuleContracts() { var r = typeof(PurchasingModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(); Assert.Contains("Erp.Modules.Contacts.Contracts", r); Assert.Contains("Erp.Modules.Products.Contracts", r); Assert.DoesNotContain("Erp.Modules.Contacts", r); Assert.DoesNotContain("Erp.Modules.Products", r); Assert.DoesNotContain("Erp.Infrastructure", r); }
    [Fact] public void PurchasingContractsExposeNoPersistence() { var r = typeof(IPurchaseOrderDirectory).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(); Assert.DoesNotContain("Microsoft.EntityFrameworkCore", r); Assert.DoesNotContain("Erp.Modules.Purchasing", r); }
    [Fact] public void InventoryUsesOnlyPublicModuleContracts() { var r = typeof(InventoryModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(); Assert.Contains("Erp.Modules.Products.Contracts", r); Assert.Contains("Erp.Modules.Sales.Contracts", r); Assert.Contains("Erp.Modules.Purchasing.Contracts", r); Assert.DoesNotContain("Erp.Modules.Products", r); Assert.DoesNotContain("Erp.Modules.Sales", r); Assert.DoesNotContain("Erp.Modules.Purchasing", r); Assert.DoesNotContain("Erp.Infrastructure", r); }
    [Fact] public void InventoryContractsExposeNoPersistence() { var r = typeof(IStockAvailability).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet(); Assert.DoesNotContain("Microsoft.EntityFrameworkCore", r); Assert.DoesNotContain("Erp.Modules.Inventory", r); }

    [Fact]
    public void AccountingUsesOnlyPublicSalesAndPurchasingContracts()
    {
        var references = typeof(AccountingModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.Contains("Erp.Abstractions", references);
        Assert.Contains("Erp.Modules.Accounting.Contracts", references);
        Assert.Contains("Erp.Modules.Sales.Contracts", references);
        Assert.Contains("Erp.Modules.Purchasing.Contracts", references);
        Assert.DoesNotContain("Erp.Modules.Sales", references);
        Assert.DoesNotContain("Erp.Modules.Purchasing", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
    }

    [Fact]
    public void AccountingContractsExposeNoPersistence()
    {
        var references = typeof(IAccountingReportingData).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Npgsql.EntityFrameworkCore.PostgreSQL", references);
        Assert.DoesNotContain("Erp.Modules.Accounting", references);
    }

    [Fact]
    public void ReportingComposesOnlyPublicModuleContracts()
    {
        var references = typeof(ReportingModule).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToHashSet();
        Assert.Contains("Erp.Abstractions", references);
        Assert.Contains("Erp.Modules.Sales.Contracts", references);
        Assert.Contains("Erp.Modules.Purchasing.Contracts", references);
        Assert.Contains("Erp.Modules.Inventory.Contracts", references);
        Assert.Contains("Erp.Modules.Accounting.Contracts", references);
        Assert.DoesNotContain("Erp.Modules.Sales", references);
        Assert.DoesNotContain("Erp.Modules.Purchasing", references);
        Assert.DoesNotContain("Erp.Modules.Inventory", references);
        Assert.DoesNotContain("Erp.Modules.Accounting", references);
        Assert.DoesNotContain("Erp.Infrastructure", references);
    }
}
