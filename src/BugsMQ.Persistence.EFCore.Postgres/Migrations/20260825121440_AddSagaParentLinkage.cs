using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BugsMQ.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaParentLinkage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentCorrelationId",
                table: "SagaInstances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentSagaType",
                table: "SagaInstances",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_ParentSagaType_ParentCorrelationId",
                table: "SagaInstances",
                columns: new[] { "ParentSagaType", "ParentCorrelationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaInstances_ParentSagaType_ParentCorrelationId",
                table: "SagaInstances");

            migrationBuilder.DropColumn(
                name: "ParentCorrelationId",
                table: "SagaInstances");

            migrationBuilder.DropColumn(
                name: "ParentSagaType",
                table: "SagaInstances");
        }
    }
}
