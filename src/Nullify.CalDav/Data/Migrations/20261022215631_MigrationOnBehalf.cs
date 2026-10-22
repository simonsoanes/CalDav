using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullify.CalDav.Data.Migrations
{
    /// <inheritdoc />
    public partial class MigrationOnBehalf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowPrivateNetworks",
                table: "MigrationSources",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "MigrationSources",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowPrivateNetworks",
                table: "MigrationSources");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "MigrationSources");
        }
    }
}
