using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHcmEntityConcurrencyStamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "training_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "performance_reviews",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "leave_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "employees",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "employee_benefits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "attendance_records",
                type: "uuid",
                nullable: true);

            foreach (var table in new[] { "training_records", "performance_reviews", "leave_requests", "employees", "employee_benefits", "attendance_records" })
            {
                migrationBuilder.Sql($"UPDATE hcm.\"{table}\" SET \"ConcurrencyStamp\" = gen_random_uuid() WHERE \"ConcurrencyStamp\" IS NULL;");
                migrationBuilder.AlterColumn<Guid>(
                    name: "ConcurrencyStamp",
                    schema: "hcm",
                    table: table,
                    type: "uuid",
                    nullable: false,
                    oldClrType: typeof(Guid),
                    oldType: "uuid",
                    oldNullable: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "training_records");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "performance_reviews");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "leave_requests");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "employees");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "employee_benefits");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                schema: "hcm",
                table: "attendance_records");
        }
    }
}
