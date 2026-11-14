using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class CaseTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "verified_at",
                table: "files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "case_tasks",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false),
                    case_id = table.Column<long>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    step = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    requested_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    requested_by_id = table.Column<long>(type: "INTEGER", nullable: true),
                    confirmed_by = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    confirmed_by_id = table.Column<long>(type: "INTEGER", nullable: true),
                    backup_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    archive_directory = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    tar_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    manifest_sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTime>(type: "TEXT", nullable: false),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_tasks", x => x.id);
                    table.CheckConstraint("ck_case_tasks_kind", "kind IN ('purge', 'archive', 'reimport')");
                    table.CheckConstraint("ck_case_tasks_state", "state IN ('requested', 'running', 'committed', 'done', 'failed', 'withdrawn')");
                    table.ForeignKey(
                        name: "fk_case_tasks_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_tasks_case_id_state",
                table: "case_tasks",
                columns: new[] { "case_id", "state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_tasks");

            migrationBuilder.DropColumn(
                name: "verified_at",
                table: "files");
        }
    }
}
