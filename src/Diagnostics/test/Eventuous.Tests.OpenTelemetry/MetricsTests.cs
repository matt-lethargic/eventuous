using Eventuous.EventStore.Subscriptions;
using Eventuous.Sut.Subs;

// ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

namespace Eventuous.Tests.OpenTelemetry;

public sealed class MetricsTests(SubscriptionFixture fixture, ITestOutputHelper outputHelper) : IAsyncLifetime, IClassFixture<SubscriptionFixture> {
    const string SubscriptionId = "test-sub";

    [Fact]
    public void ShouldMeasureSubscriptionGapCount() {
        Assert.NotNull(_values);
        var counter  = _host.Services.GetRequiredService<MessageCounter>();
        var gapCount = GetValue(_values, SubscriptionMetrics.GapCountMetricName)!;
        var duration = GetValue(_values, SubscriptionMetrics.ProcessingRateName)!;

        var expectedGap = SubscriptionFixture.Count - counter.Count;

        gapCount.Should().NotBeNull();
        gapCount.Value.Should().BeInRange(expectedGap - 20, expectedGap + 20);
        GetTag(gapCount, SubscriptionMetrics.SubscriptionIdTag).Should().Be(SubscriptionId);
        GetTag(gapCount, "test").Should().Be("foo");

        duration.Should().NotBeNull();
        GetTag(duration, SubscriptionMetrics.SubscriptionIdTag).Should().Be(SubscriptionId);
        GetTag(duration, SubscriptionMetrics.MessageTypeTag).Should().Be(TestEvent.TypeName);
        GetTag(duration, "test").Should().Be("foo");
    }

    static MetricValue? GetValue(MetricValue[] values, string metric)
        => values.FirstOrDefault(x => x.Name == metric);

    static object GetTag(MetricValue metric, string key) {
        var index = metric.Keys.Select((x, i) => (x, i)).First(x => x.x == key).i;

        return metric.Values[index];
    }

    public async Task InitializeAsync() {
        var builder = new WebHostBuilder()
            .Configure(_ => { })
            .ConfigureServices(
                services => {
                    services.AddSingleton(fixture.Client);
                    services.AddSingleton<MessageCounter>();

                    services.AddSubscription<StreamSubscription, StreamSubscriptionOptions>(
                        SubscriptionId,
                        builder => builder
                            .Configure(options => options.StreamName = fixture.Stream)
                            .UseCheckpointStore<NoOpCheckpointStore>()
                            .AddEventHandler<TestHandler>()
                    );

                    services.AddOpenTelemetry()
                        .WithMetrics(
                            builder => builder
                                .AddEventuousSubscriptions()
                                .AddReader(new BaseExportingMetricReader(_exporter))
                        );
                }
            )
            .ConfigureLogging(cfg => cfg.AddXunit(outputHelper));

        _host = new TestServer(builder);
        var counter = _host.Services.GetRequiredService<MessageCounter>();

        while (counter.Count < SubscriptionFixture.Count / 2) {
            await Task.Delay(10);
        }

        _exporter.Collect(Timeout.Infinite);
        _values = _exporter.CollectValues();

        foreach (var value in _values) {
            outputHelper.WriteLine(value.ToString());
        }

        // await Task.Delay(500);
    }

    public Task DisposeAsync() {
        _host.Dispose();
        _exporter.Dispose();
        _es.Dispose();

        return Task.CompletedTask;
    }

    TestServer _host = null!;

    readonly TestExporter _exporter = new();

    readonly TestEventListener _es = new(outputHelper);

    MetricValue[]? _values;

    // ReSharper disable once ClassNeverInstantiated.Local
    class TestHandler(MessageCounter counter, ILogger<TestHandler> log) : BaseEventHandler {
        public override async ValueTask<EventHandlingStatus> HandleEvent(IMessageConsumeContext context) {
            await Task.Delay(10, context.CancellationToken);
            counter.Increment();
            log.LogDebug("Handled event {Number} {EventId}", counter.Count, context.MessageId);

            return EventHandlingStatus.Success;
        }
    }

    class MessageCounter {
        public int Count;
        public void Increment() => Interlocked.Increment(ref Count);
    }

    [ExportModes(ExportModes.Pull)]
    class TestExporter : BaseExporter<Metric>, IPullMetricExporter {
        public override ExportResult Export(in Batch<Metric> batch) {
            Batch = batch;

            return ExportResult.Success;
        }

        Batch<Metric> Batch { get; set; }

        public Func<int, bool> Collect { get; set; } = null!;

        public MetricValue[] CollectValues() {
            var values = new List<MetricValue>();

            foreach (var metric in Batch) {
                if (metric == null) continue;

                foreach (ref readonly var metricPoint in metric.GetMetricPoints()) {
                    var tags = new List<(string, object?)>();

                    foreach (var (key, value) in metricPoint.Tags) {
                        tags.Add((key, value));
                    }

                    var metricValue = metric.MetricType switch {
                        MetricType.Histogram   => metricPoint.GetHistogramSum() / metricPoint.GetHistogramCount(),
                        MetricType.DoubleGauge => metricPoint.GetGaugeLastValueDouble(),
                        MetricType.LongGauge   => metricPoint.GetGaugeLastValueLong(),
                        _                      => throw new ArgumentOutOfRangeException()
                    };

                    values.Add(
                        new MetricValue(
                            metric.Name,
                            tags.Select(x => x.Item1).ToArray(),
                            tags.Select(x => x.Item2).ToArray()!,
                            metricValue
                        )
                    );
                }
            }

            return values.ToArray();
        }
    }
}

record MetricValue(string Name, string[] Keys, object[] Values, double Value);
