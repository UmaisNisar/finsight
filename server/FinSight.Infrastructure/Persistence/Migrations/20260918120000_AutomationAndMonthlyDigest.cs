using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinSight.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationAndMonthlyDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old "Email notifications" switch was marked "coming soon" and did nothing, so it isn't taken as consent to
            // monthly summary emails: the column is replaced and everyone starts with summaries off.
            migrationBuilder.DropColumn(
                name: "Settings_NotificationsEnabled",
                table: "Users");

            migrationBuilder.AddColumn<bool>(
                name: "Settings_MonthlyDigestEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "AutoImportEnabledAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DigestFailedFor",
                table: "Users",
                type: "TEXT",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DigestFailures",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "DigestNextAttemptAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastDigestSentFor",
                table: "Users",
                type: "TEXT",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextAutoScanAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Settings_AutoImportEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Settings_AutoScanEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NextAutoScanAt",
                table: "Users",
                column: "NextAutoScanAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_NextAutoScanAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AutoImportEnabledAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DigestFailedFor",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DigestFailures",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DigestNextAttemptAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastDigestSentFor",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "NextAutoScanAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_AutoImportEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_AutoScanEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Settings_MonthlyDigestEnabled",
                table: "Users");

            migrationBuilder.AddColumn<bool>(
                name: "Settings_NotificationsEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
