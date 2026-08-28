using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaOutboxMessageIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SagaOutboxMessages_MessageId",
                table: "SagaOutboxMessages",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaOutboxMessages_MessageId",
                table: "SagaOutboxMessages");
        }
    }
}
