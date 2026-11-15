using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861

namespace Erp.Modules.Projects.Persistence.Migrations;

public sealed partial class InitialProjects : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("projects");
        migrationBuilder.CreateTable(
            name: "projects",
            schema: "projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                StartDate = table.Column<DateOnly>(type: "date", nullable: true),
                EndDate = table.Column<DateOnly>(type: "date", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_projects", x => x.Id);
                table.UniqueConstraint("AK_projects_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                table.CheckConstraint("CK_projects_DateRange", "\"EndDate\" IS NULL OR \"StartDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
            });

        migrationBuilder.CreateTable(
            name: "project_milestones",
            schema: "projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_milestones", x => x.Id);
                table.UniqueConstraint("AK_project_milestones_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                table.ForeignKey("FK_project_milestones_projects_TenantId_CompanyId_ProjectId",
                    x => new { x.TenantId, x.CompanyId, x.ProjectId }, "projects", new[] { "TenantId", "CompanyId", "Id" },
                    principalSchema: "projects", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "project_tasks",
            schema: "projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                MilestoneId = table.Column<Guid>(type: "uuid", nullable: true),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                AssignedUserId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_tasks", x => x.Id);
                table.UniqueConstraint("AK_project_tasks_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                table.ForeignKey("FK_project_tasks_projects_TenantId_CompanyId_ProjectId",
                    x => new { x.TenantId, x.CompanyId, x.ProjectId }, "projects", new[] { "TenantId", "CompanyId", "Id" },
                    principalSchema: "projects", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_project_tasks_project_milestones_TenantId_CompanyId_MilestoneId",
                    x => new { x.TenantId, x.CompanyId, x.MilestoneId }, "project_milestones",
                    new[] { "TenantId", "CompanyId", "Id" }, principalSchema: "projects", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "project_time_entries",
            schema: "projects",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                Hours = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                IsBillable = table.Column<bool>(type: "boolean", nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_project_time_entries", x => x.Id);
                table.UniqueConstraint("AK_project_time_entries_TenantId_CompanyId_Id", x => new { x.TenantId, x.CompanyId, x.Id });
                table.ForeignKey("FK_project_time_entries_projects_TenantId_CompanyId_ProjectId",
                    x => new { x.TenantId, x.CompanyId, x.ProjectId }, "projects", new[] { "TenantId", "CompanyId", "Id" },
                    principalSchema: "projects", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_project_time_entries_project_tasks_TenantId_CompanyId_TaskId",
                    x => new { x.TenantId, x.CompanyId, x.TaskId }, "project_tasks",
                    new[] { "TenantId", "CompanyId", "Id" }, principalSchema: "projects", onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_project_time_entries_Hours", "\"Hours\" > 0 AND \"Hours\" <= 24");
            });

        migrationBuilder.CreateIndex("IX_projects_TenantId_CompanyId_Name", "projects",
            new[] { "TenantId", "CompanyId", "Name" }, schema: "projects");
        migrationBuilder.CreateIndex("IX_project_milestones_TenantId_CompanyId_ProjectId", "project_milestones",
            new[] { "TenantId", "CompanyId", "ProjectId" }, schema: "projects");
        migrationBuilder.CreateIndex("IX_project_tasks_TenantId_CompanyId_ProjectId_Status", "project_tasks",
            new[] { "TenantId", "CompanyId", "ProjectId", "Status" }, schema: "projects");
        migrationBuilder.CreateIndex("IX_project_tasks_TenantId_CompanyId_MilestoneId", "project_tasks",
            new[] { "TenantId", "CompanyId", "MilestoneId" }, schema: "projects");
        migrationBuilder.CreateIndex("IX_project_time_entries_TenantId_CompanyId_ProjectId_WorkDate", "project_time_entries",
            new[] { "TenantId", "CompanyId", "ProjectId", "WorkDate" }, schema: "projects");
        migrationBuilder.CreateIndex("IX_project_time_entries_TenantId_CompanyId_TaskId", "project_time_entries",
            new[] { "TenantId", "CompanyId", "TaskId" }, schema: "projects");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("project_time_entries", "projects");
        migrationBuilder.DropTable("project_tasks", "projects");
        migrationBuilder.DropTable("project_milestones", "projects");
        migrationBuilder.DropTable("projects", "projects");
    }
}
