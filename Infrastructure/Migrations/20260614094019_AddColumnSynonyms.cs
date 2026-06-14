using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnSynonyms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColumnSynonyms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageId = table.Column<int>(type: "int", nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UserTerm = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Language = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsApproved = table.Column<bool>(type: "bit", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnSynonyms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnSynonyms_PageId_IsApproved",
                table: "ColumnSynonyms",
                columns: new[] { "PageId", "IsApproved" });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnSynonyms_PageId_TableName_ColumnName_UserTerm",
                table: "ColumnSynonyms",
                columns: new[] { "PageId", "TableName", "ColumnName", "UserTerm" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnSynonyms");
        }
    }
}
