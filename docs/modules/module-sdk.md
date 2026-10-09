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

Installation extracts into an isolated staging directory, rejects unexpected paths, traversal and symbolic links, then applies the same manifest, inventory, assembly metadata, and checksum validation used at startup. Updates first move the installed package to a private backup and restore it if replacement fails. Disabling changes state only; it never deletes module business data.

At startup, the host scans each enabled external folder. Before executing code, it strictly parses the manifest, requires exactly one DLL, verifies an exact SHA-256 inventory, loads the assembly without retaining a package-file lock, requires exactly one public parameterless `IErpModule`, compares assembly metadata to `module.json`, and then validates the combined dependency graph with built-in modules. Invalid packages fail startup instead of being partially registered.

Installing a server module is equivalent to installing trusted server software. Checksums detect corruption or unexpected file changes; they do not establish publisher identity and do not make CLR code safe. Review the source and publisher before installation.

Module database initializers run in dependency order during startup under a PostgreSQL advisory migration lock. Successfully migrated module versions are tracked in `platform.installed_modules`. Failed migrations stop startup and are not recorded as successful; database migrations are not automatically reversible.
