using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Reporting;

namespace Erp.UnitTests;

public sealed class ReportingModuleTests
{
    [Fact]
    public void ManifestDeclaresSourceDependenciesAndReportPermissions()
    {
        var module = new ReportingModule(); var permissions = new PermissionRegistry(); module.ConfigurePermissions(permissions);
        Assert.Equal(["erp.sales", "erp.purchasing", "erp.inventory", "erp.accounting"], module.Manifest.Dependencies.Select(x => x.ModuleId));
        Assert.Equal(8, permissions.Permissions.Count); Assert.All(permissions.Permissions, x => Assert.Equal("erp.reporting", x.ModuleId));
    }

    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+SUM(A1:A2)", "\"'+SUM(A1:A2)\"")]
    [InlineData("normal, value", "\"normal, value\"")]
    [InlineData("quote\"value", "\"quote\"\"value\"")]
    public void CsvEscapesFormulasAndSpecialCharacters(string value, string expected) => Assert.Equal(expected, ReportingCsv.Quote(value));
}
