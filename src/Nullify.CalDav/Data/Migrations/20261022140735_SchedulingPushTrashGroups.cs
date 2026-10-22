using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nullify.CalDav.Data.Migrations
{
    /// <inheritdoc />
    public partial class SchedulingPushTrashGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "QuotaBytes",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ShareeId",
                table: "CalendarShares",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "CalendarShares",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "Calendars",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<bool>(
                name: "PublicBusyOnly",
                table: "Calendars",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PublicToken",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionETag",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionError",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionLastFetchUtc",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionRefreshMinutes",
                table: "Calendars",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionUrl",
                table: "Calendars",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ScheduleTag",
                table: "CalendarObjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ShareeId",
                table: "AddressBookShares",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "AddressBookShares",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Kind",
                table: "AddressBooks",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateTable(
                name: "Delegations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    DelegateId = table.Column<int>(type: "INTEGER", nullable: false),
                    CanWrite = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Delegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Delegations_Users_DelegateId",
                        column: x => x.DelegateId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Delegations_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, collation: "NOCASE"),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushRegistrations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionType = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    PushResource = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", nullable: true),
                    AuthSecret = table.Column<string>(type: "TEXT", nullable: true),
                    ContentUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    PropertyUpdate = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushRegistrations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TrashItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<byte>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    CollectionName = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Uri = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrashItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrashItems_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarShares_CalendarId_GroupId",
                table: "CalendarShares",
                columns: new[] { "CalendarId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarShares_GroupId",
                table: "CalendarShares",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_PublicToken",
                table: "Calendars",
                column: "PublicToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AddressBookShares_AddressBookId_GroupId",
                table: "AddressBookShares",
                columns: new[] { "AddressBookId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AddressBookShares_GroupId",
                table: "AddressBookShares",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_DelegateId",
                table: "Delegations",
                column: "DelegateId");

            migrationBuilder.CreateIndex(
                name: "IX_Delegations_OwnerId_DelegateId",
                table: "Delegations",
                columns: new[] { "OwnerId", "DelegateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserId",
                table: "GroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_Name",
                table: "Groups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushRegistrations_CollectionType_CollectionId",
                table: "PushRegistrations",
                columns: new[] { "CollectionType", "CollectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PushRegistrations_PushResource",
                table: "PushRegistrations",
                column: "PushResource");

            migrationBuilder.CreateIndex(
                name: "IX_PushRegistrations_UserId",
                table: "PushRegistrations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TrashItems_OwnerId_DeletedUtc",
                table: "TrashItems",
                columns: new[] { "OwnerId", "DeletedUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_AddressBookShares_Groups_GroupId",
                table: "AddressBookShares",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_CalendarShares_Groups_GroupId",
                table: "CalendarShares",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AddressBookShares_Groups_GroupId",
                table: "AddressBookShares");

            migrationBuilder.DropForeignKey(
                name: "FK_CalendarShares_Groups_GroupId",
                table: "CalendarShares");

            migrationBuilder.DropTable(
                name: "Delegations");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "PushRegistrations");

            migrationBuilder.DropTable(
                name: "TrashItems");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropIndex(
                name: "IX_CalendarShares_CalendarId_GroupId",
                table: "CalendarShares");

            migrationBuilder.DropIndex(
                name: "IX_CalendarShares_GroupId",
                table: "CalendarShares");

            migrationBuilder.DropIndex(
                name: "IX_Calendars_PublicToken",
                table: "Calendars");

            migrationBuilder.DropIndex(
                name: "IX_AddressBookShares_AddressBookId_GroupId",
                table: "AddressBookShares");

            migrationBuilder.DropIndex(
                name: "IX_AddressBookShares_GroupId",
                table: "AddressBookShares");

            migrationBuilder.DropColumn(
                name: "QuotaBytes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "CalendarShares");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "PublicBusyOnly",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "PublicToken",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SubscriptionETag",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SubscriptionError",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SubscriptionLastFetchUtc",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SubscriptionRefreshMinutes",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "SubscriptionUrl",
                table: "Calendars");

            migrationBuilder.DropColumn(
                name: "ScheduleTag",
                table: "CalendarObjects");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "AddressBookShares");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AddressBooks");

            migrationBuilder.AlterColumn<int>(
                name: "ShareeId",
                table: "CalendarShares",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ShareeId",
                table: "AddressBookShares",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
