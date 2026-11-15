using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Manufacturing.Persistence.Migrations
{
    public partial class InitialManufacturing : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'manufacturing') THEN
        CREATE SCHEMA manufacturing;
    END IF;
END $EF$;

CREATE TABLE manufacturing.bills_of_material (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "Number" character varying(50) NOT NULL,
    "ProductId" uuid NOT NULL,
    "ProductSku" character varying(100) NOT NULL,
    "ProductName" character varying(200) NOT NULL,
    "Revision" character varying(30) NOT NULL,
    "OutputQuantity" numeric(18,4) NOT NULL,
    "OutputUnitCode" character varying(20) NOT NULL,
    "State" character varying(20) NOT NULL,
    "Notes" character varying(2000),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" uuid,
    "UpdatedBy" uuid,
    "ConcurrencyStamp" character varying(32) NOT NULL,
    CONSTRAINT "PK_bills_of_material" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_bills_of_material_TenantId_CompanyId_Id" UNIQUE ("TenantId", "CompanyId", "Id"),
    CONSTRAINT "CK_bills_of_material_output_quantity" CHECK ("OutputQuantity">0)
);

CREATE TABLE manufacturing.bill_of_material_lines (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "BillOfMaterialId" uuid NOT NULL,
    "ComponentProductId" uuid NOT NULL,
    "ComponentSku" character varying(100) NOT NULL,
    "ComponentName" character varying(200) NOT NULL,
    "Quantity" numeric(18,4) NOT NULL,
    "UnitCode" character varying(20) NOT NULL,
    "Notes" character varying(1000),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" uuid,
    CONSTRAINT "PK_bill_of_material_lines" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_bill_of_material_lines_TenantId_CompanyId_Id" UNIQUE ("TenantId", "CompanyId", "Id"),
    CONSTRAINT "CK_bill_of_material_lines_quantity" CHECK ("Quantity">0),
    CONSTRAINT "FK_bill_of_material_lines_bills_of_material_TenantId_CompanyId~" FOREIGN KEY ("TenantId", "CompanyId", "BillOfMaterialId") REFERENCES manufacturing.bills_of_material ("TenantId", "CompanyId", "Id") ON DELETE CASCADE
);

CREATE TABLE manufacturing.work_orders (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "Number" character varying(50) NOT NULL,
    "BillOfMaterialId" uuid NOT NULL,
    "BillOfMaterialNumber" character varying(50) NOT NULL,
    "BillOfMaterialRevision" character varying(30) NOT NULL,
    "ProductId" uuid NOT NULL,
    "ProductSku" character varying(100) NOT NULL,
    "ProductName" character varying(200) NOT NULL,
    "PlannedQuantity" numeric(18,4) NOT NULL,
    "UnitCode" character varying(20) NOT NULL,
    "State" character varying(20) NOT NULL,
    "PlannedStartDate" date,
    "PlannedEndDate" date,
    "Notes" character varying(2000),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "UpdatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" uuid,
    "UpdatedBy" uuid,
    "ConcurrencyStamp" character varying(32) NOT NULL,
    CONSTRAINT "PK_work_orders" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_work_orders_TenantId_CompanyId_Id" UNIQUE ("TenantId", "CompanyId", "Id"),
    CONSTRAINT "CK_work_orders_planned_dates" CHECK ("PlannedEndDate" IS NULL OR "PlannedStartDate" IS NULL OR "PlannedEndDate">="PlannedStartDate"),
    CONSTRAINT "CK_work_orders_planned_quantity" CHECK ("PlannedQuantity">0),
    CONSTRAINT "FK_work_orders_bills_of_material_TenantId_CompanyId_BillOfMate~" FOREIGN KEY ("TenantId", "CompanyId", "BillOfMaterialId") REFERENCES manufacturing.bills_of_material ("TenantId", "CompanyId", "Id") ON DELETE RESTRICT
);

CREATE TABLE manufacturing.quality_inspections (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "WorkOrderId" uuid NOT NULL,
    "Number" character varying(50) NOT NULL,
    "SampleQuantity" numeric(18,4) NOT NULL,
    "Decision" character varying(20) NOT NULL,
    "InspectedAtUtc" timestamp with time zone NOT NULL,
    "Inspector" character varying(200),
    "Notes" character varying(2000),
    "CreatedAtUtc" timestamp with time zone NOT NULL,
    "CreatedBy" uuid,
    CONSTRAINT "PK_quality_inspections" PRIMARY KEY ("Id"),
    CONSTRAINT "AK_quality_inspections_TenantId_CompanyId_Id" UNIQUE ("TenantId", "CompanyId", "Id"),
    CONSTRAINT "CK_quality_inspections_sample_quantity" CHECK ("SampleQuantity">0),
    CONSTRAINT "FK_quality_inspections_work_orders_TenantId_CompanyId_WorkOrde~" FOREIGN KEY ("TenantId", "CompanyId", "WorkOrderId") REFERENCES manufacturing.work_orders ("TenantId", "CompanyId", "Id") ON DELETE RESTRICT
);

CREATE TABLE manufacturing.work_order_status_history (
    "Id" uuid NOT NULL,
    "TenantId" uuid NOT NULL,
    "CompanyId" uuid NOT NULL,
    "WorkOrderId" uuid NOT NULL,
    "FromState" character varying(20) NOT NULL,
    "ToState" character varying(20) NOT NULL,
    "ChangedAtUtc" timestamp with time zone NOT NULL,
    "ChangedBy" uuid,
    "Notes" character varying(2000),
    CONSTRAINT "PK_work_order_status_history" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_work_order_status_history_work_orders_TenantId_CompanyId_Wo~" FOREIGN KEY ("TenantId", "CompanyId", "WorkOrderId") REFERENCES manufacturing.work_orders ("TenantId", "CompanyId", "Id") ON DELETE CASCADE
);

CREATE INDEX "IX_bill_of_material_lines_TenantId_CompanyId_BillOfMaterialId" ON manufacturing.bill_of_material_lines ("TenantId", "CompanyId", "BillOfMaterialId");

CREATE UNIQUE INDEX "IX_bills_of_material_TenantId_CompanyId_Number" ON manufacturing.bills_of_material ("TenantId", "CompanyId", "Number");

CREATE UNIQUE INDEX "IX_quality_inspections_TenantId_CompanyId_Number" ON manufacturing.quality_inspections ("TenantId", "CompanyId", "Number");

CREATE INDEX "IX_quality_inspections_TenantId_CompanyId_WorkOrderId_Inspecte~" ON manufacturing.quality_inspections ("TenantId", "CompanyId", "WorkOrderId", "InspectedAtUtc");

CREATE INDEX "IX_work_order_status_history_TenantId_CompanyId_WorkOrderId_Ch~" ON manufacturing.work_order_status_history ("TenantId", "CompanyId", "WorkOrderId", "ChangedAtUtc");

CREATE INDEX "IX_work_orders_TenantId_CompanyId_BillOfMaterialId" ON manufacturing.work_orders ("TenantId", "CompanyId", "BillOfMaterialId");

CREATE UNIQUE INDEX "IX_work_orders_TenantId_CompanyId_Number" ON manufacturing.work_orders ("TenantId", "CompanyId", "Number");

CREATE INDEX "IX_work_orders_TenantId_CompanyId_State_CreatedAtUtc" ON manufacturing.work_orders ("TenantId", "CompanyId", "State", "CreatedAtUtc");
""");

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP SCHEMA manufacturing CASCADE;
""");
    }
}