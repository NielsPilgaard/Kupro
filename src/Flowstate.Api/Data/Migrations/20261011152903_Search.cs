using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Flowstate.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Search : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "issues",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('simple', key), 'A') || setweight(to_tsvector('english', title), 'A') || setweight(to_tsvector('danish', title), 'A') || setweight(to_tsvector('english', coalesce(description, '')), 'B') || setweight(to_tsvector('danish', coalesce(description, '')), 'B')",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_issues_search_vector",
                table: "issues",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_issues_search_vector",
                table: "issues");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "issues");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
