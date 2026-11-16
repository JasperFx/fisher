using System.Diagnostics.Metrics;

namespace Fisher.Benchmarks.Scenarios;

/// <summary>
///     Collects Fisher's <c>fisher.write_lock.wait</c> histogram and <c>fisher.write_lock.retries</c>
///     counter (fisher#208) for one named store.
/// </summary>
/// <remarks>
///     A <see cref="MeterListener" /> is process-wide and the <c>Fisher</c> meter is shared by every
///     store, so measurements are filtered on the <c>fisher.store</c> tag — each scenario round names its
///     store uniquely. The store must call <c>OpenTelemetry.TrackWriteLockContention()</c> or nothing is
///     recorded at all; that is the opt-in the instruments are built behind.
/// </remarks>
public sealed class WriteLockRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly string _storeName;
    private readonly List<double> _waits = new();
    private readonly List<(double Wait, string? Holder)> _byHolder = new();
    private long _retries;

    public WriteLockRecorder(string storeName)
    {
        _storeName = storeName;

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Fisher"
                && instrument.Name is "fisher.write_lock.wait" or "fisher.write_lock.retries")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "fisher.write_lock.wait" && IsOurs(tags))
            {
                string? holder = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "fisher.write_lock.holder") holder = tag.Value as string;
                }

                lock (_waits)
                {
                    _waits.Add(value);
                    _byHolder.Add((value, holder));
                }
            }
        });

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "fisher.write_lock.retries" && IsOurs(tags))
            {
                Interlocked.Add(ref _retries, value);
            }
        });

        _listener.Start();
    }

    /// <summary>Every recorded wait at <c>BEGIN IMMEDIATE</c>, in milliseconds.</summary>
    public IReadOnlyList<double> Waits
    {
        get
        {
            lock (_waits)
            {
                return _waits.ToArray();
            }
        }
    }

    /// <summary>
    ///     Every recorded wait with its <c>fisher.write_lock.holder</c> tag (<c>session</c>, <c>daemon</c>,
    ///     ...), in recording order.
    /// </summary>
    public IReadOnlyList<(double Wait, string? Holder)> WaitsByHolder
    {
        get
        {
            lock (_waits)
            {
                return _byHolder.ToArray();
            }
        }
    }

    public long Retries => Interlocked.Read(ref _retries);

    private bool IsOurs(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "fisher.store")
            {
                return Equals(tag.Value, _storeName);
            }
        }

        return false;
    }

    public void Dispose() => _listener.Dispose();
}
