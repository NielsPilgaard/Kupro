using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kupro.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImportData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "members",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "placeholder_email",
                table: "members",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    base_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    encrypted_token = table.Column<string>(type: "text", nullable: true),
                    mapping = table.Column<string>(type: "jsonb", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    auto_sync = table.Column<bool>(type: "boolean", nullable: false),
                    cursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_change_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imports", x => x.id);
                    table.UniqueConstraint("ak_imports_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.ForeignKey(
                        name: "fk_imports_members_workspace_id_created_by_id",
                        columns: x => new { x.workspace_id, x.created_by_id },
                        principalTable: "members",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_imports_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "import_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    counts = table.Column<string>(type: "jsonb", nullable: true),
                    report = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_runs_imports_workspace_id_import_id",
                        columns: x => new { x.workspace_id, x.import_id },
                        principalTable: "imports",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "imported_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    entity_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_values = table.Column<string>(type: "jsonb", nullable: true),
                    seen_change_mark = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_imported_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_imported_items_imports_workspace_id_import_id",
                        columns: x => new { x.workspace_id, x.import_id },
                        principalTable: "imports",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "issue_aliases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_issue_aliases", x => x.id);
                    table.ForeignKey(
                        name: "fk_issue_aliases_imports_workspace_id_import_id",
                        columns: x => new { x.workspace_id, x.import_id },
                        principalTable: "imports",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_issue_aliases_issues_workspace_id_issue_id",
                        columns: x => new { x.workspace_id, x.issue_id },
                        principalTable: "issues",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_runs_state",
                table: "import_runs",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_import_runs_workspace_id_import_id_created_at",
                table: "import_runs",
                columns: new[] { "workspace_id", "import_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_imported_items_workspace_id_entity_kind_entity_id",
                table: "imported_items",
                columns: new[] { "workspace_id", "entity_kind", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_imported_items_workspace_id_import_id_source_kind_source_id",
                table: "imported_items",
                columns: new[] { "workspace_id", "import_id", "source_kind", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_imports_auto_sync",
                table: "imports",
                column: "auto_sync",
                filter: "auto_sync");

            migrationBuilder.CreateIndex(
                name: "ix_imports_last_run_at",
                table: "imports",
                column: "last_run_at",
                filter: "encrypted_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_imports_workspace_id_created_at",
                table: "imports",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_imports_workspace_id_created_by_id",
                table: "imports",
                columns: new[] { "workspace_id", "created_by_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_aliases_workspace_id_alias",
                table: "issue_aliases",
                columns: new[] { "workspace_id", "alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_issue_aliases_workspace_id_import_id",
                table: "issue_aliases",
                columns: new[] { "workspace_id", "import_id" });

            migrationBuilder.CreateIndex(
                name: "ix_issue_aliases_workspace_id_issue_id",
                table: "issue_aliases",
                columns: new[] { "workspace_id", "issue_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_runs");

            migrationBuilder.DropTable(
                name: "imported_items");

            migrationBuilder.DropTable(
                name: "issue_aliases");

            migrationBuilder.DropTable(
                name: "imports");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "members");

            migrationBuilder.DropColumn(
                name: "placeholder_email",
                table: "members");
        }
    }
}
