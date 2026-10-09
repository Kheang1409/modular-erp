using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Products;

namespace Erp.UnitTests;

public sealed class ProductsModuleTests
{
    [Fact]
    public void ManifestAndPermissionsDescribeProductsBoundary()
    {
        var module = new ProductsModule(); var permissions = new PermissionRegistry();
        module.ConfigurePermissions(permissions);
        Assert.Equal("erp.products", module.Manifest.Id);
        Assert.Equal(5, permissions.Permissions.Count);
        Assert.All(permissions.Permissions, permission => Assert.Equal("erp.products", permission.ModuleId));
    }

    [Fact]
    public void ValidationNormalizesCodesAndSku()
    {
        var value = ProductService.Validate(new(ProductType.Product, " sku-1 ", " Widget ", null, null,
            12.3456m, 4m, " usd ", " ea ", " ea ", true));
        Assert.Equal("SKU-1", value.Sku); Assert.Equal("USD", value.CurrencyCode); Assert.Equal("EA", value.SalesUnitCode);
    }

    [Fact]
    public void ValidationRejectsInventoryTrackedServices()
    {
        var exception = Assert.Throws<ProductValidationException>(() => ProductService.Validate(
            new(ProductType.Service, "CONSULT", "Consulting", null, null, 100, 0, "USD", "HUR", "HUR", true)));
        Assert.Contains("tracksInventory", exception.Errors.Keys);
    }

    [Fact]
    public void ValidationRejectsExcessPricePrecision()
    {
        var exception = Assert.Throws<ProductValidationException>(() => ProductService.Validate(
            new(ProductType.Product, "SKU", "Widget", null, null, 1.12345m, 0, "USD", "EA", "EA", true)));
        Assert.Contains("prices", exception.Errors.Keys);
    }

    [Theory]
    [InlineData("=2+3", "\"'=2+3\"")]
    [InlineData("Widget", "\"Widget\"")]
    public void CsvExportPreventsFormulaExecution(string value, string expected) =>
        Assert.Equal(expected, ProductService.Csv(value));

    [Fact]
    public void CsvParserSupportsEscaping() =>
        Assert.Equal(["Product", "Widget, large", "A \"great\" item"],
            ProductService.ParseCsvLine("Product,\"Widget, large\",\"A \"\"great\"\" item\""));
}
