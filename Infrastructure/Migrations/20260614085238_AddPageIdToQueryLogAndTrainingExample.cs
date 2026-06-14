using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPageIdToQueryLogAndTrainingExample : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PageId",
                table: "TrainingExamples",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PageId",
                table: "QueryLogs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PageId",
                table: "TrainingExamples");

            migrationBuilder.DropColumn(
                name: "PageId",
                table: "QueryLogs");
        }
    }
}
