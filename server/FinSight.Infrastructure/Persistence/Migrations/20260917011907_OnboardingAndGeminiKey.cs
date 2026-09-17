using System;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace FinSight.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingAndGeminiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EncryptedGeminiApiKey",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeminiApiKeyHint",
                table: "Users",
                type: "TEXT",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OnboardingCompletedAt",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            // Users who already have statements, and demo users, are past onboarding. Timestamps are stored in the
            // DateTimeOffsetToBinaryConverter format (see FinSightDbContext), so the value is encoded the same way.
            var completedAt = (long)new DateTimeOffsetToBinaryConverter().ConvertToProvider(DateTimeOffset.UtcNow)!;
            migrationBuilder.Sql(
                $"""
                UPDATE "Users" SET "OnboardingCompletedAt" = {completedAt.ToString(CultureInfo.InvariantCulture)}
                WHERE "OnboardingCompletedAt" IS NULL
                  AND ("IsDemo" = 1 OR EXISTS (SELECT 1 FROM "Statements" WHERE "Statements"."UserId" = "Users"."Id"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EncryptedGeminiApiKey",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GeminiApiKeyHint",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OnboardingCompletedAt",
                table: "Users");
        }
    }
}
