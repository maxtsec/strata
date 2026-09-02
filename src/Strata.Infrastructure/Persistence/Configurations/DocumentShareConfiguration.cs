using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Strata.Domain.Documents;
using Strata.Infrastructure.Identity;

namespace Strata.Infrastructure.Persistence.Configurations;

public class DocumentShareConfiguration : IEntityTypeConfiguration<DocumentShare>
{
    public void Configure(EntityTypeBuilder<DocumentShare> builder)
    {
        builder.HasOne<Document>()
            .WithMany()
            .HasForeignKey(share => share.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(share => share.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
