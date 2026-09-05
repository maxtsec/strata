using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Strata.Domain.Documents;
using Strata.Infrastructure.Identity;

namespace Strata.Api.IntegrationTests;

public static class TestApiHelpers
{
    public static async Task<HttpClient> AuthenticatedClientAsync(StrataWebApplicationFactory factory, string email)
    {
        var client = factory.CreateClient();
        var token = await RegisterAsync(client, email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<string> RegisterAsync(HttpClient client, string email, string password = "P@ssw0rd123!", string tenantName = "Test Tenant")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = password, TenantName = tenantName });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    public static Guid UserIdFromToken(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var sub = jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value;
        return Guid.Parse(sub);
    }

    public static Guid TenantIdFromToken(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var tenantId = jwt.Claims.First(c => c.Type == JwtTokenGenerator.TenantIdClaimType).Value;
        return Guid.Parse(tenantId);
    }

    public static async Task<Guid> CreateFolderAsync(HttpClient client, string name, Guid? parentFolderId = null)
    {
        var response = await client.PostAsJsonAsync("/api/folders", new { Name = name, ParentFolderId = parentFolderId });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("folderId").GetGuid();
    }

    public static async Task<Guid> CreateDocumentAsync(HttpClient client, string name, Guid? folderId = null)
    {
        var response = await client.PostAsJsonAsync("/api/documents", new
        {
            Name = name,
            FolderId = folderId,
            ContentType = "text/plain",
            Size = 1L
        });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("documentId").GetGuid();
    }

    public static async Task<Guid> CreateShareAsync(HttpClient client, Guid documentId, string email, DocumentShare.Role role)
    {
        var response = await client.PostAsJsonAsync($"/api/documents/{documentId}/shares", new { Email = email, Role = role });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("shareId").GetGuid();
    }
}
