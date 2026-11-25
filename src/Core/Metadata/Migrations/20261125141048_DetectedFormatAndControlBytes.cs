using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Urlog.Core.Metadata.Migrations
{
    /// <inheritdoc />
    public partial class DetectedFormatAndControlBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "detected_control_bytes",
                table: "files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "detected_format",
                table: "files",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "detected_control_bytes",
                table: "files");

            migrationBuilder.DropColumn(
                name: "detected_format",
                table: "files");
        }
    }
}
