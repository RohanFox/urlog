using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class CasesApi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_case_id",
                table: "jobs");

            migrationBuilder.AddColumn<int>(
                name: "settings_version",
                table: "jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "merged_into_id",
                table: "hosts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_options",
                table: "hosts",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "hosts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "detection_registry",
                table: "files",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parsed_settings_version",
                table: "files",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "settings_version",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_case_id_state",
                table: "jobs",
                columns: new[] { "case_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_hosts_merged_into_id",
                table: "hosts",
                column: "merged_into_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_jobs_case_id_state",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_hosts_merged_into_id",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "settings_version",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "merged_into_id",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "source_options",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "version",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "detection_registry",
                table: "files");

            migrationBuilder.DropColumn(
                name: "parsed_settings_version",
                table: "files");

            migrationBuilder.DropColumn(
                name: "settings_version",
                table: "files");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_case_id",
                table: "jobs",
                column: "case_id");
        }
    }
}
