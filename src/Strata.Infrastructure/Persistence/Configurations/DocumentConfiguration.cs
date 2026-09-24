// Strata.Infrastructure/Persistence/Configurations/DocumentConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strata.Domain.Documents;
using Strata.Domain.Tenancy;
using Strata.Infrastructure.Identity;

namespace Strata.Infrastructure.Persistence.Configurations;

public class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        // DocumentShare references documents by (Id, TenantId), which makes
        // the share's tenant agree with the document's tenant in the database.
        builder.HasAlternateKey(document => new { document.Id, document.TenantId });

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(d => new { d.OwnerId, d.TenantId })
            .HasPrincipalKey(user => new { user.Id, user.TenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(d => new { d.FolderId, d.TenantId })
            .HasPrincipalKey(folder => new { folder.Id, folder.TenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.TenantId);
    }
}
