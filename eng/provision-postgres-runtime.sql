\set ON_ERROR_STOP on

-- Run with a PostgreSQL administrator after creating the login roles through
-- the deployment secret provider. Example variables:
--   psql --set=database_name=erp --set=migration_role=erp_migrator \
--        --set=runtime_role=erp_runtime --file=eng/provision-postgres-runtime.sql
-- This script intentionally never creates roles or handles passwords.

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON DATABASE :"database_name" FROM :"runtime_role";
GRANT CONNECT ON DATABASE :"database_name" TO :"runtime_role";

GRANT USAGE ON SCHEMA
    platform,
    contacts,
    products,
    crm,
    sales,
    purchasing,
    inventory,
    accounting
TO :"runtime_role";

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA
    platform,
    contacts,
    products,
    crm,
    sales,
    purchasing,
    inventory,
    accounting
TO :"runtime_role";

GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA
    platform,
    contacts,
    products,
    crm,
    sales,
    purchasing,
    inventory,
    accounting
TO :"runtime_role";

ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA platform GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA contacts GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA products GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA crm GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA sales GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA purchasing GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA inventory GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA accounting GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime_role";

ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA platform GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA contacts GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA products GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA crm GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA sales GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA purchasing GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA inventory GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";
ALTER DEFAULT PRIVILEGES FOR ROLE :"migration_role" IN SCHEMA accounting GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO :"runtime_role";

-- The runtime identity receives no CREATE privilege and must never own schemas,
-- tables, sequences, functions, migrations, or extensions. Re-run reviewed
-- grants when installing a trusted module that introduces another schema.
