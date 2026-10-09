using Erp.Abstractions.Modules;
using Erp.ModuleSystem;
using Erp.Modules.Sample;
using System.Text;

namespace Erp.UnitTests;

public sealed class ModuleSystemTests
{
    [Fact]
    public void DiscoveryFindsAndValidatesSampleModule()
    {
        var discovered = new AssemblyModuleDiscovery().Discover([typeof(SampleModule).Assembly]);

        var module = Assert.Single(discovered);
        Assert.Equal("erp.sample", module.Manifest.Id);
        Assert.Equal("0.1.0", module.Manifest.Version);
    }

    [Fact]
    public void RegistryRejectsDuplicateModuleIds()
    {
        var registry = new ModuleRegistry();
        registry.Add(new SampleModule());

        var error = Assert.Throws<InvalidOperationException>(() => registry.Add(new SampleModule()));
        Assert.Contains("erp.sample", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionRegistryRejectsDuplicatePermissions()
    {
        var registry = new PermissionRegistry();
        var permission = new PermissionDefinition("sample.read", "Read", "erp.sample");
        registry.Add(permission);

        Assert.Throws<InvalidOperationException>(() => registry.Add(permission));
    }

    [Fact]
    public void ManifestParserReadsStrictSampleManifest()
    {
        const string json = """
            {
              "id": "community.payroll",
              "name": "Payroll",
              "version": "1.2.0-beta.1",
              "description": "Payroll processing.",
              "author": "Community",
              "license": "Apache-2.0",
              "minPlatformVersion": "0.1.0",
              "dependencies": [{ "module": "erp.contacts", "version": ">=1.0.0 <2.0.0" }],
              "permissions": ["payroll.read", "payroll.run"]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var manifest = new ModuleManifestParser().Parse(stream);

        Assert.Equal("community.payroll", manifest.Id);
        Assert.Equal("1.2.0-beta.1", manifest.Version);
        Assert.Equal(">=1.0.0 <2.0.0", Assert.Single(manifest.Dependencies).VersionRange);
        Assert.Equal(2, manifest.Permissions.Count);
    }

    [Fact]
    public void ManifestParserRejectsUnknownProperties()
    {
        const string json = """
            {
              "id": "erp.invalid",
              "name": "Invalid",
              "version": "1.0.0",
              "description": "Invalid manifest.",
              "author": "Community",
              "license": "Apache-2.0",
              "minPlatformVersion": "0.1.0",
              "dependencies": [],
              "permissions": [],
              "unexpected": true
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        Assert.Throws<InvalidOperationException>(() => new ModuleManifestParser().Parse(stream));
    }

    [Fact]
    public void DependencyGraphOrdersCompatibleDependencies()
    {
        var contacts = Descriptor(Manifest("erp.contacts", "1.4.0"));
        var sales = Descriptor(Manifest("erp.sales", "1.0.0", [new("erp.contacts", ">=1.0.0 <2.0.0")]));

        var ordered = new ModuleDependencyGraph("0.1.0").Order([sales, contacts]);

        Assert.Equal(["erp.contacts", "erp.sales"], ordered.Select(x => x.Manifest.Id));
    }

    [Fact]
    public void DependencyGraphRejectsIncompatibleVersion()
    {
        var contacts = Descriptor(Manifest("erp.contacts", "2.0.0"));
        var sales = Descriptor(Manifest("erp.sales", "1.0.0", [new("erp.contacts", ">=1.0.0 <2.0.0")]));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new ModuleDependencyGraph("0.1.0").Order([contacts, sales]));
        Assert.Contains("but found '2.0.0'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundJobRegistryRejectsDuplicateStableNames()
    {
        var registry = new BackgroundJobRegistry();
        registry.Add<SampleHeartbeatJob, SampleHeartbeatPayload>("erp.sample.heartbeat");

        Assert.Throws<InvalidOperationException>(() =>
            registry.Add<SampleHeartbeatJob, SampleHeartbeatPayload>("erp.sample.heartbeat"));
        Assert.Throws<ArgumentException>(() =>
            registry.Add<SampleHeartbeatJob, SampleHeartbeatPayload>("invalid name"));
    }

    [Fact]
    public void IntegrationEventRegistryRequiresUniqueVersionedNamesAndPayloads()
    {
        var registry = new IntegrationEventRegistry();
        registry.Add<SampleGreetingPublished>("sample.greeting.published.v1");

        Assert.Throws<InvalidOperationException>(() =>
            registry.Add<SampleGreetingPublished>("sample.greeting.renamed.v2"));
        Assert.Throws<ArgumentException>(() =>
            registry.Add<SampleGreetingPublished>("SampleGreetingPublished"));
    }

    private static ModuleManifest Manifest(
        string id,
        string version,
        IReadOnlyList<ModuleDependency>? dependencies = null) =>
        new(id, id, version, "Test module.", "Test", "Apache-2.0", "0.1.0", dependencies ?? [], []);

    private static ModuleDescriptor Descriptor(ModuleManifest manifest) =>
        new(manifest, typeof(SampleModule), typeof(SampleModule).Assembly);
}
