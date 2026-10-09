# Contributing

Open ERP is a pre-release project licensed under [Apache-2.0](LICENSE). Discuss substantial changes before investing in them.

Keep module boundaries explicit, avoid cross-module persistence access, and submit one coherent vertical slice per pull request. Run restore, build, and all tests locally. New persistence behavior needs a PostgreSQL integration test and a reviewed migration. New permissions must be enforced server-side and registered by their owning module.

Use `feature/*` or `fix/*` branches, keep commits focused, and explain migrations, security implications, and known limitations in the pull request.
