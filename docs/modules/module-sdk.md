# Module SDK and external discovery

Install the repository-local template once:

```powershell
dotnet new install ./templates/Erp.ModuleSdk
```

Generate a module beneath the repository `modules` folder so its default project reference resolves:

```powershell
dotnet new erp-module -n Payroll -o modules/Payroll --ModuleId community.payroll
dotnet build modules/Payroll/Payroll.csproj
```

The generated Razor project references only `Erp.Abstractions` and includes a module class, strict manifest, service, protected endpoint, navigation entry, permission, and page. Use the public event, job, storage, sequence, and context contracts when the module needs those capabilities.

[`samples/CommunityHello`](../../samples/CommunityHello) is a checked-in, independently packaged example. It is not linked into the host and is therefore safe to validate, install, disable, and enable as an external module. CI performs that complete package lifecycle on every change.

Package and inspect the sample locally without changing the development module directory:

```powershell
./eng/pack-module.ps1 -Project samples/CommunityHello/CommunityHello.csproj -OutputRoot artifacts/community-module
dotnet run --project src/Tools/Erp.Cli -- module validate artifacts/community-module/community.hello-0.1.0.erpmod
dotnet run --project src/Tools/Erp.Cli -- module install artifacts/community-module/community.hello-0.1.0.erpmod --root artifacts/community-install
```

Create the restart-loadable folder:

```powershell
./eng/pack-module.ps1 -Project modules/Payroll/Payroll.csproj
```

To publish a signed package, generate and protect an ECDSA private key outside the repository, then provide its publisher ID during packing:

```powershell
./eng/pack-module.ps1 -Project modules/Payroll/Payroll.csproj -Publisher acme -PrivateKeyPath C:\secure\acme-private.pem
```

Copy only the matching public key to `.erp/trusted-publishers/acme.pem`. In production set `Modules__Trust__RequireSignatures=true`. Private signing keys must never be copied into the application, container, repository, or `.env` file.

The packer builds Release output and creates a formal `<id>-<version>.erpmod` ZIP archive. Its contents are:

```text
modules/external/community.payroll/
|-- module.json
|-- module.sha256
`-- lib/Payroll.dll
```

Validate and install the archive with the repository CLI:

```powershell
dotnet run --project src/Tools/Erp.Cli -- module validate modules/external/community.payroll-0.1.0.erpmod
dotnet run --project src/Tools/Erp.Cli -- module install modules/external/community.payroll-0.1.0.erpmod
dotnet run --project src/Tools/Erp.Cli -- module disable community.payroll
dotnet run --project src/Tools/Erp.Cli -- module enable community.payroll
```

Use `--root <path>` to manage a non-default module directory. `list`, `status`, `search`, `validate`, `install`, `update`, `enable`, and `disable` emit machine-readable JSON. Lifecycle changes require an application restart.

For signed packages, pass `--trusted-keys .erp/trusted-publishers`; add `--require-signature` to reject unsigned archives. The web Module Manager uses the host trust policy automatically and requires `platform.module.manage`. Install, update, enable, and disable operations are written to the tenant audit log.

Installation extracts into an isolated staging directory, rejects unexpected paths, traversal and symbolic links, then applies the same manifest, inventory, assembly metadata, and checksum validation used at startup. Updates retain a private backup until replacement files and enablement state are saved. Disabling changes state only; it never deletes module business data. A successful install or update enables the package, preserving the existing lifecycle behavior.

### Failure and recovery boundary

The manager validates existing `modules.state.json` before mutating installed
packages. State must be a JSON object containing boolean flags with unique,
case-insensitive module IDs. Invalid, null, non-boolean, and duplicate-key state
fails closed in both the manager and startup discovery instead of accidentally
enabling modules. Repair only reviewed state; do not delete the file merely
to bypass an error, because absent flags default to enabled.

State is written to a unique same-directory temporary file, flushed, and moved
over the old file. Saving state is the package operation's commit point. A caught
failure before that point removes the promoted package and restores the backup
when filesystem operations permit it. Recovery errors retain the hidden backup
and both the original and recovery failures for manual inspection. After commit,
an I/O or access failure deleting the old backup leaves the operation successful.
Any remaining hidden backup files are kept for inspection, but recursive cleanup
may already have deleted part of that old backup. Hidden folders are not loaded
as modules.

List, validation, install/update, toggles, and the complete startup scan wait up
to 30 seconds to acquire a named-mutex lease. Cooperating processes on one host
must use the same normalized absolute root path (Windows casing and lexical `..`/trailing
separator differences are normalized). Symlink, junction, short-name, and other
physical-path aliases are not unified. This is not multi-host/shared-volume
coordination, hot reload, or a substitute for trusted-code review.

The package filesystem commit and the web endpoint's subsequent PostgreSQL audit
write are **separate**. An audit failure can return an error after the package was
changed: inspect `list`/`status` and state before retrying. The CLI does not create
tenant audit records. No distributed transaction or durable recovery journal is
introduced. Process termination, power loss, failed recovery I/O, and database
migrations still require operator inspection and tested backups. An observed
abandoned mutex reports an interruption but is not a persistent recovery barrier.
See the [operations recovery guide](../deployment/production.md#recovery).

### Startup discovery and trust

At startup, the host scans each enabled external folder. Before executing code, it strictly parses the manifest, requires exactly one DLL, verifies an exact SHA-256 inventory, loads the assembly without retaining a package-file lock, requires exactly one public parameterless `IErpModule`, compares assembly metadata to `module.json`, and then validates the combined dependency graph with built-in modules. Invalid packages fail startup instead of being partially registered.

Installing a server module is equivalent to installing trusted server software. Checksums detect corruption or unexpected file changes; they do not establish publisher identity and do not make CLR code safe. Review the source and publisher before installation.

Module database initializers run in dependency order during startup under a PostgreSQL advisory migration lock. Successfully migrated module versions are tracked in `platform.installed_modules`. Failed migrations stop startup and are not recorded as successful; database migrations are not automatically reversible.
