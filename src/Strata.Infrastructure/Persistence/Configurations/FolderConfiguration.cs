// Strata.Infrastructure/Persistence/Configurations/FolderConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strata.Domain.Documents;
using Strata.Domain.Tenancy;
using Strata.Infrastructure.Identity;

namespace Strata.Infrastructure.Persistence.Configurations;

public class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        // Folder relationships include TenantId so the database can reject
        // owners and parent folders from another tenant.
        builder.HasAlternateKey(folder => new { folder.Id, folder.TenantId });

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(f => new { f.OwnerId, f.TenantId })
            .HasPrincipalKey(user => new { user.Id, user.TenantId })
            .OnDelete(DeleteBehavior.Restrict);

        // 呢度 HasOne<Folder>() 指緊嘅係「同一種」entity(自己連自己)。
        // 因為冇 navigation property,寫法同上面連第二種 entity 完全一樣,
        // 淨係將 <Folder> 換做返自己個型別。
        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(f => new { f.ParentFolderId, f.TenantId })
            .HasPrincipalKey(parent => new { parent.Id, parent.TenantId })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(f => f.TenantId);
    }
}
