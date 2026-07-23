using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ThermalWatch.Api;

namespace ThermalWatch.Tests;

public sealed class ApplicationLoggingTests
{
    [Fact]
    public void ConfigureSuppressesPollyEventsAndKeepsApplicationEvents()
    {
        var logEvents = new List<LogEvent>();
        using Logger logger = ApplicationLogging
            .Configure(new LoggerConfiguration(), LogEventLevel.Verbose)
            .WriteTo.Sink(new CollectingLogEventSink(logEvents))
            .CreateLogger();

        logger
            .ForContext(propertyName: Constants.SourceContextPropertyName, value: "Polly")
            .Fatal(messageTemplate: "Top-level Polly event");
        logger
            .ForContext(propertyName: Constants.SourceContextPropertyName, value: "Polly.Timeout")
            .Error(messageTemplate: "Nested Polly event");
        logger
            .ForContext(propertyName: Constants.SourceContextPropertyName, value: "ThermalWatch.Api.FirmsPollingService")
            .Warning(messageTemplate: "Application event");

        LogEvent logEvent = Assert.Single(logEvents);
        Assert.Equal(LogEventLevel.Warning, logEvent.Level);
        Assert.Equal("Application event", logEvent.RenderMessage(formatProvider: CultureInfo.InvariantCulture));
    }

    private sealed class CollectingLogEventSink(ICollection<LogEvent> logEvents) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => logEvents.Add(logEvent);
    }
}
