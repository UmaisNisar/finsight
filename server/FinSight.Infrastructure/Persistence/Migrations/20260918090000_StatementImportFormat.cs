using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinSight.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StatementImportFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "Statements",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "Statements");
        }
    }
}
