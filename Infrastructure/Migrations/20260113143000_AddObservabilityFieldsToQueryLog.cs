using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddObservabilityFieldsToQueryLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "ConfidenceScore",
                table: "QueryLogs",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FeedbackStatus",
                table: "QueryLogs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "GeminiCost",
                table: "QueryLogs",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HandlerType",
                table: "QueryLogs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "QueryLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAmbiguous",
                table: "QueryLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LatencyMs",
                table: "QueryLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "QueryLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasFallback",
                table: "QueryLogs",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfidenceScore",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "FeedbackStatus",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "GeminiCost",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "HandlerType",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "IsAmbiguous",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "LatencyMs",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "QueryLogs");

            migrationBuilder.DropColumn(
                name: "WasFallback",
                table: "QueryLogs");
        }
    }
}
