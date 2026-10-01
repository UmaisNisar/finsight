using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinSight.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class TransactionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Settings_ReceiptMatchingEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TransactionReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    EmailDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OrderNumber = table.Column<string>(type: "text", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: true),
                    ItemsJson = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TransactionReceipts_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionReceipts_TransactionId",
                table: "TransactionReceipts",
                column: "TransactionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransactionReceipts");

            migrationBuilder.DropColumn(
                name: "Settings_ReceiptMatchingEnabled",
                table: "Users");
        }
    }
}
