using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF generates constant index column arrays.

#nullable disable

namespace Erp.Modules.Accounting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgeting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget_plans",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    FiscalPeriodId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_plans", x => x.Id);
                    table.UniqueConstraint("AK_budget_plans_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "budget_plan_lines",
                schema: "accounting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetAmount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_plan_lines", x => x.Id);
                    table.CheckConstraint("CK_budget_plan_lines_amount", "\"BudgetAmount\">=0");
                    table.ForeignKey(
                        name: "FK_budget_plan_lines_budget_plans_TenantId_CompanyId_BudgetPla~",
                        columns: x => new { x.TenantId, x.CompanyId, x.BudgetPlanId },
                        principalSchema: "accounting",
                        principalTable: "budget_plans",
                        principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budget_plan_lines_TenantId_CompanyId_BudgetPlanId_AccountId",
                schema: "accounting",
                table: "budget_plan_lines",
                columns: new[] { "TenantId", "CompanyId", "BudgetPlanId", "AccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budget_plans_TenantId_CompanyId_FiscalPeriodId_State",
                schema: "accounting",
                table: "budget_plans",
                columns: new[] { "TenantId", "CompanyId", "FiscalPeriodId", "State" },
                unique: true,
                filter: "\"State\" = 'Approved'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_plan_lines",
                schema: "accounting");

            migrationBuilder.DropTable(
                name: "budget_plans",
                schema: "accounting");
        }
    }
}
