using Strata.Domain.Documents;
using Microsoft.EntityFrameworkCore;

namespace Strata.Application.Persistence;

public interface IApplicationDbContext
{
    DbSet<Document> Documents { get; }
    DbSet<DocumentShare> DocumentShares { get; }
    DbSet<Folder> Folders { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
