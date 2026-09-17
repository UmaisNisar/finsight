using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinSight.Migrations.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GoogleSubject = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsDemo = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    OnboardingCompletedAt = table.Column<long>(type: "bigint", nullable: true),
                    EncryptedGeminiApiKey = table.Column<string>(type: "text", nullable: true),
                    GeminiApiKeyHint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    NextAutoScanAt = table.Column<long>(type: "bigint", nullable: true),
                    AutoImportEnabledAt = table.Column<long>(type: "bigint", nullable: true),
                    LastDigestSentFor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    DigestNextAttemptAt = table.Column<long>(type: "bigint", nullable: true),
                    DigestFailedFor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    DigestFailures = table.Column<int>(type: "integer", nullable: false),
                    Settings_AiCategorizationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Settings_AiInsightsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Settings_AutoImportEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Settings_AutoScanEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Settings_Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Settings_DateFormat = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Settings_MonthlyDigestEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Settings_Theme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomCategories_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FinancialAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    FactsHash = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CorrectionsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FinancialAnalyses_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GmailConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GoogleEmail = table.Column<string>(type: "text", nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "text", nullable: false),
                    Scopes = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectedAt = table.Column<long>(type: "bigint", nullable: false),
                    LastSyncedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GmailConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GmailConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MerchantRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantKey = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    CategoryId = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantRules_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StepsJson = table.Column<string>(type: "text", nullable: false),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessingJobs_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Statements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceMessageId = table.Column<string>(type: "text", nullable: true),
                    SourceThreadId = table.Column<string>(type: "text", nullable: true),
                    SourcePartId = table.Column<string>(type: "text", nullable: true),
                    Subject = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Sender = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReceivedAt = table.Column<long>(type: "bigint", nullable: true),
                    Filename = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ContentHash = table.Column<string>(type: "text", nullable: true),
                    Format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DocumentKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetectionConfidence = table.Column<double>(type: "double precision", nullable: false),
                    DetectionReasons = table.Column<string>(type: "text", nullable: true),
                    Institution = table.Column<string>(type: "text", nullable: true),
                    AccountType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AccountMask = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    Currency = table.Column<string>(type: "text", nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    OpeningBalance = table.Column<long>(type: "bigint", nullable: true),
                    ClosingBalance = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    ExtractionConfidence = table.Column<double>(type: "double precision", nullable: true),
                    ExtractionWarnings = table.Column<string>(type: "text", nullable: true),
                    TransactionCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                    ProcessedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Statements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Statements_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Balance = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExtractionConfidence = table.Column<double>(type: "double precision", nullable: false),
                    Merchant = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    MerchantKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CategoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CategorySource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CategoryConfidence = table.Column<double>(type: "double precision", nullable: false),
                    IsRefund = table.Column<bool>(type: "boolean", nullable: false),
                    IsReversal = table.Column<bool>(type: "boolean", nullable: false),
                    TransferPairId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserCategoryId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserMerchant = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    UserType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsExcluded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transactions_Statements_StatementId",
                        column: x => x.StatementId,
                        principalTable: "Statements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Transactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomCategories_UserId_CategoryId",
                table: "CustomCategories",
                columns: new[] { "UserId", "CategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinancialAnalyses_UserId_PeriodStart_PeriodEnd",
                table: "FinancialAnalyses",
                columns: new[] { "UserId", "PeriodStart", "PeriodEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_GmailConnections_UserId",
                table: "GmailConnections",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantRules_UserId_MerchantKey",
                table: "MerchantRules",
                columns: new[] { "UserId", "MerchantKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_UserId_CreatedAt",
                table: "ProcessingJobs",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Statements_UserId_ContentHash",
                table: "Statements",
                columns: new[] { "UserId", "ContentHash" });

            migrationBuilder.CreateIndex(
                name: "IX_Statements_UserId_SourceKey",
                table: "Statements",
                columns: new[] { "UserId", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_StatementId",
                table: "Transactions",
                column: "StatementId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_Date",
                table: "Transactions",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_Fingerprint",
                table: "Transactions",
                columns: new[] { "UserId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId_MerchantKey",
                table: "Transactions",
                columns: new[] { "UserId", "MerchantKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_GoogleSubject",
                table: "Users",
                column: "GoogleSubject",
                unique: true,
                filter: "\"GoogleSubject\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NextAutoScanAt",
                table: "Users",
                column: "NextAutoScanAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                table: "UserSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomCategories");

            migrationBuilder.DropTable(
                name: "FinancialAnalyses");

            migrationBuilder.DropTable(
                name: "GmailConnections");

            migrationBuilder.DropTable(
                name: "MerchantRules");

            migrationBuilder.DropTable(
                name: "ProcessingJobs");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "Statements");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
