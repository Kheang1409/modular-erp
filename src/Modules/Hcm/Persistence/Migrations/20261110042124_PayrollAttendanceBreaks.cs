using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Modules.Hcm.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollAttendanceBreaks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BreaksJson",
                schema: "hcm",
                table: "attendance_records",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BreaksJson",
                schema: "hcm",
                table: "attendance_records");
        }
    }
}
