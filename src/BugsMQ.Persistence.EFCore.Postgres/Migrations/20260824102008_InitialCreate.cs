using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BugsMQ.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SagaEventLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EntryType = table.Column<int>(type: "integer", nullable: false),
                    FromState = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ToState = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    MessageType = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    MessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PayloadJson = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    TraceId = table.Column<string>(type: "text", nullable: true),
                    SpanId = table.Column<string>(type: "text", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaEventLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SagaInstances",
                columns: table => new
                {
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CurrentState = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaInstances", x => x.CorrelationId);
                });

            migrationBuilder.CreateTable(
                name: "SagaTimeouts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SagaType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ForState = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaTimeouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_CorrelationId_Id",
                table: "SagaEventLog",
                columns: new[] { "CorrelationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_CorrelationId_MessageId",
                table: "SagaEventLog",
                columns: new[] { "CorrelationId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_SagaType_Status",
                table: "SagaInstances",
                columns: new[] { "SagaType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_Status_UpdatedAtUtc",
                table: "SagaInstances",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaTimeouts_Status_DueAtUtc",
                table: "SagaTimeouts",
                columns: new[] { "Status", "DueAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SagaEventLog");

            migrationBuilder.DropTable(
                name: "SagaInstances");

            migrationBuilder.DropTable(
                name: "SagaTimeouts");
        }
    }
}
