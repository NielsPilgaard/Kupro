using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AiConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage");

            // Every row so far was spent on Kupro's key, within the budget (ADR-022 decision 5).
            migrationBuilder.AddColumn<string>(
                name: "paid_by",
                table: "meeting_usage",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Kupro");

            migrationBuilder.AddPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage",
                columns: new[] { "workspace_id", "period", "paid_by" });

            migrationBuilder.CreateTable(
                name: "ai_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    encrypted_key = table.Column<string>(type: "text", nullable: false),
                    key_hint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    eu_confirmed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    eu_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_connections", x => x.id);
                    table.UniqueConstraint("ak_ai_connections_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "fk_ai_connections_members_workspace_id_created_by_id",
                        columns: x => new { x.workspace_id, x.created_by_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_connections_members_workspace_id_eu_confirmed_by_id",
                        columns: x => new { x.workspace_id, x.eu_confirmed_by_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ai_connections_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_routes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_routes", x => x.id);
                    table.ForeignKey(
                        name: "fk_ai_routes_ai_connections_workspace_id_connection_id",
                        columns: x => new { x.workspace_id, x.connection_id },
                        principalTable: "ai_connections",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ai_routes_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_connections_workspace_id",
                table: "ai_connections",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_connections_workspace_id_created_by_id",
                table: "ai_connections",
                columns: new[] { "workspace_id", "created_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_connections_workspace_id_eu_confirmed_by_id",
                table: "ai_connections",
                columns: new[] { "workspace_id", "eu_confirmed_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_routes_workspace_id_capability_language",
                table: "ai_routes",
                columns: new[] { "workspace_id", "capability", "language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_routes_workspace_id_connection_id",
                table: "ai_routes",
                columns: new[] { "workspace_id", "connection_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_routes");

            migrationBuilder.DropTable(
                name: "ai_connections");

            migrationBuilder.DropPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage");

            migrationBuilder.DropColumn(
                name: "paid_by",
                table: "meeting_usage");

            migrationBuilder.AddPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage",
                columns: new[] { "workspace_id", "period" });
        }
    }
}
