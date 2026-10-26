using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MeetingUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meeting_agent_spend",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    cost_cents = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meeting_agent_spend", x => x.day);
                });

            migrationBuilder.CreateTable(
                name: "meeting_usage",
                columns: table => new
                {
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<DateOnly>(type: "date", nullable: false),
                    audio_seconds = table.Column<double>(type: "double precision", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cost_cents = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    warned80at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    warned100at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_meeting_usage", x => new { x.workspace_id, x.period });
                    table.ForeignKey(
                        name: "fk_meeting_usage_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meeting_agent_spend");

            migrationBuilder.DropTable(
                name: "meeting_usage");
        }
    }
}
