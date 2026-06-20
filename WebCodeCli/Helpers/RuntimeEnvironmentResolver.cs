using Microsoft.AspNetCore.Hosting;

namespace WebCodeCli.Helpers;

public static class RuntimeEnvironmentResolver
{
    public static string ResolveDefaultEnvironmentName()
    {
        var configuredEnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(configuredEnvironmentName))
        {
            configuredEnvironmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        }

        if (!string.IsNullOrWhiteSpace(configuredEnvironmentName))
        {
            return configuredEnvironmentName;
        }

        return Environments.Production;
    }
}
