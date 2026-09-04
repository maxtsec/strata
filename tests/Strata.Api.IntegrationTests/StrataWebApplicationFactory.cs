using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Strata.Application.Persistence;

namespace Strata.Api.IntegrationTests;

// Hosts the real app (real DI wiring, real OwnerAuthorizationHandler, real
// controllers) against a dedicated test SQL Server database, with IFileStorage
// swapped for a fake so no test ever reaches real Blob Storage.
public class StrataWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public FakeFileStorage FileStorage { get; } = new();

    public StrataWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["Jwt:SigningKey"] = "integration-test-signing-key-32-bytes-minimum!!",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(FileStorage);
        });
    }
}
