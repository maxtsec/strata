using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using Strata.Domain.Documents;
using Xunit;

namespace Strata.Architecture.Tests;

// Enforces the ADR 0004 project policy: EF Core APIs that bypass the global
// query filters or the SaveChanges interceptor must not appear in production
// code without their own tenant-isolation design review. Scans compiled IL
// rather than source, so calls inside async state machines and lambdas are
// covered too.
public class TenantFilterBypassTests
{
    private static readonly HashSet<string> BypassMethods =
    [
        "IgnoreQueryFilters",
        "ExecuteUpdate", "ExecuteUpdateAsync",
        "ExecuteDelete", "ExecuteDeleteAsync",
        "FromSql", "FromSqlRaw", "FromSqlInterpolated",
        "ExecuteSql", "ExecuteSqlAsync",
        "ExecuteSqlRaw", "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated", "ExecuteSqlInterpolatedAsync",
        "SqlQuery", "SqlQueryRaw"
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

    [Fact]
    public void Scanner_Detects_A_Known_Bypass_Call()
    {
        // Guards against the rule passing vacuously, e.g. if EF Core renames
        // or moves these methods and the name match silently stops working.
        var calls = FindBypassCalls(Assembly.GetExecutingAssembly().Location);

        Assert.Contains(calls, call => call.Caller.StartsWith(typeof(KnownViolation).FullName!.Replace('+', '/'))
            && call.Method == nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters));
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
                .Where(called => called.DeclaringType.Namespace.StartsWith("Microsoft.EntityFrameworkCore")
                    && BypassMethods.Contains(called.Name))
                .Select(called => new BypassCall($"{method.DeclaringType.FullName}::{method.Name}", called.Name)))
            .ToList();
    }

    private sealed record BypassCall(string Caller, string Method)
    {
        public override string ToString() => $"{Caller} calls {Method}";
    }

    private static class KnownViolation
    {
        public static IQueryable<Folder> Query(IQueryable<Folder> folders) => folders.IgnoreQueryFilters();
    }
}
