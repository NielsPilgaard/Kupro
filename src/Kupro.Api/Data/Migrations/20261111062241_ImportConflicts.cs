using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "level",
                table: "imported_items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_parent",
                table: "imported_items",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_conflicts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kupro_value = table.Column<string>(type: "jsonb", nullable: true),
                    source_value = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_conflicts", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_conflicts_imports_workspace_id_import_id",
                        columns: x => new { x.workspace_id, x.import_id },
                        principalTable: "imports",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_import_conflicts_issues_workspace_id_issue_id",
                        columns: x => new { x.workspace_id, x.issue_id },
                        principalTable: "issues",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_conflicts_workspace_id_import_id_issue_id_field",
                table: "import_conflicts",
                columns: new[] { "workspace_id", "import_id", "issue_id", "field" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_conflicts_workspace_id_import_id_state",
                table: "import_conflicts",
                columns: new[] { "workspace_id", "import_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_import_conflicts_workspace_id_issue_id",
                table: "import_conflicts",
                columns: new[] { "workspace_id", "issue_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_conflicts");

            migrationBuilder.DropColumn(
                name: "level",
                table: "imported_items");

            migrationBuilder.DropColumn(
                name: "source_parent",
                table: "imported_items");
        }
    }
}
