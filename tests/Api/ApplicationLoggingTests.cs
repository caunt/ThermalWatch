using System.Globalization;

using Serilog;
using Serilog.Core;
using Serilog.Events;

using ThermalWatch.Api;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies application logging filters.</summary>
public sealed class ApplicationLoggingTests
{
    /// <summary>Filters resilience-library events while retaining application warnings.</summary>
    [Fact]
    public void ConfigureSuppressesPollyEventsAndKeepsApplicationEvents()
    {
        var logEvents = new List<LogEvent>();

        using Logger logger = ApplicationLogging
            .Configure(new LoggerConfiguration(), LogEventLevel.Verbose)
            .WriteTo.Sink(new CollectingLogEventSink(logEvents))
            .CreateLogger();

        logger
            .ForContext(Constants.SourceContextPropertyName, value: "Polly")
            .Fatal(messageTemplate: "Top-level Polly event");
        logger
            .ForContext(Constants.SourceContextPropertyName, value: "Polly.Timeout")
            .Error(messageTemplate: "Nested Polly event");
        logger
            .ForContext(Constants.SourceContextPropertyName, value: "ThermalWatch.Api.FirmsPollingService")
            .Warning(messageTemplate: "Application event");

        LogEvent logEvent = Assert.Single(logEvents);
        Assert.Equal(LogEventLevel.Warning, logEvent.Level);
        Assert.Equal(expected: "Application event", logEvent.RenderMessage(CultureInfo.InvariantCulture));
    }

    private sealed class CollectingLogEventSink(ICollection<LogEvent> logEvents) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            logEvents.Add(logEvent);
        }
    }
}
