using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullify.CalDav.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServerMigration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MigrationSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ServerUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPassword = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    ItemsTotal = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsDone = table.Column<int>(type: "INTEGER", nullable: false),
                    ItemsFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastRunUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationSources_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationCollections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RemoteUrl = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Color = table.Column<string>(type: "TEXT", nullable: true),
                    Components = table.Column<string>(type: "TEXT", nullable: true),
                    Selected = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalCalendarId = table.Column<int>(type: "INTEGER", nullable: true),
                    LocalAddressBookId = table.Column<int>(type: "INTEGER", nullable: true),
                    ItemsImported = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationCollections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationCollections_MigrationSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "MigrationSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MigrationCollections_SourceId_RemoteUrl",
                table: "MigrationCollections",
                columns: new[] { "SourceId", "RemoteUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MigrationSources_UserId",
                table: "MigrationSources",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MigrationCollections");

            migrationBuilder.DropTable(
                name: "MigrationSources");
        }
    }
}
