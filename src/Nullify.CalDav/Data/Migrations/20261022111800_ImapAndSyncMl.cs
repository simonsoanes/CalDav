using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullify.CalDav.Data.Migrations
{
    /// <inheritdoc />
    public partial class ImapAndSyncMl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "AuthSource",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "ImapHost",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImapLogin",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImapPort",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ImapSecurity",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncMlDevices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    SoftwareVersion = table.Column<string>(type: "TEXT", nullable: true),
                    PreferredTypes = table.Column<string>(type: "TEXT", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMlDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncMlDevices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncMlAnchors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientDatabase = table.Column<string>(type: "TEXT", nullable: false),
                    ServerDatabase = table.Column<string>(type: "TEXT", nullable: false),
                    LastClientAnchor = table.Column<string>(type: "TEXT", nullable: true),
                    LastServerAnchor = table.Column<string>(type: "TEXT", nullable: true),
                    ServerSyncToken = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMlAnchors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncMlAnchors_SyncMlDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "SyncMlDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncMlMappings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AnchorId = table.Column<int>(type: "INTEGER", nullable: false),
                    Luid = table.Column<string>(type: "TEXT", nullable: false),
                    ServerUri = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncMlMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncMlMappings_SyncMlAnchors_AnchorId",
                        column: x => x.AnchorId,
                        principalTable: "SyncMlAnchors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncMlAnchors_DeviceId_ClientDatabase_ServerDatabase",
                table: "SyncMlAnchors",
                columns: new[] { "DeviceId", "ClientDatabase", "ServerDatabase" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMlDevices_UserId_DeviceId",
                table: "SyncMlDevices",
                columns: new[] { "UserId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMlMappings_AnchorId_Luid",
                table: "SyncMlMappings",
                columns: new[] { "AnchorId", "Luid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncMlMappings_AnchorId_ServerUri",
                table: "SyncMlMappings",
                columns: new[] { "AnchorId", "ServerUri" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncMlMappings");

            migrationBuilder.DropTable(
                name: "SyncMlAnchors");

            migrationBuilder.DropTable(
                name: "SyncMlDevices");

            migrationBuilder.DropColumn(
                name: "AuthSource",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ImapHost",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ImapLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ImapPort",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ImapSecurity",
                table: "Users");
        }
    }
}
