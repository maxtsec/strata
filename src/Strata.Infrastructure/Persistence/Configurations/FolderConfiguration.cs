// Strata.Infrastructure/Persistence/Configurations/FolderConfiguration.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strata.Domain.Documents;
using Strata.Infrastructure.Identity;

namespace Strata.Infrastructure.Persistence.Configurations;

public class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(f => f.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        // 呢度 HasOne<Folder>() 指緊嘅係「同一種」entity(自己連自己)。
        // 因為冇 navigation property,寫法同上面連第二種 entity 完全一樣,
        // 淨係將 <Folder> 換做返自己個型別。
        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(f => f.ParentFolderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}