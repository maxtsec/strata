using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strata.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Nullable first — existing rows have no value yet.
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Folders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "DocumentShares",
                type: "uniqueidentifier",
                nullable: true);

            // 2. Folders derive their tenant from their owner. Every
            // ApplicationUser already has a required TenantId (since the
            // ApplicationUser-Tenant migration), so this is the source of
            // truth — not a Legacy Tenant, not Guid.Empty.
            migrationBuilder.Sql(@"
                UPDATE f
                SET f.TenantId = u.TenantId
                FROM [Folders] f
                INNER JOIN [AspNetUsers] u ON f.OwnerId = u.Id
                WHERE f.TenantId IS NULL;
            ");

            // 3. Documents derive their tenant from their owner the same way.
            migrationBuilder.Sql(@"
                UPDATE d
                SET d.TenantId = u.TenantId
                FROM [Documents] d
                INNER JOIN [AspNetUsers] u ON d.OwnerId = u.Id
                WHERE d.TenantId IS NULL;
            ");

            // 4. DocumentShares derive their tenant from the document being
            // shared — not the recipient. Must run after Documents is
            // backfilled (step 3), since it reads Documents.TenantId.
            migrationBuilder.Sql(@"
                UPDATE s
                SET s.TenantId = doc.TenantId
                FROM [DocumentShares] s
                INNER JOIN [Documents] doc ON s.DocumentId = doc.Id
                WHERE s.TenantId IS NULL;
            ");

            // 5. Fail closed rather than silently leaving (or inventing) a
            // tenant for any row the joins above couldn't reach — an orphaned
            // OwnerId/DocumentId would otherwise slip through as a NULL that
            // step 9 turns into a runtime constraint violation with a much
            // less informative message.
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [Folders] WHERE [TenantId] IS NULL)
                BEGIN
                    RAISERROR('AddTenantIdToResources migration failed: one or more Folders rows could not be backfilled with a TenantId (orphaned OwnerId).', 16, 1);
                END
            ");

            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [Documents] WHERE [TenantId] IS NULL)
                BEGIN
                    RAISERROR('AddTenantIdToResources migration failed: one or more Documents rows could not be backfilled with a TenantId (orphaned OwnerId).', 16, 1);
                END
            ");

            migrationBuilder.Sql(@"
                IF EXISTS (SELECT 1 FROM [DocumentShares] WHERE [TenantId] IS NULL)
                BEGIN
                    RAISERROR('AddTenantIdToResources migration failed: one or more DocumentShares rows could not be backfilled with a TenantId (orphaned DocumentId).', 16, 1);
                END
            ");

            // 6. Historical consistency checks. These are not the same-tenant
            // *recipient* enforcement (deliberately not added here) — they
            // check that the derived tenants line up with each other along
            // relationships that were always supposed to stay inside one
            // tenant: a folder tree, and a document inside its folder.
            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1 FROM [Folders] child
                    INNER JOIN [Folders] parent ON child.ParentFolderId = parent.Id
                    WHERE child.TenantId <> parent.TenantId
                )
                BEGIN
                    RAISERROR('AddTenantIdToResources migration failed: a child folder''s derived TenantId does not match its parent folder''s TenantId.', 16, 1);
                END
            ");

            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1 FROM [Documents] d
                    INNER JOIN [Folders] f ON d.FolderId = f.Id
                    WHERE d.TenantId <> f.TenantId
                )
                BEGIN
                    RAISERROR('AddTenantIdToResources migration failed: a document''s derived TenantId does not match its folder''s TenantId.', 16, 1);
                END
            ");

            // Deliberately no check comparing a DocumentShare's TenantId to
            // its recipient's TenantId: same-tenant sharing was never
            // enforced, so an existing cross-tenant recipient is expected
            // and must not fail this migration. The share's TenantId stays
            // the document's, regardless of who it was shared with.

            // 7. Every row now has a value — require one going forward.
            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Folders",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "Documents",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "DocumentShares",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_TenantId",
                table: "Folders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId",
                table: "Documents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_TenantId",
                table: "DocumentShares",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Tenants_TenantId",
                table: "Folders",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Tenants_TenantId",
                table: "Documents",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentShares_Tenants_TenantId",
                table: "DocumentShares",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DocumentShares_Tenants_TenantId",
                table: "DocumentShares");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Tenants_TenantId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Tenants_TenantId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_DocumentShares_TenantId",
                table: "DocumentShares");

            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Folders_TenantId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "DocumentShares");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Folders");

            // No Tenant or ApplicationUser rows are touched — this migration
            // never created a sentinel tenant, only derived values from
            // relationships that already existed.
        }
    }
}
