using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Strata.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSharesUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentShares_DocumentId",
                table: "DocumentShares");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_DocumentId_UserId",
                table: "DocumentShares",
                columns: new[] { "DocumentId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DocumentShares_DocumentId_UserId",
                table: "DocumentShares");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_DocumentId",
                table: "DocumentShares",
                column: "DocumentId");
        }
    }
}
