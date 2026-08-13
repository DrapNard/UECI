using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ueci.Plugin;

public sealed record UnrealPluginBuildTiming(string Phase, TimeSpan Duration);

internal sealed class UnrealPluginBuildTimingCollector
{
    private readonly ConcurrentDictionary<string, long> _ticks = new(StringComparer.Ordinal);

    public async Task<T> MeasureAsync<T>(string phase, Func<Task<T>> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(action);
        long started = Stopwatch.GetTimestamp();
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            AddElapsed(phase, started);
        }
    }

    public async Task MeasureAsync(string phase, Func<Task> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(action);
        long started = Stopwatch.GetTimestamp();
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            AddElapsed(phase, started);
        }
    }

    public T Measure<T>(string phase, Func<T> action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        ArgumentNullException.ThrowIfNull(action);
        long started = Stopwatch.GetTimestamp();
        try
        {
            return action();
        }
        finally
        {
            AddElapsed(phase, started);
        }
    }

    public void Add(string phase, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        if (duration <= TimeSpan.Zero)
        {
            return;
        }
        _ticks.AddOrUpdate(phase, duration.Ticks, (_, previous) => checked(previous + duration.Ticks));
    }

    public IReadOnlyList<UnrealPluginBuildTiming> Snapshot(TimeSpan total)
    {
        var values = _ticks
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UnrealPluginBuildTiming(pair.Key, TimeSpan.FromTicks(pair.Value)))
            .ToList();
        values.Add(new UnrealPluginBuildTiming("total", total));
        return values;
    }

    private void AddElapsed(string phase, long started)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        Add(phase, elapsed);
    }
}
