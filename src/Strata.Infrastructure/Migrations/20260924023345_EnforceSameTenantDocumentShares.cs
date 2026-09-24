using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strata.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSameTenantDocumentShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Earlier releases allowed a share to target a user in another
            // tenant. Never delete or silently rewrite those rows during a
            // schema migration: stop with an actionable error so they can be
            // reviewed before this constraint is applied.
            migrationBuilder.Sql(@"
                IF EXISTS (
                    SELECT 1
                    FROM [DocumentShares] AS [share]
                    INNER JOIN [Documents] AS [document]
                        ON [share].[DocumentId] = [document].[Id]
                    INNER JOIN [AspNetUsers] AS [recipient]
                        ON [share].[UserId] = [recipient].[Id]
                    WHERE [share].[TenantId] <> [document].[TenantId]
                       OR [share].[TenantId] <> [recipient].[TenantId]
                )
                BEGIN
                    RAISERROR('EnforceSameTenantDocumentShares migration failed: existing shares cross tenant boundaries. Review and remove or correct those rows before retrying.', 16, 1);
                END
            ");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentShares_AspNetUsers_UserId",
                table: "DocumentShares");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentShares_Documents_DocumentId",
                table: "DocumentShares");

            migrationBuilder.DropIndex(
                name: "IX_DocumentShares_UserId",
                table: "DocumentShares");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Documents_Id_TenantId",
                table: "Documents",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AspNetUsers_Id_TenantId",
                table: "AspNetUsers",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_DocumentId_TenantId",
                table: "DocumentShares",
                columns: new[] { "DocumentId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_UserId_TenantId",
                table: "DocumentShares",
                columns: new[] { "UserId", "TenantId" });

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentShares_AspNetUsers_UserId_TenantId",
                table: "DocumentShares",
                columns: new[] { "UserId", "TenantId" },
                principalTable: "AspNetUsers",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentShares_Documents_DocumentId_TenantId",
                table: "DocumentShares",
                columns: new[] { "DocumentId", "TenantId" },
                principalTable: "Documents",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DocumentShares_AspNetUsers_UserId_TenantId",
                table: "DocumentShares");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentShares_Documents_DocumentId_TenantId",
                table: "DocumentShares");

            migrationBuilder.DropIndex(
                name: "IX_DocumentShares_DocumentId_TenantId",
                table: "DocumentShares");

            migrationBuilder.DropIndex(
                name: "IX_DocumentShares_UserId_TenantId",
                table: "DocumentShares");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Documents_Id_TenantId",
                table: "Documents");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AspNetUsers_Id_TenantId",
                table: "AspNetUsers");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_UserId",
                table: "DocumentShares",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentShares_AspNetUsers_UserId",
                table: "DocumentShares",
                column: "UserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentShares_Documents_DocumentId",
                table: "DocumentShares",
                column: "DocumentId",
                principalTable: "Documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
