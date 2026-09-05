using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Strata.Application.Tenancy;
using Strata.Domain.Documents;

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
        var tenantId = jwt.Claims.First(c => c.Type == TenantClaimTypes.TenantId).Value;
        return Guid.Parse(tenantId);
    }

    // Hand-crafts a token signed with the test host's own signing key, so it
    // passes real signature and lifetime validation — only the supplied
    // claims are under the test's control. Used to exercise the tenant-claim
    // validation at the authentication boundary directly, independent of the
    // normal register/login issuance path.
    public static string CreateToken(IEnumerable<Claim> claims, TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(StrataWebApplicationFactory.TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
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
