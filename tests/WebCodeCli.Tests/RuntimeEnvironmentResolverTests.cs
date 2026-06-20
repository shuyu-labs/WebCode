using WebCodeCli.Helpers;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class RuntimeEnvironmentResolverTests
{
    [Fact]
    public void ResolveDefaultEnvironmentName_ReturnsProduction_WhenNoEnvironmentVariablesAreSet()
    {
        const string aspnetCoreEnvironment = "ASPNETCORE_ENVIRONMENT";
        const string dotnetEnvironment = "DOTNET_ENVIRONMENT";

        var originalAspNetCoreEnvironment = Environment.GetEnvironmentVariable(aspnetCoreEnvironment);
        var originalDotnetEnvironment = Environment.GetEnvironmentVariable(dotnetEnvironment);

        try
        {
            Environment.SetEnvironmentVariable(aspnetCoreEnvironment, null);
            Environment.SetEnvironmentVariable(dotnetEnvironment, null);

            var resolved = RuntimeEnvironmentResolver.ResolveDefaultEnvironmentName();

            Assert.Equal("Production", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(aspnetCoreEnvironment, originalAspNetCoreEnvironment);
            Environment.SetEnvironmentVariable(dotnetEnvironment, originalDotnetEnvironment);
        }
    }

    [Fact]
    public void ResolveDefaultEnvironmentName_PrefersExplicitAspNetCoreEnvironment()
    {
        const string aspnetCoreEnvironment = "ASPNETCORE_ENVIRONMENT";
        const string dotnetEnvironment = "DOTNET_ENVIRONMENT";

        var originalAspNetCoreEnvironment = Environment.GetEnvironmentVariable(aspnetCoreEnvironment);
        var originalDotnetEnvironment = Environment.GetEnvironmentVariable(dotnetEnvironment);

        try
        {
            Environment.SetEnvironmentVariable(aspnetCoreEnvironment, "Staging");
            Environment.SetEnvironmentVariable(dotnetEnvironment, "Production");

            var resolved = RuntimeEnvironmentResolver.ResolveDefaultEnvironmentName();

            Assert.Equal("Staging", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable(aspnetCoreEnvironment, originalAspNetCoreEnvironment);
            Environment.SetEnvironmentVariable(dotnetEnvironment, originalDotnetEnvironment);
        }
    }
}
