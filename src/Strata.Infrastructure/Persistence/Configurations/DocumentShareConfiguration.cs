using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strata.Domain.Documents;
using Strata.Domain.Tenancy;
using Strata.Infrastructure.Identity;

namespace Strata.Infrastructure.Persistence.Configurations;

public class DocumentShareConfiguration : IEntityTypeConfiguration<DocumentShare>
{
    public void Configure(EntityTypeBuilder<DocumentShare> builder)
    {
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(share => new { share.DocumentId, share.TenantId })
            .HasPrincipalKey(document => new { document.Id, document.TenantId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(share => new { share.UserId, share.TenantId })
            .HasPrincipalKey(user => new { user.Id, user.TenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(share => share.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(share => new { share.DocumentId, share.UserId }).IsUnique();
        builder.HasIndex(share => share.TenantId);
    }
}
