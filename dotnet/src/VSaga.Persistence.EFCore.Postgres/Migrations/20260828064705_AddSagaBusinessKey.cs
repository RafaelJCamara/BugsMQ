using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaBusinessKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessKey",
                table: "SagaInstances",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_SagaType_BusinessKey",
                table: "SagaInstances",
                columns: new[] { "SagaType", "BusinessKey" },
                unique: true,
                filter: "\"BusinessKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaInstances_SagaType_BusinessKey",
                table: "SagaInstances");

            migrationBuilder.DropColumn(
                name: "BusinessKey",
                table: "SagaInstances");
        }
    }
}
