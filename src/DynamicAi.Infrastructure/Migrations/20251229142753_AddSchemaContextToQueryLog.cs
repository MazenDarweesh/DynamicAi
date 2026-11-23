using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSchemaContextToQueryLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompanyId",
                table: "QueryLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaContext",
                table: "QueryLogs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "QueryLogs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "SchemaContext",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "QueryLogs");
        }
    }
}
