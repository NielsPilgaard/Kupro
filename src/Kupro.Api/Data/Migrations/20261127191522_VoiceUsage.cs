using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoiceUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage");

            migrationBuilder.AddColumn<string>(
                name: "feature",
                table: "meeting_usage",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Every row before voice control is the meeting agent's.
                defaultValue: "Meetings");

            migrationBuilder.AddPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage",
                columns: new[] { "workspace_id", "period", "paid_by", "feature" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old key has room for one row per payer: voice usage goes.
            migrationBuilder.Sql("DELETE FROM meeting_usage WHERE feature <> 'Meetings'");

            migrationBuilder.DropPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage");

            migrationBuilder.DropColumn(
                name: "feature",
                table: "meeting_usage");

            migrationBuilder.AddPrimaryKey(
                name: "pk_meeting_usage",
                table: "meeting_usage",
                columns: new[] { "workspace_id", "period", "paid_by" });
        }
    }
}
