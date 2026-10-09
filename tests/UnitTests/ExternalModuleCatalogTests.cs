using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using Erp.ModuleSystem;
using Erp.Modules.Sample;

namespace Erp.UnitTests;

public sealed class ExternalModuleCatalogTests
{
    [Fact]
    public void CatalogLoadsValidatedExternalAssembly()
    {
        var root = CreatePackage(out var packagePath, out var assemblyPath);
        try
        {
            var loaded = new ExternalModuleCatalog(new ModuleManifestParser()).Load(root);

            var assembly = Assert.Single(loaded);
            Assert.Contains(assembly.DefinedTypes, type => type.Name == nameof(SampleModule));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogRejectsChecksumMismatchBeforeLoading()
    {
        var root = CreatePackage(out var packagePath, out var assemblyPath);
        try
        {
            File.AppendAllText(assemblyPath, "tampered");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                new ExternalModuleCatalog(new ModuleManifestParser()).Load(root));
            Assert.Contains("checksum validation", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackageManagerInstallsDisablesAndUpdatesAtomically()
    {
        var source = CreatePackage(out var packagePath, out _);
        var archive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.erpmod");
        var installedRoot = Path.Combine(Path.GetTempPath(), "erp-installed-modules", Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.CreateFromDirectory(packagePath, archive);
            var manager = new ModulePackageManager(new ModuleManifestParser(), installedRoot);

            var validated = manager.Validate(archive);
            Assert.Equal("erp.sample", validated.Id);
            Assert.False(validated.Installed);

            var installed = manager.Install(archive);
            Assert.True(installed.Installed);
            Assert.True(installed.Enabled);
            Assert.True(installed.RestartRequired);
            Assert.Single(new ExternalModuleCatalog(new ModuleManifestParser()).Load(installedRoot));

            var disabled = manager.SetEnabled("erp.sample", false);
            Assert.False(disabled.Enabled);
            Assert.Empty(new ExternalModuleCatalog(new ModuleManifestParser()).Load(installedRoot));

            Assert.Throws<InvalidOperationException>(() => manager.Install(archive));
            var updated = manager.Install(archive, update: true);
            Assert.True(updated.Enabled);
            Assert.Single(manager.List());
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(installedRoot)) Directory.Delete(installedRoot, recursive: true);
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    [Fact]
    public void PackageManagerRejectsArchiveTraversalAndUnexpectedFiles()
    {
        var archive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.erpmod");
        var root = Path.Combine(Path.GetTempPath(), "erp-installed-modules", Guid.NewGuid().ToString("N"));
        try
        {
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                zip.CreateEntry("../outside.txt");
            }
            var manager = new ModulePackageManager(new ModuleManifestParser(), root);
            Assert.Throws<InvalidDataException>(() => manager.Validate(archive));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    [Fact]
    public void PackageManagerVerifiesTrustedPublisherSignature()
    {
        var source = CreatePackage(out var packagePath, out _);
        var archive = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.erpmod");
        var installedRoot = Path.Combine(Path.GetTempPath(), "erp-signed-modules", Guid.NewGuid().ToString("N"));
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var signature = signer.SignData(ModulePackageManager.SignaturePayload(packagePath), HashAlgorithmName.SHA256);
            File.WriteAllText(Path.Combine(packagePath, "module.signature.json"), JsonSerializer.Serialize(new
            {
                publisher = "community.test",
                algorithm = "ECDSA-SHA256",
                signature = Convert.ToBase64String(signature)
            }));
            ZipFile.CreateFromDirectory(packagePath, archive);
            var policy = new ModuleTrustPolicy(true, new Dictionary<string, string>
            {
                ["community.test"] = signer.ExportSubjectPublicKeyInfoPem()
            });
            var manager = new ModulePackageManager(new ModuleManifestParser(), installedRoot, policy);

            Assert.Equal("erp.sample", manager.Validate(archive).Id);

            File.AppendAllText(Path.Combine(packagePath, "module.json"), " ");
            File.Delete(archive);
            ZipFile.CreateFromDirectory(packagePath, archive);
            var error = Assert.Throws<InvalidDataException>(() => manager.Validate(archive));
            Assert.Contains("signature validation failed", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(installedRoot)) Directory.Delete(installedRoot, recursive: true);
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    private static string CreatePackage(out string packagePath, out string assemblyPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "erp-module-catalog-tests", Guid.NewGuid().ToString("N"));
        packagePath = Path.Combine(root, "erp.sample");
        var libraryPath = Path.Combine(packagePath, "lib");
        Directory.CreateDirectory(libraryPath);
        assemblyPath = Path.Combine(libraryPath, "Erp.Modules.Sample.dll");
        File.Copy(typeof(SampleModule).Assembly.Location, assemblyPath);
        var sourceManifest = Path.Combine(Path.GetDirectoryName(typeof(SampleModule).Assembly.Location)!, "module.json");
        File.Copy(sourceManifest, Path.Combine(packagePath, "module.json"));
        using var stream = File.OpenRead(assemblyPath);
        var checksum = Convert.ToHexString(SHA256.HashData(stream));
        File.WriteAllText(Path.Combine(packagePath, "module.sha256"), $"{checksum}  lib/Erp.Modules.Sample.dll{Environment.NewLine}");
        return root;
    }
}
