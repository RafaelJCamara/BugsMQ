using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ScopeSagaIdentityToSagaTypeAndCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SagaInstances",
                table: "SagaInstances");

            migrationBuilder.DropIndex(
                name: "IX_SagaEventLog_CorrelationId_Id",
                table: "SagaEventLog");

            migrationBuilder.DropIndex(
                name: "IX_SagaEventLog_CorrelationId_MessageId",
                table: "SagaEventLog");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SagaInstances",
                table: "SagaInstances",
                columns: new[] { "SagaType", "CorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_CorrelationId",
                table: "SagaInstances",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_SagaType_CorrelationId_Id",
                table: "SagaEventLog",
                columns: new[] { "SagaType", "CorrelationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_SagaType_CorrelationId_MessageId",
                table: "SagaEventLog",
                columns: new[] { "SagaType", "CorrelationId", "MessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SagaInstances",
                table: "SagaInstances");

            migrationBuilder.DropIndex(
                name: "IX_SagaInstances_CorrelationId",
                table: "SagaInstances");

            migrationBuilder.DropIndex(
                name: "IX_SagaEventLog_SagaType_CorrelationId_Id",
                table: "SagaEventLog");

            migrationBuilder.DropIndex(
                name: "IX_SagaEventLog_SagaType_CorrelationId_MessageId",
                table: "SagaEventLog");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SagaInstances",
                table: "SagaInstances",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_CorrelationId_Id",
                table: "SagaEventLog",
                columns: new[] { "CorrelationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_CorrelationId_MessageId",
                table: "SagaEventLog",
                columns: new[] { "CorrelationId", "MessageId" });
        }
    }
}
