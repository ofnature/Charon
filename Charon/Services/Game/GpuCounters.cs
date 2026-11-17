using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Charon.Services.Game;

/// <summary>
/// One process's GPU load and video memory, from Windows' own performance counters — the figures Task
/// Manager shows per process: "GPU Engine(pid_N_…engtype_3D)\Utilization Percentage" summed over the
/// process's 3D engines, and "GPU Process Memory(pid_N_…)\Dedicated Usage".
///
/// Collecting them walks every GPU engine on the machine and takes milliseconds, so it runs on its own
/// thread once a second and the game's thread only ever reads the last answer. No Dalamud types: it was
/// checked outside the game against Get-Counter for the same processes.
/// </summary>
public sealed class GpuCounters : IDisposable
{
    private const uint FormatDouble = 0x00000200;
    private const uint MoreData = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nint userData, out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize,
        out uint itemCount, nint buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);

    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    /// <summary>The last reading. Null fields mean the counters could not be read.</summary>
    public readonly record struct Reading(DateTime Utc, double? Gpu3dPercent, double? VideoMemoryMb);

    private readonly string _prefix;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly object _gate = new();
    private Reading _latest;

    public GpuCounters(int processId)
    {
        _prefix = $"pid_{processId}_";
        _thread = new Thread(Run) { IsBackground = true, Name = "Charon GPU counters" };
        _thread.Start();
    }

    /// <summary>Why nothing is being read, or empty while it works.</summary>
    public string Problem { get; private set; } = string.Empty;

    public Reading Latest
    {
        get
        {
            lock (_gate)
                return _latest;
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        if (PdhOpenQueryW(null, 0, out var query) != 0)
        {
            Problem = "Windows performance counters would not open";
            return;
        }

        try
        {
            var gpuOk = PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", 0, out var gpu) == 0;
            var memoryOk = PdhAddEnglishCounterW(query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out var memory) == 0;
            if (!gpuOk && !memoryOk)
            {
                Problem = "this PC has no per-process GPU counters";
                return;
            }

            // A rate needs two collections, so the first reading comes after the first wait.
            PdhCollectQueryData(query);
            while (!_stop.Wait(TimeSpan.FromSeconds(1)))
            {
                if (PdhCollectQueryData(query) != 0)
                    continue;

                var load = gpuOk ? Sum(gpu, name => name.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) : null;
                var bytes = memoryOk ? Sum(memory, _ => true) : null;
                lock (_gate)
                    _latest = new Reading(DateTime.UtcNow, load, bytes / (1024.0 * 1024.0));
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    /// <summary>The sum of this process's instances of one counter that pass <paramref name="wanted"/>;
    /// null when the counter could not be read.</summary>
    private double? Sum(nint counter, Func<string, bool> wanted)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out _, 0) != MoreData || size == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out var count, buffer) != 0)
                return null;

            var total = 0.0;
            var itemSize = Marshal.SizeOf<Item>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * itemSize);
                var name = Marshal.PtrToStringUni(item.Name);
                if (name != null && item.Status == 0
                    && name.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase) && wanted(name))
                    total += item.Value;
            }

            return total;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
