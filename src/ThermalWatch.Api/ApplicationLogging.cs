using Serilog;
using Serilog.Events;
using Serilog.Filters;

namespace ThermalWatch.Api;

internal static class ApplicationLogging
{
    internal static LoggerConfiguration Configure(
        LoggerConfiguration loggerConfiguration,
        LogEventLevel minimumLevel) =>
        loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override(source: "Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override(source: "Microsoft.Extensions.Http", LogEventLevel.Fatal)
            .MinimumLevel.Override(source: "System.Net.Http.HttpClient", LogEventLevel.Fatal)
            .Filter.ByExcluding(Matching.FromSource(source: "Polly"))
            .Enrich.FromLogContext();
}
