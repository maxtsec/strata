using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strata.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddApplicationUserTenantId : Migration
    {
        // Fixed on purpose: the same value in every environment this
        // migration runs against, so it's recognizable in code, in the
        // database, and in the Down migration below. Not Guid.Empty —
        // that value is reserved for "uninitialized", not "a real tenant".
        private static readonly Guid LegacyTenantId = new("00000000-0000-0000-0000-000000000001");
        private static readonly DateTimeOffset LegacyTenantCreatedAt = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. A deterministic tenant to hold every user who existed
            // before tenancy did. Exists only to preserve Phase 1 data —
            // no new user is ever assigned to it after this migration runs.
            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "Name", "CreatedAt" },
                values: new object[] { LegacyTenantId, "Legacy Tenant", LegacyTenantCreatedAt });

            // 2. Nullable first — existing rows have no value yet, and a
            // NOT NULL column can't be added directly against them.
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: true);

            // 3. Backfill every existing user onto the legacy tenant. No
            // permanent database default is used for this — this UPDATE
            // runs once, here, not as a standing DEFAULT constraint that
            // would silently catch future inserts that forgot to set
            // TenantId.
            migrationBuilder.Sql(
                $"UPDATE [AspNetUsers] SET [TenantId] = '{LegacyTenantId}' WHERE [TenantId] IS NULL;");

            // 4. Now that every row has a value, require one going forward.
            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "AspNetUsers",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_Tenants_TenantId",
                table: "AspNetUsers",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_Tenants_TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_TenantId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AspNetUsers");

            // Only the tenant this migration itself created — never a
            // blanket delete of the Tenants table.
            migrationBuilder.DeleteData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: LegacyTenantId);
        }
    }
}
