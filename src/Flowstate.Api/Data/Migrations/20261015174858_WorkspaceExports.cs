using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowstate.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspace_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_exports", x => x.id);
                    table.ForeignKey(
                        name: "fk_workspace_exports_members_requested_by_id",
                        column: x => x.requested_by_id,
                        principalTable: "members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workspace_exports_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_exports_requested_by_id",
                table: "workspace_exports",
                column: "requested_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_exports_state",
                table: "workspace_exports",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_exports_workspace_id_created_at",
                table: "workspace_exports",
                columns: new[] { "workspace_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workspace_exports");
        }
    }
}
