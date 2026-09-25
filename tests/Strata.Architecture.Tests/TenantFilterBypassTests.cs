using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using Strata.Domain.Documents;
using Xunit;

namespace Strata.Architecture.Tests;

// Enforces the ADR 0004 project policy: APIs that bypass the global query
// filters or the SaveChanges interceptor must not appear in production code
// without their own tenant-isolation design review. That covers EF Core's
// filter opt-out, bulk and raw-SQL APIs, and raw ADO.NET, which reaches the
// database without going through EF Core at all. Scans compiled IL rather
// than source, so calls inside async state machines and lambdas are covered
// too.
public class TenantFilterBypassTests
{
    private static readonly HashSet<string> EfCoreBypassMethods =
    [
        "IgnoreQueryFilters",
        "ExecuteUpdate", "ExecuteUpdateAsync",
        "ExecuteDelete", "ExecuteDeleteAsync",
        "FromSql", "FromSqlRaw", "FromSqlInterpolated",
        "ExecuteSql", "ExecuteSqlAsync",
        "ExecuteSqlRaw", "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "SqlQuery", "SqlQueryRaw",
        "GetDbConnection"
    ];

    // Raw ADO.NET: opening a connection or creating a command, and executing
    // one. Matching both ends means a bypass is caught whether it starts from
    // EF Core's connection or a new one.
    private static readonly HashSet<string> AdoNetConnectionTypes =
    [
        "System.Data.IDbConnection",
        "System.Data.Common.DbConnection",
        "Microsoft.Data.SqlClient.SqlConnection",
        "System.Data.SqlClient.SqlConnection"
    ];

    private static readonly HashSet<string> AdoNetCommandTypes =
    [
        "System.Data.IDbCommand",
        "System.Data.Common.DbCommand",
        "Microsoft.Data.SqlClient.SqlCommand",
        "System.Data.SqlClient.SqlCommand"
    ];

    // Each entry must point to the design review and adversarial tests that
    // justify it (see ADR 0004). Format: "Namespace.Type::Method".
    private static readonly HashSet<string> ReviewedCallers = [];

    private static readonly Dictionary<string, Assembly> ProductionAssemblyByName = new[]
    {
        typeof(Strata.Domain.AssemblyMarker).Assembly,
        typeof(Strata.Application.AssemblyMarker).Assembly,
        typeof(Strata.Infrastructure.Persistence.AppDbContext).Assembly,
        typeof(Strata.Api.Controllers.DocumentsController).Assembly
    }.ToDictionary(assembly => assembly.GetName().Name!);

    public static TheoryData<string> ProductionAssemblies => [.. ProductionAssemblyByName.Keys];

    [Theory]
    [MemberData(nameof(ProductionAssemblies))]
    public void Production_Code_Should_Not_Bypass_Tenant_Isolation(string assemblyName)
    {
        var violations = FindBypassCalls(ProductionAssemblyByName[assemblyName].Location)
            .Where(call => !ReviewedCallers.Contains(call.Caller))
            .Select(call => call.ToString())
            .ToList();

        Assert.True(violations.Count == 0,
            "Tenant-isolation bypass APIs used without review (ADR 0004): " + string.Join("; ", violations));
    }

    // Guards against the rule passing vacuously, e.g. if a library renames or
    // moves these methods and the match silently stops working.
    [Theory]
    [InlineData(nameof(KnownViolation.IgnoreFilters), "EntityFrameworkQueryableExtensions.IgnoreQueryFilters")]
    [InlineData(nameof(KnownViolation.CommandFromEfConnection), "RelationalDatabaseFacadeExtensions.GetDbConnection")]
    [InlineData(nameof(KnownViolation.CommandFromEfConnection), "DbConnection.CreateCommand")]
    [InlineData(nameof(KnownViolation.CommandFromEfConnection), "DbCommand.ExecuteNonQuery")]
    [InlineData(nameof(KnownViolation.CommandFromNewConnection), "SqlConnection..ctor")]
    [InlineData(nameof(KnownViolation.CommandFromNewConnection), "SqlCommand.ExecuteReader")]
    public void Scanner_Detects_A_Known_Bypass_Call(string violatingMethod, string expectedCall)
    {
        var caller = $"{typeof(KnownViolation).FullName!.Replace('+', '/')}::{violatingMethod}";

        var calls = FindBypassCalls(Assembly.GetExecutingAssembly().Location);

        Assert.Contains(calls, call => call.Caller == caller && call.Method == expectedCall);
    }

    private static List<BypassCall> FindBypassCalls(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        // GetTypes() includes nested types, which is where the compiler puts
        // async state machines and lambda closures.
        return module.GetTypes()
            .SelectMany(type => type.Methods.Where(method => method.HasBody))
            .SelectMany(method => method.Body.Instructions
                .Select(instruction => instruction.Operand)
                .OfType<MethodReference>()
                .Where(IsBypass)
                .Select(called => new BypassCall(
                    $"{method.DeclaringType.FullName}::{method.Name}",
                    $"{called.DeclaringType.Name}.{called.Name}")))
            .ToList();
    }

    private static bool IsBypass(MethodReference called)
    {
        var declaringType = called.DeclaringType.GetElementType();

        if (declaringType.Namespace.StartsWith("Microsoft.EntityFrameworkCore"))
        {
            return EfCoreBypassMethods.Contains(called.Name);
        }

        if (AdoNetConnectionTypes.Contains(declaringType.FullName))
        {
            return called.Name is ".ctor" or "CreateCommand";
        }

        if (AdoNetCommandTypes.Contains(declaringType.FullName))
        {
            return called.Name == ".ctor" || called.Name.StartsWith("Execute");
        }

        return false;
    }

    private sealed record BypassCall(string Caller, string Method)
    {
        public override string ToString() => $"{Caller} calls {Method}";
    }

    private static class KnownViolation
    {
        public static IQueryable<Folder> IgnoreFilters(IQueryable<Folder> folders) => folders.IgnoreQueryFilters();

        public static int CommandFromEfConnection(DbContext db)
        {
            DbCommand command = db.Database.GetDbConnection().CreateCommand();
            return command.ExecuteNonQuery();
        }

        public static SqlDataReader CommandFromNewConnection(string connectionString) =>
            new SqlConnection(connectionString).CreateCommand().ExecuteReader();
    }
}
