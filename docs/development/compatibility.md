# Compatibility and breaking changes

The current pre-release line is `0.1.x`. Public contracts may change while the version is below `1.0.0`, but each change must be called out in `CHANGELOG.md` and coordinated with bundled modules and the SDK template.

After `1.0.0`:

- Patch releases contain backward-compatible fixes.
- Minor releases may add backward-compatible contracts and manifest capabilities.
- Major releases may remove or change public contracts.
- A deprecated public contract should remain for at least one minor release unless retaining it would preserve a security defect.
- Module manifests declare their minimum platform version and dependency ranges; incompatible modules fail before registration.

The supported baseline for the unreleased `0.1.0` line is .NET 10 and PostgreSQL 18, matching `global.json`, CI, Testcontainers, and Compose. Browser support targets current evergreen Chromium, Firefox, and WebKit engines; automated critical-workflow coverage currently runs Chromium.

External modules are trusted in-process software. Binary compatibility alone does not imply publisher trust or safe upgrade semantics. Review release notes, signature identity, migrations, and backup readiness before upgrading.
