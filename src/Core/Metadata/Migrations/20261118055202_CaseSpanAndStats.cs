using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class CaseSpanAndStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "future_rows",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "undated_rows",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "future_rows",
                table: "cases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "ts_max",
                table: "cases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ts_min",
                table: "cases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "undated_rows",
                table: "cases",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "case_stats",
                columns: table => new
                {
                    case_id = table.Column<long>(type: "INTEGER", nullable: false),
                    dirty_since = table.Column<DateTime>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    message = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    computed_at = table.Column<DateTime>(type: "TEXT", nullable: true),
                    events = table.Column<long>(type: "INTEGER", nullable: false),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    finished_at = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_stats", x => x.case_id);
                    table.CheckConstraint("ck_case_stats_state", "state IN ('none', 'ok', 'skipped', 'timed_out', 'failed')");
                    table.ForeignKey(
                        name: "fk_case_stats_cases_case_id",
                        column: x => x.case_id,
                        principalTable: "cases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Cases parsed before the span was recorded: taken from their file records, times below the floor
            // (EventTime.Floor) left out. The two counts of such files stay 0 until a re-parse counts them.
            migrationBuilder.Sql(
                "UPDATE cases SET ts_min = (SELECT MIN(f.ts_min) FROM files f WHERE f.case_id = cases.id AND f.ts_min >= '1980-01-01'), " +
                "ts_max = (SELECT MAX(f.ts_max) FROM files f WHERE f.case_id = cases.id AND f.ts_max >= '1980-01-01');");

            // And marked, so the runner computes their field statistics once.
            migrationBuilder.Sql(
                "INSERT INTO case_stats (case_id, dirty_since, state, events, duration_ms) " +
                "SELECT DISTINCT f.case_id, '2026-01-01 00:00:00', 'none', 0, 0 FROM files f WHERE f.row_count > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_stats");

            migrationBuilder.DropColumn(
                name: "future_rows",
                table: "files");

            migrationBuilder.DropColumn(
                name: "undated_rows",
                table: "files");

            migrationBuilder.DropColumn(
                name: "future_rows",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ts_max",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "ts_min",
                table: "cases");

            migrationBuilder.DropColumn(
                name: "undated_rows",
                table: "cases");
        }
    }
}
