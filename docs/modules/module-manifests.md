# Module manifests

`IModuleManifestParser` reads strict JSON manifests. Unknown properties, missing required metadata, malformed module or permission identifiers, duplicate dependencies or permissions, and invalid semantic versions are rejected before registration.

Versions follow semantic versioning, including prerelease identifiers. Dependency ranges currently support exact versions and whitespace-separated `>`, `>=`, `<`, `<=`, and `=` comparisons, such as `>=1.0.0 <2.0.0`. Discovery rejects modules requiring a newer platform, missing dependencies, incompatible dependency versions, duplicate IDs, and circular graphs, then returns dependencies before their consumers.

A module's permissions registered through `ConfigurePermissions` must exactly match its manifest and must identify that module as their owner. This prevents runtime declarations from silently drifting away from package metadata.

The trusted SampleModule is compiled into the host and exposes its manifest through `IErpModule`. External `.erpmod` packages use the same parser and dependency graph. The package manager verifies exact checksum inventories and, when configured, ECDSA-SHA256 publisher signatures before loading code. Installations and updates are staged, enable/disable changes require restart, and dependent modules prevent unsafe disable operations.
