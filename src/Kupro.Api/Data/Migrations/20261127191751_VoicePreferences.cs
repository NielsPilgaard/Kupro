using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoicePreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "voice_auto_apply",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "voice_button",
                table: "users",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "TopBar");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "voice_auto_apply",
                table: "users");

            migrationBuilder.DropColumn(
                name: "voice_button",
                table: "users");
        }
    }
}
