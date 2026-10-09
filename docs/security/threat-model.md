# Threat model

## Assets and trust boundaries

The primary assets are tenant business records, accounting entries, credentials, signing keys, Data Protection keys, attachments, audit history, outbox messages, and database backups. HTTP clients, reverse proxies, object storage, PostgreSQL, telemetry collectors, module publishers, and administrators cross explicit trust boundaries.

External CLR modules are trusted server software. They execute in-process with the host identity and are not a sandbox or tenant boundary. A publisher signature establishes key possession, not code safety. Review source and publisher ownership before adding a public key.

## Principal threats and controls

| Threat | Current controls | Remaining operator responsibility |
|---|---|---|
| Cross-tenant access | Membership-derived tenant/company context, EF query filters, explicit company predicates, write guards, isolation tests | Restrict database access and review new raw SQL |
| Broken authorization | Permission policies on commands and pages, tenant-scoped roles, final-system-admin protection | Review the authorization matrix whenever endpoints are added |
| CSRF/session theft | Antiforgery middleware and form tokens, HttpOnly/SameSite cookies, secure-cookie production default, short sliding session | Terminate TLS correctly and protect Data Protection keys |
| XSS/clickjacking/content sniffing | Razor encoding, CSP, frame denial, no-sniff, restrictive referrer and browser capability policies | Do not introduce untrusted markup or weaken CSP without review |
| SQL injection | EF parameterization and constrained sort/filter selection | Never concatenate user input into SQL |
| Host-header/proxy spoofing | Allowed-host validation and explicit known proxy IP allowlist | Configure `ReverseProxy:KnownProxies`; never trust arbitrary forwarded headers |
| Credential guessing/resource exhaustion | Login-specific and global per-user/IP fixed-window limits, upload size limits, pagination | Tune thresholds and edge rate limits from production telemetry |
| Malicious files | Filename/content-type/size validation, tenant-separated storage keys, scanner interface, fail-closed `RequireMalwareScanner` mode | Supply a scanner implementation and enable the production requirement |
| Malicious modules | Traversal/symlink/inventory rejection, SHA-256, ECDSA publisher trust, dependency validation, isolated staging, restart activation, lifecycle authorization/audit | Protect signing keys and trusted-key administration |
| Secret disclosure | Environment/secret-provider configuration, structured logs without request bodies, generic errors | Rotate exposed secrets, restrict telemetry, scan release artifacts |
| Lost or duplicated async work | Transactional outbox, leases, retry caps, dead-letter state, idempotency keys | Monitor queue depth and execute documented replay procedures |

## Review triggers

Repeat the threat review when adding raw SQL, HTML rendering, outbound HTTP, archive/file parsing, a new storage provider, a new module trust key, public callbacks, or a new tenant-scoped entity. Security defects must follow [SECURITY.md](../../SECURITY.md).
