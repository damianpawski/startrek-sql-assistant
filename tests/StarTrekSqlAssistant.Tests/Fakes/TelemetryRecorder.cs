using System.Diagnostics;
using System.Diagnostics.Metrics;
using StarTrekSqlAssistant.Web.Services;

namespace StarTrekSqlAssistant.Tests.Fakes;

/// <summary>One measurement as the app emitted it.</summary>
public sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
{
    public string? Tag(string key) => Tags.TryGetValue(key, out var value) ? value?.ToString() : null;
}

/// <summary>
/// Collects the spans and measurements the app emits, in process - the same way
/// an OTel exporter sees them, minus the exporter.
///
/// Everything is filtered to the trace this recorder starts, because xUnit runs
/// test classes in parallel and <see cref="AgentTelemetry"/>'s ActivitySource and
/// Meter are static: without the filter, a telemetry assertion would occasionally
/// pick up a question asked by a completely different test class. The metric
/// callbacks run synchronously on the recording thread, so the ambient
/// <see cref="Activity.Current"/> there is still the span that caused them.
/// </summary>
public sealed class TelemetryRecorder : IDisposable
{
    private static readonly ActivitySource TestSource = new("StarTrekSqlAssistant.Tests");

    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;
    private readonly Activity _root;
    private readonly List<Activity> _stopped = [];
    private readonly List<Measurement> _measurements = [];
    private readonly Lock _gate = new();

    public TelemetryRecorder()
    {
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name is AgentTelemetry.Name or "StarTrekSqlAssistant.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_gate)
                {
                    _stopped.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_activities);

        // Started before the meter listener so every measurement below has a
        // root to be attributed to.
        _root = TestSource.StartActivity("test")!;

        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AgentTelemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
    }

    /// <summary>Spans from this trace, in the order they finished.</summary>
    public IReadOnlyList<Activity> Activities
    {
        get
        {
            lock (_gate)
            {
                return [.. _stopped.Where(a => a.RootId == _root.RootId)];
            }
        }
    }

    public IReadOnlyList<Measurement> Measurements
    {
        get
        {
            lock (_gate)
            {
                return [.. _measurements];
            }
        }
    }

    public IEnumerable<Activity> Named(string name) => Activities.Where(a => a.OperationName == name);

    /// <summary>The one span with this name - a second one is a bug worth failing on.</summary>
    public Activity Single(string name) => Assert.Single(Named(name));

    public IEnumerable<Measurement> Of(string instrument) =>
        Measurements.Where(m => m.Instrument == instrument);

    private void Record<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        if (Activity.Current?.RootId != _root.RootId)
        {
            return; // another test class's question, running in parallel
        }

        var copied = new Dictionary<string, object?>(tags.Length);
        foreach (var tag in tags)
        {
            copied[tag.Key] = tag.Value;
        }

        lock (_gate)
        {
            _measurements.Add(new Measurement(instrument.Name, Convert.ToDouble(value), copied));
        }
    }

    public void Dispose()
    {
        _root.Dispose();
        _meters.Dispose();
        _activities.Dispose();
    }
}
