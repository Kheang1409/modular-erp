using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Erp.ModuleSystem;
using Erp.Modules.Sample;

namespace Erp.UnitTests;

public sealed class ModulePackageRecoveryTests
{
    [Fact]
    public void FailedUpdatePreservesInstalledPackageAndDisabledState()
    {
        using var fixture = new PackageFixture();
        var failure = new IOException("Injected atomic state replacement failure.");
        var files = new FaultingFileOperations();
        var manager = fixture.Manager(files);
        manager.Install(fixture.Archive("original"));
        manager.SetEnabled("erp.sample", false);
        var packageBefore = fixture.PackageBytes();
        var stateBefore = File.ReadAllBytes(fixture.StatePath);
        files.MovingFile = (_, destination, _) =>
        {
            if (destination != fixture.StatePath) return;
            files.MovingFile = null;
            throw failure;
        };
        var replacement = fixture.Archive("replacement");

        var error = Assert.Throws<IOException>(() => manager.Install(replacement, update: true));

        Assert.Same(failure, error);
        AssertPackageBytesEqual(packageBefore, fixture.PackageBytes());
        Assert.Equal(stateBefore, File.ReadAllBytes(fixture.StatePath));
        Assert.False(Assert.Single(manager.List()).Enabled);
        Assert.Empty(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
        fixture.AssertNoPendingFiles();

        var retried = manager.Install(replacement, update: true);

        Assert.True(retried.Enabled);
        Assert.True(retried.RestartRequired);
        Assert.Equal("replacement", fixture.Release);
        Assert.Single(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
        fixture.AssertNoPendingFiles();
    }

    [Fact]
    public void FailedInitialInstallLeavesNoVisiblePackageAndCanBeRetried()
    {
        using var fixture = new PackageFixture();
        var failure = new IOException("Injected initial state replacement failure.");
        var files = new FaultingFileOperations();
        files.MovingFile = (_, destination, _) =>
        {
            if (destination != fixture.StatePath) return;
            files.MovingFile = null;
            throw failure;
        };
        var manager = fixture.Manager(files);
        var archive = fixture.Archive("initial");

        Assert.Same(failure, Assert.Throws<IOException>(() => manager.Install(archive)));

        Assert.False(Directory.Exists(fixture.PackagePath));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.Empty(manager.List());
        Assert.Empty(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
        fixture.AssertNoPendingFiles();

        Assert.True(manager.Install(archive).Enabled);
        Assert.Equal("initial", fixture.Release);
        Assert.Single(manager.List());
        fixture.AssertNoPendingFiles();
    }

    [Fact]
    public void FailedPromotionRestoresOriginalPackageBeforeRetry()
    {
        using var fixture = new PackageFixture();
        var files = new FaultingFileOperations();
        var manager = fixture.Manager(files);
        manager.Install(fixture.Archive("original"));
        manager.SetEnabled("erp.sample", false);
        var packageBefore = fixture.PackageBytes();
        var stateBefore = File.ReadAllBytes(fixture.StatePath);
        var failure = new IOException("Injected staged directory promotion failure.");
        files.MovingDirectory = (source, destination) =>
        {
            if (!fixture.IsPromotion(source, destination)) return;
            files.MovingDirectory = null;
            throw failure;
        };
        var replacement = fixture.Archive("replacement");

        Assert.Same(failure, Assert.Throws<IOException>(() => manager.Install(replacement, update: true)));

        AssertPackageBytesEqual(packageBefore, fixture.PackageBytes());
        Assert.Equal(stateBefore, File.ReadAllBytes(fixture.StatePath));
        Assert.False(Assert.Single(manager.List()).Enabled);
        fixture.AssertNoPendingFiles();

        Assert.True(manager.Install(replacement, update: true).Enabled);
        Assert.Equal("replacement", fixture.Release);
        fixture.AssertNoPendingFiles();
    }

    [Fact]
    public void FailedBackupMoveDoesNotTouchInstalledPackageOrState()
    {
        using var fixture = new PackageFixture();
        var files = new FaultingFileOperations();
        var manager = fixture.Manager(files);
        manager.Install(fixture.Archive("original"));
        manager.SetEnabled("erp.sample", false);
        var before = fixture.Snapshot();
        var failure = new IOException("Injected installed package backup failure.");
        files.MovingDirectory = (source, _) =>
        {
            if (source != fixture.PackagePath) return;
            files.MovingDirectory = null;
            throw failure;
        };

        Assert.Same(failure, Assert.Throws<IOException>(() => manager.Install(fixture.Archive("replacement"), update: true)));

        fixture.AssertSnapshot(before);
        fixture.AssertNoPendingFiles();
    }

    [Fact]
    public void ToggleStateFailurePreservesExactBytesAndCanBeRetried()
    {
        using var fixture = new PackageFixture();
        var files = new FaultingFileOperations();
        var manager = fixture.Manager(files);
        manager.Install(fixture.Archive("original"));
        // Deliberate whitespace and casing prove failure does not rewrite equivalent JSON.
        File.WriteAllText(fixture.StatePath, "{\r\n  \"ERP.SAMPLE\" : true\r\n}\r\n");
        var before = fixture.Snapshot();
        var failure = new IOException("Injected toggle state replacement failure.");
        files.MovingFile = (_, destination, _) =>
        {
            if (destination != fixture.StatePath) return;
            files.MovingFile = null;
            throw failure;
        };

        Assert.Same(failure, Assert.Throws<IOException>(() => manager.SetEnabled("erp.sample", false)));

        fixture.AssertSnapshot(before);
        Assert.True(Assert.Single(manager.List()).Enabled);
        Assert.Single(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
        fixture.AssertNoPendingFiles();

        Assert.False(manager.SetEnabled("erp.sample", false).Enabled);
        Assert.Empty(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
        fixture.AssertNoPendingFiles();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"erp.sample\":\"false\"}")]
    [InlineData("{\"erp.sample\":false,\"ERP.SAMPLE\":true}")]
    [InlineData("{\"erp.sample\":false,\"erp.sample\":true}")]
    public void CorruptStateFailsClosedBeforeAnyPackageMutation(string state)
    {
        using var fixture = new PackageFixture();
        var manager = fixture.Manager();
        manager.Install(fixture.Archive("original"));
        File.WriteAllText(fixture.StatePath, state);
        var before = fixture.Snapshot();
        var archive = fixture.Archive("replacement");
        Action[] operations =
        [
            () => manager.List(),
            () => manager.Validate(archive),
            () => manager.Install(archive, update: true),
            () => manager.SetEnabled("erp.sample", false),
            () => new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root)
        ];

        foreach (var operation in operations)
        {
            Assert.Throws<InvalidDataException>(operation);
            fixture.AssertSnapshot(before);
        }
    }

    [Fact]
    public void CaseInsensitiveStateHasOneMeaningForManagerValidationAndCatalog()
    {
        using var fixture = new PackageFixture();
        var manager = fixture.Manager();
        var archive = fixture.Archive("original");
        manager.Install(archive);
        File.WriteAllText(fixture.StatePath, "{\"ERP.SAMPLE\":false}");

        Assert.False(Assert.Single(manager.List()).Enabled);
        Assert.False(manager.Validate(archive).Enabled);
        Assert.Empty(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));

        Assert.True(manager.SetEnabled("erp.sample", true).Enabled);

        using var state = JsonDocument.Parse(File.ReadAllText(fixture.StatePath));
        var property = Assert.Single(state.RootElement.EnumerateObject());
        Assert.Equal("erp.sample", property.Name, ignoreCase: true);
        Assert.True(property.Value.GetBoolean());
        Assert.True(Assert.Single(manager.List()).Enabled);
        Assert.Single(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
    }

    [Fact]
    public void PostCommitBackupCleanupFailureCannotUndoSuccessfulUpdate()
    {
        using var fixture = new PackageFixture();
        var files = new FaultingFileOperations();
        var manager = fixture.Manager(files);
        manager.Install(fixture.Archive("original"));
        manager.SetEnabled("erp.sample", false);
        var oldPackage = fixture.PackageBytes();
        files.DeletingDirectory = path =>
        {
            if (PackageFixture.IsBackup(path)) throw new IOException("Injected committed backup cleanup failure.");
        };

        var installed = manager.Install(fixture.Archive("replacement"), update: true);

        Assert.True(installed.Enabled);
        Assert.True(installed.Installed);
        Assert.True(installed.RestartRequired);
        Assert.Equal("replacement", fixture.Release);
        Assert.True(Assert.Single(manager.List()).Enabled);
        Assert.Single(new ExternalModuleCatalog(new ModuleManifestParser()).Load(fixture.Root));
        var backup = Assert.Single(fixture.Backups());
        AssertPackageBytesEqual(oldPackage, PackageFixture.DirectoryBytes(backup));
        using var state = JsonDocument.Parse(File.ReadAllText(fixture.StatePath));
        Assert.True(state.RootElement.GetProperty("erp.sample").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackFailureRetainsRecoveryBackupAndBothErrors(bool failPromotedCleanup)
    {
        using var fixture = new PackageFixture();
        var files = new FaultingFileOperations();
        var manager = fixture.Manager(files);
        manager.Install(fixture.Archive("original"));
        manager.SetEnabled("erp.sample", false);
        var oldPackage = fixture.PackageBytes();
        var oldState = File.ReadAllBytes(fixture.StatePath);
        var stateFailure = new IOException("Injected primary state replacement failure.");
        var rollbackFailure = new IOException("Injected rollback recovery failure.");
        files.MovingFile = (_, destination, _) =>
        {
            if (destination == fixture.StatePath) throw stateFailure;
        };
        if (failPromotedCleanup)
        {
            files.DeletingDirectory = path =>
            {
                if (path == fixture.PackagePath) throw rollbackFailure;
            };
        }
        else
        {
            files.MovingDirectory = (source, _) =>
            {
                if (PackageFixture.IsBackup(source)) throw rollbackFailure;
            };
        }

        var error = Assert.Throws<AggregateException>(() => manager.Install(fixture.Archive("replacement"), update: true));

        var errors = error.Flatten().InnerExceptions;
        Assert.Contains(stateFailure, errors);
        Assert.Contains(rollbackFailure, errors);
        Assert.Equal(oldState, File.ReadAllBytes(fixture.StatePath));
        var backup = Assert.Single(fixture.Backups());
        AssertPackageBytesEqual(oldPackage, PackageFixture.DirectoryBytes(backup));
        Assert.Equal(failPromotedCleanup, Directory.Exists(fixture.PackagePath));
        if (failPromotedCleanup) Assert.Equal("replacement", fixture.Release);
    }

    [Fact]
    public async Task ManagersAndCatalogSerializeAgainstInFlightPromotionForEquivalentRoots()
    {
        using var fixture = new PackageFixture();
        var files = new FaultingFileOperations();
        var updatingManager = fixture.Manager(files);
        updatingManager.Install(fixture.Archive("original"));
        var archive = fixture.Archive("replacement");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        files.MovingDirectory = (source, destination) =>
        {
            if (!fixture.IsPromotion(source, destination)) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test did not release the staged package promotion.");
        };
        var update = Task.Run(() => updatingManager.Install(archive, update: true));
        var alias = Path.Combine(fixture.Root, "..", Path.GetFileName(fixture.Root));
        var otherManager = new ModulePackageManager(new ModuleManifestParser(), alias);
        Task[] observers = [];
        Task<IReadOnlyList<Erp.Abstractions.Modules.ModulePackageInfo>>? listed = null;
        Task<Erp.Abstractions.Modules.ModulePackageInfo>? toggled = null;
        Task<int>? loaded = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Directory.Exists(fixture.PackagePath));
            var listStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var toggleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var catalogStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listed = Task.Run(() =>
            {
                listStarted.SetResult();
                return otherManager.List();
            });
            toggled = Task.Run(() =>
            {
                toggleStarted.SetResult();
                return otherManager.SetEnabled("erp.sample", false);
            });
            loaded = Task.Run(() =>
            {
                catalogStarted.SetResult();
                return new ExternalModuleCatalog(new ModuleManifestParser()).Load(alias).Count;
            });
            observers = [listed, toggled, loaded];
            await Task.WhenAll(listStarted.Task, toggleStarted.Task, catalogStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await Task.WhenAny(observers).WaitAsync(TimeSpan.FromMilliseconds(150)));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(observers.Append(update)).WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.NotNull(listed);
        Assert.NotNull(toggled);
        Assert.NotNull(loaded);
        Assert.Single(await listed);
        Assert.False((await toggled).Enabled);
        Assert.InRange(await loaded, 0, 1);
        Assert.Equal("replacement", fixture.Release);
        Assert.False(Assert.Single(otherManager.List()).Enabled);
        Assert.Empty(new ExternalModuleCatalog(new ModuleManifestParser()).Load(alias));
        fixture.AssertNoPendingFiles();
    }

    private static void AssertPackageBytesEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, bytes) in expected) Assert.Equal(bytes, actual[name]);
    }

    private sealed class FaultingFileOperations : ModulePackageFileOperations
    {
        public Action<string, string>? MovingDirectory { get; set; }
        public Action<string>? DeletingDirectory { get; set; }
        public Action<string, string, bool>? MovingFile { get; set; }

        public override void MoveDirectory(string source, string destination)
        {
            MovingDirectory?.Invoke(source, destination);
            base.MoveDirectory(source, destination);
        }

        public override void DeleteDirectory(string path)
        {
            DeletingDirectory?.Invoke(path);
            base.DeleteDirectory(path);
        }

        public override void MoveFile(string source, string destination, bool overwrite)
        {
            MovingFile?.Invoke(source, destination, overwrite);
            base.MoveFile(source, destination, overwrite);
        }
    }

    private sealed record TreeSnapshot(Dictionary<string, byte[]> Files, string[] Directories);

    private sealed class PackageFixture : IDisposable
    {
        private readonly string _temporary = Path.Combine(Path.GetTempPath(), "erp-module-recovery-tests", Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public string StatePath => Path.Combine(Root, "modules.state.json");
        public string PackagePath => Path.Combine(Root, "erp.sample");
        public string Release => File.ReadAllText(Path.Combine(PackagePath, "lib", "release.txt"));

        public PackageFixture()
        {
            Root = Path.Combine(_temporary, "installed");
            Directory.CreateDirectory(_temporary);
        }

        public ModulePackageManager Manager() => new(new ModuleManifestParser(), Root);
        public ModulePackageManager Manager(ModulePackageFileOperations files) => new(new ModuleManifestParser(), Root, null, files);

        public bool IsPromotion(string source, string destination) => destination == PackagePath &&
            Path.GetDirectoryName(source) == Path.Combine(Root, ".staging");

        public static bool IsBackup(string path) => Path.GetFileName(path).StartsWith(".erp.sample.backup-", StringComparison.Ordinal);

        public string[] Backups() => Directory.GetDirectories(Root).Where(IsBackup).ToArray();

        public TreeSnapshot Snapshot() => new(DirectoryBytes(Root), Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path)).Order(StringComparer.Ordinal).ToArray());

        public void AssertSnapshot(TreeSnapshot expected)
        {
            var actual = Snapshot();
            AssertPackageBytesEqual(expected.Files, actual.Files);
            Assert.Equal(expected.Directories, actual.Directories);
        }

        public void AssertNoPendingFiles()
        {
            Assert.Empty(Backups());
            var staging = Path.Combine(Root, ".staging");
            if (Directory.Exists(staging)) Assert.Empty(Directory.GetFileSystemEntries(staging));
            Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
        }

        public string Archive(string release)
        {
            var source = Path.Combine(_temporary, "source-" + Guid.NewGuid().ToString("N"));
            var library = Path.Combine(source, "lib");
            Directory.CreateDirectory(library);
            var assembly = Path.Combine(library, "Erp.Modules.Sample.dll");
            File.Copy(typeof(SampleModule).Assembly.Location, assembly);
            File.Copy(Path.Combine(Path.GetDirectoryName(typeof(SampleModule).Assembly.Location)!, "module.json"), Path.Combine(source, "module.json"));
            File.WriteAllText(Path.Combine(library, "release.txt"), release);
            using (var stream = File.OpenRead(assembly))
                File.WriteAllText(Path.Combine(source, "module.sha256"), $"{Convert.ToHexString(SHA256.HashData(stream))}  lib/Erp.Modules.Sample.dll{Environment.NewLine}");
            var archive = Path.Combine(_temporary, Guid.NewGuid().ToString("N") + ".erpmod");
            ZipFile.CreateFromDirectory(source, archive);
            return archive;
        }

        public Dictionary<string, byte[]> PackageBytes() => DirectoryBytes(PackagePath);

        public static Dictionary<string, byte[]> DirectoryBytes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllBytes, StringComparer.Ordinal);

        public void Dispose()
        {
            if (Directory.Exists(_temporary)) Directory.Delete(_temporary, recursive: true);
        }
    }
}
