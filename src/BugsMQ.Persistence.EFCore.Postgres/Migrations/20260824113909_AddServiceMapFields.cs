using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BugsMQ.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceMapFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CausationId",
                table: "SagaEventLog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationService",
                table: "SagaEventLog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceService",
                table: "SagaEventLog",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SagaConsumerRegistrations",
                columns: table => new
                {
                    ServiceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MessageType = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    QueueName = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaConsumerRegistrations", x => new { x.ServiceName, x.MessageType });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SagaConsumerRegistrations_MessageType",
                table: "SagaConsumerRegistrations",
                column: "MessageType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SagaConsumerRegistrations");

            migrationBuilder.DropColumn(
                name: "CausationId",
                table: "SagaEventLog");

            migrationBuilder.DropColumn(
                name: "DestinationService",
                table: "SagaEventLog");

            migrationBuilder.DropColumn(
                name: "SourceService",
                table: "SagaEventLog");
        }
    }
}
