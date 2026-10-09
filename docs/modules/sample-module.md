# SampleModule tutorial

`samples/SampleModule` proves the minimum module surface:

1. `SampleModule` exposes metadata and its component assembly; `module.json` mirrors that manifest.
2. `ConfigureServices` registers a module-owned service.
3. `ConfigureBackgroundJobs` registers a typed handler under the stable `erp.sample.heartbeat` name.
4. `ConfigureServices` also registers the `SampleGreetingRequested` domain-event handler.
5. `ConfigureIntegrationEvents` registers the versioned `sample.greeting.published.v1` payload and its consumer.
6. `ConfigurePermissions` defines `sample.read` and `sample.events.publish`.
7. `ConfigureNavigation` adds a permission-filtered menu entry.
8. `ConfigureEndpoints` maps protected sample and event demonstration endpoints.
9. `SamplePage.razor` maps `/sample` and requires the read policy.

The module references only `Erp.Abstractions`; architecture tests enforce that it does not depend on platform internals. A server module is trusted executable code, not a sandboxed script.

See [module manifests](module-manifests.md) for validation and dependency-range rules.
