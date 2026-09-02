using NetArchTest.Rules;
using Xunit;

namespace Strata.Architecture.Tests;

public class LayeringTests
{
    [Fact]
    public void Domain_Should_Not_HaveDependencyOnOtherProjects()
    {
        var result = Types.InAssembly(typeof(Strata.Domain.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny(
                "Strata.Application",
                "Strata.Infrastructure",
                "Strata.Api",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Application_Should_Not_HaveDependencyOnInfrastructureOrApi()
    {
        var result = Types.InAssembly(typeof(Strata.Application.AssemblyMarker).Assembly)
            .Should()
            .NotHaveDependencyOnAny("Strata.Infrastructure", "Strata.Api")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    private static string Describe(TestResult result) =>
        "Types violating the rule: " + string.Join(", ", result.FailingTypeNames ?? []);
}
