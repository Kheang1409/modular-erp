# Tenant isolation

Authenticated users receive `tenant_id` and `company_id` claims from their default persisted company membership. Request contexts parse those trusted claims; clients cannot select arbitrary identifiers through headers.

Tenant-owned EF entities implement `ITenantOwned`. Their global filters use the request tenant, and the DbContext validates added, changed, and deleted tenant-owned entities before saving. Database composite keys additionally enforce tenant/company consistency for branches and memberships.

Infrastructure bootstrap uses an explicitly constructed system DbContext without a request context. Application and module code must use the dependency-injected context and must not call `IgnoreQueryFilters`; architecture/API reviews should treat either bypass as privileged code.

Roles and role-permission assignments are tenant-owned and filtered by the same context. User-role links remain standard Identity records because a user may belong to several tenants; claims generation joins them to the active membership's tenant before emitting permission claims. Assignment writes accept only users with an active-tenant membership and roles owned by that tenant, replacing only that tenant's links. Role names are unique within a tenant, not globally.

The PostgreSQL integration test proves that Tenant A sees only its company, cannot update Tenant B's company, and cannot create a branch that pairs Tenant A with Tenant B's company.
