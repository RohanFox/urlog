using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class DetectionRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detected_kind",
                table: "files",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detection",
                table: "files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "file_time",
                table: "files",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_files_detected_kind",
                table: "files",
                sql: "detected_kind IN ('log', 'container', 'artifact', 'unparsed', 'rejected')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_files_detected_kind",
                table: "files");

            migrationBuilder.DropColumn(
                name: "detected_kind",
                table: "files");

            migrationBuilder.DropColumn(
                name: "detection",
                table: "files");

            migrationBuilder.DropColumn(
                name: "file_time",
                table: "files");
        }
    }
}
