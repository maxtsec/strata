using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strata.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSameTenantOwnershipAndFolderRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing single-column foreign keys guarantee the related rows
            // exist. Check their tenant labels before replacing those keys so
            // deployment fails with an actionable message instead of deleting
            // or rewriting inconsistent data.
            migrationBuilder.Sql(@"
                SET XACT_ABORT ON;

                IF EXISTS (
                    SELECT 1
                    FROM [Folders] AS [folder]
                    INNER JOIN [AspNetUsers] AS [owner]
                        ON [folder].[OwnerId] = [owner].[Id]
                    WHERE [folder].[TenantId] <> [owner].[TenantId]
                )
                BEGIN
                    ;THROW 51000, 'EnforceSameTenantOwnershipAndFolderRelationships migration failed: a folder owner belongs to another tenant.', 1;
                END
            ");

            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1
                    FROM [Folders] AS [child]
                    INNER JOIN [Folders] AS [parent]
                        ON [child].[ParentFolderId] = [parent].[Id]
                    WHERE [child].[TenantId] <> [parent].[TenantId]
                )
                BEGIN
                    ;THROW 51000, 'EnforceSameTenantOwnershipAndFolderRelationships migration failed: a folder parent belongs to another tenant.', 1;
                END
            ");

            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1
                    FROM [Documents] AS [document]
                    INNER JOIN [AspNetUsers] AS [owner]
                        ON [document].[OwnerId] = [owner].[Id]
                    WHERE [document].[TenantId] <> [owner].[TenantId]
                )
                BEGIN
                    ;THROW 51000, 'EnforceSameTenantOwnershipAndFolderRelationships migration failed: a document owner belongs to another tenant.', 1;
                END
            ");

            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1
                    FROM [Documents] AS [document]
                    INNER JOIN [Folders] AS [folder]
                        ON [document].[FolderId] = [folder].[Id]
                    WHERE [document].[TenantId] <> [folder].[TenantId]
                )
                BEGIN
                    ;THROW 51000, 'EnforceSameTenantOwnershipAndFolderRelationships migration failed: a document folder belongs to another tenant.', 1;
                END
            ");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_AspNetUsers_OwnerId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Folders_FolderId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_AspNetUsers_OwnerId",
                table: "Folders");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_OwnerId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_ParentFolderId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Documents_FolderId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_OwnerId",
                table: "Documents");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Folders_Id_TenantId",
                table: "Folders",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OwnerId_TenantId",
                table: "Folders",
                columns: new[] { "OwnerId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentFolderId_TenantId",
                table: "Folders",
                columns: new[] { "ParentFolderId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_FolderId_TenantId",
                table: "Documents",
                columns: new[] { "FolderId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_OwnerId_TenantId",
                table: "Documents",
                columns: new[] { "OwnerId", "TenantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_AspNetUsers_OwnerId_TenantId",
                table: "Documents",
                columns: new[] { "OwnerId", "TenantId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Folders_FolderId_TenantId",
                table: "Documents",
                columns: new[] { "FolderId", "TenantId" },
                principalTable: "Folders",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_AspNetUsers_OwnerId_TenantId",
                table: "Folders",
                columns: new[] { "OwnerId", "TenantId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Folders_ParentFolderId_TenantId",
                table: "Folders",
                columns: new[] { "ParentFolderId", "TenantId" },
                principalTable: "Folders",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Documents_AspNetUsers_OwnerId_TenantId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_Documents_Folders_FolderId_TenantId",
                table: "Documents");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_AspNetUsers_OwnerId_TenantId",
                table: "Folders");

            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Folders_ParentFolderId_TenantId",
                table: "Folders");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Folders_Id_TenantId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_OwnerId_TenantId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Folders_ParentFolderId_TenantId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Documents_FolderId_TenantId",
                table: "Documents");

            migrationBuilder.DropIndex(
                name: "IX_Documents_OwnerId_TenantId",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_OwnerId",
                table: "Folders",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_FolderId",
                table: "Documents",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_OwnerId",
                table: "Documents",
                column: "OwnerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_AspNetUsers_OwnerId",
                table: "Documents",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Folders_FolderId",
                table: "Documents",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_AspNetUsers_OwnerId",
                table: "Folders",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
