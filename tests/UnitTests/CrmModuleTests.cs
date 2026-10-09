using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Crm;

namespace Erp.UnitTests;

public sealed class CrmModuleTests
{
    [Fact]
    public void ManifestDeclaresContactsDependencyAndPermissions()
    { var module = new CrmModule(); var registry = new PermissionRegistry(); module.ConfigurePermissions(registry); Assert.Equal("erp.crm", module.Manifest.Id); var dependency = Assert.Single(module.Manifest.Dependencies); Assert.Equal("erp.contacts", dependency.ModuleId); Assert.Equal(">=0.1.0 <1.0.0", dependency.VersionRange); Assert.Equal(5, registry.Permissions.Count); }
    [Fact]
    public void ValidationNormalizesLead()
    { var lead = LeadService.Validate(new(" New deal ", null, " SALES@EXAMPLE.TEST ", null, null, 10.1234m, 50, null, null, null), false); Assert.Equal("New deal", lead.Title); Assert.Equal("sales@example.test", lead.Email); }
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ValidationRejectsProbabilityOutsideRange(int probability)
    { var ex = Assert.Throws<LeadValidationException>(() => LeadService.Validate(new("Deal", null, null, null, null, 0, probability, null, null, null), false)); Assert.Contains("probability", ex.Errors); }
    [Fact]
    public void StageCannotBeBothWonAndLost() => Assert.Throws<LeadValidationException>(() =>
        OpportunityService.ValidateStage(new("Terminal", 10, 100, true, true), false));
    [Fact]
    public void ActivityRequiresExactlyOneParent()
    {
        Assert.Throws<LeadValidationException>(() => ActivityService.ValidateParent(null, null));
        Assert.Throws<LeadValidationException>(() => ActivityService.ValidateParent(Guid.NewGuid(), Guid.NewGuid()));
        ActivityService.ValidateParent(Guid.NewGuid(), null);
    }
}
