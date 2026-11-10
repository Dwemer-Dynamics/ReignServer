using System;
using System.Collections.Generic;

namespace Reign.Core.Contracts.Platform
{
    // Single-writer collector. The host supplies monotonic milliseconds and owns synchronization.
    // Storage is allocated at construction; Observe/RecordPhase allocate nothing in steady state.
    public sealed class NativePerformanceWindows
    {
        public const string Schema = "reign-native-performance-v1";
        private readonly NativePerformanceWindow[] ring;
        private readonly NativePerformanceWindow current;
        private readonly string[] phaseNames;
        private readonly double windowMs;
        private int writeIndex, retained, segment;
        private long sequence, invalidSamples, unknownPhases;
        private bool enabled, hasPrevious, previousForeground;
        private double previousMs, startMs;
        private int startGen0, startGen1, startGen2;
        private int previousGen0, previousGen1, previousGen2;

        public NativePerformanceWindows(string[] phases, int capacity = 120, double targetWindowMs = 1000)
        {
            if (phases == null || phases.Length > 8) throw new ArgumentException("At most eight fixed phase names are supported.", nameof(phases));
            if (capacity < 2 || capacity > 120) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!Finite(targetWindowMs) || targetWindowMs < 100 || targetWindowMs > 10000) throw new ArgumentOutOfRangeException(nameof(targetWindowMs));
            phaseNames = (string[])phases.Clone();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string name in phaseNames)
                if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || !names.Add(name)) throw new ArgumentException("Phase names must be unique bounded identifiers.", nameof(phases));
            windowMs = targetWindowMs;
            ring = new NativePerformanceWindow[capacity];
            for (int i = 0; i < capacity; i++) ring[i] = NewWindow();
            current = NewWindow();
        }

        public void Observe(bool samplingEnabled, double monotonicMs, int gen0, int gen1, int gen2, bool foreground)
        {
            if (!samplingEnabled)
            {
                if (enabled && hasPrevious && current.IntervalCount != 0) Publish(previousMs, previousGen0, previousGen1, previousGen2);
                enabled = false;
                hasPrevious = false;
                return;
            }
            enabled = true;
            if (!Finite(monotonicMs) || monotonicMs < 0 || hasPrevious && monotonicMs < previousMs)
            {
                invalidSamples++;
                hasPrevious = false;
                Clear(current);
                return;
            }
            if (!hasPrevious)
            {
                segment++;
                Clear(current);
                startMs = previousMs = monotonicMs;
                startGen0 = gen0; startGen1 = gen1; startGen2 = gen2;
                previousGen0 = gen0; previousGen1 = gen1; previousGen2 = gen2;
                previousForeground = foreground;
                hasPrevious = true;
                return;
            }
            double interval = monotonicMs - previousMs;
            current.IntervalCount++;
            current.IntervalSumMs += interval;
            current.MaximumIntervalMs = Math.Max(current.MaximumIntervalMs, interval);
            current.Buckets[Bucket(interval)]++;
            if (foreground && previousForeground) current.ForegroundIntervals++;
            previousMs = monotonicMs;
            previousForeground = foreground;
            previousGen0 = gen0; previousGen1 = gen1; previousGen2 = gen2;
            // A long stopped tick is one interval in one longer window, never fabricated empty windows.
            if (monotonicMs - startMs >= windowMs) Publish(monotonicMs, gen0, gen1, gen2);
        }

        public void RecordPhase(string name, double milliseconds)
        {
            if (!enabled || !hasPrevious) return;
            if (!Finite(milliseconds) || milliseconds < 0) { invalidSamples++; return; }
            for (int i = 0; i < phaseNames.Length; i++)
            {
                if (!string.Equals(name, phaseNames[i], StringComparison.Ordinal)) continue;
                NativePhaseTiming phase = current.Phases[i];
                phase.Count++;
                phase.TotalMs += milliseconds;
                phase.MaximumMs = Math.Max(phase.MaximumMs, milliseconds);
                return;
            }
            unknownPhases++;
        }

        public NativePerformanceSnapshot Snapshot(double capturedMonotonicMs, long afterSequence = 0)
        {
            if (!Finite(capturedMonotonicMs) || capturedMonotonicMs < 0) throw new ArgumentOutOfRangeException(nameof(capturedMonotonicMs));
            if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
            var windows = new List<NativePerformanceWindow>(retained);
            int oldest = (writeIndex - retained + ring.Length) % ring.Length;
            long oldestSequence = retained == 0 ? sequence + 1 : ring[oldest].Sequence;
            for (int i = 0; i < retained; i++)
            {
                NativePerformanceWindow source = ring[(oldest + i) % ring.Length];
                if (source.Sequence > afterSequence) windows.Add(Clone(source));
            }
            return new NativePerformanceSnapshot {
                Schema = Schema, SamplingEnabled = enabled, Segment = segment,
                CapturedMonotonicMs = capturedMonotonicMs, TargetWindowMs = windowMs,
                OldestSequence = oldestSequence, LatestSequence = sequence,
                RingCapacity = ring.Length, RingOverwrites = Math.Max(0, sequence - ring.Length),
                MissingRequestedWindows = afterSequence == 0 ? 0 : Math.Max(0, oldestSequence - afterSequence - 1),
                InvalidSamples = invalidSamples, UnknownPhaseObservations = unknownPhases,
                Windows = windows.ToArray()
            };
        }

        public static int Bucket(double milliseconds)
        {
            if (!Finite(milliseconds) || milliseconds < 0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            if (milliseconds < 8) return 0;
            if (milliseconds < 16) return 1;
            if (milliseconds < 33) return 2;
            if (milliseconds < 50) return 3;
            if (milliseconds < 100) return 4;
            return milliseconds < 250 ? 5 : 6;
        }

        private void Publish(double endMs, int gen0, int gen1, int gen2)
        {
            current.Sequence = ++sequence;
            current.Segment = segment;
            current.StartMonotonicMs = startMs;
            current.EndMonotonicMs = endMs;
            current.Gen0Collections = Math.Max(0, gen0 - startGen0);
            current.Gen1Collections = Math.Max(0, gen1 - startGen1);
            current.Gen2Collections = Math.Max(0, gen2 - startGen2);
            current.GcCountersReset = gen0 < startGen0 || gen1 < startGen1 || gen2 < startGen2;
            Copy(current, ring[writeIndex]);
            writeIndex = (writeIndex + 1) % ring.Length;
            retained = Math.Min(retained + 1, ring.Length);
            Clear(current);
            startMs = endMs;
            startGen0 = gen0; startGen1 = gen1; startGen2 = gen2;
        }

        private NativePerformanceWindow NewWindow()
        {
            var result = new NativePerformanceWindow { Buckets = new long[7], Phases = new NativePhaseTiming[phaseNames.Length] };
            for (int i = 0; i < phaseNames.Length; i++) result.Phases[i] = new NativePhaseTiming { Name = phaseNames[i] };
            return result;
        }

        private NativePerformanceWindow Clone(NativePerformanceWindow source)
        {
            var result = NewWindow();
            Copy(source, result);
            return result;
        }

        private static void Copy(NativePerformanceWindow source, NativePerformanceWindow target)
        {
            target.Sequence = source.Sequence; target.Segment = source.Segment;
            target.StartMonotonicMs = source.StartMonotonicMs; target.EndMonotonicMs = source.EndMonotonicMs;
            target.IntervalCount = source.IntervalCount; target.IntervalSumMs = source.IntervalSumMs;
            target.MaximumIntervalMs = source.MaximumIntervalMs; target.ForegroundIntervals = source.ForegroundIntervals;
            target.Gen0Collections = source.Gen0Collections; target.Gen1Collections = source.Gen1Collections; target.Gen2Collections = source.Gen2Collections;
            target.GcCountersReset = source.GcCountersReset;
            Array.Copy(source.Buckets, target.Buckets, source.Buckets.Length);
            for (int i = 0; i < source.Phases.Length; i++)
            {
                target.Phases[i].Count = source.Phases[i].Count;
                target.Phases[i].TotalMs = source.Phases[i].TotalMs;
                target.Phases[i].MaximumMs = source.Phases[i].MaximumMs;
            }
        }

        private static void Clear(NativePerformanceWindow value)
        {
            value.IntervalCount = value.ForegroundIntervals = 0;
            value.IntervalSumMs = value.MaximumIntervalMs = 0;
            Array.Clear(value.Buckets, 0, value.Buckets.Length);
            foreach (NativePhaseTiming phase in value.Phases) { phase.Count = 0; phase.TotalMs = phase.MaximumMs = 0; }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class NativePerformanceSnapshot
    {
        public string Schema { get; set; } = "";
        // Host publication identity travels with the immutable window snapshot.
        public string SnapshotCapturedUtc { get; set; } = "";
        public string ClientModuleMvid { get; set; } = "";
        public string Measurement { get; set; } = "";
        public bool SamplingEnabled { get; set; }
        public int Segment { get; set; }
        public double CapturedMonotonicMs { get; set; }
        public double TargetWindowMs { get; set; }
        public long OldestSequence { get; set; }
        public long LatestSequence { get; set; }
        public int RingCapacity { get; set; }
        public long RingOverwrites { get; set; }
        public long MissingRequestedWindows { get; set; }
        public long InvalidSamples { get; set; }
        public long UnknownPhaseObservations { get; set; }
        public NativePerformanceWindow[] Windows { get; set; } = Array.Empty<NativePerformanceWindow>();
    }

    public sealed class NativePerformanceWindow
    {
        public long Sequence { get; set; }
        public int Segment { get; set; }
        public double StartMonotonicMs { get; set; }
        public double EndMonotonicMs { get; set; }
        public long IntervalCount { get; set; }
        public double IntervalSumMs { get; set; }
        public double MaximumIntervalMs { get; set; }
        public long ForegroundIntervals { get; set; }
        public long[] Buckets { get; set; } = Array.Empty<long>();
        public int Gen0Collections { get; set; }
        public int Gen1Collections { get; set; }
        public int Gen2Collections { get; set; }
        public bool GcCountersReset { get; set; }
        public NativePhaseTiming[] Phases { get; set; } = Array.Empty<NativePhaseTiming>();
    }

    public sealed class NativePhaseTiming
    {
        public string Name { get; set; } = "";
        public long Count { get; set; }
        public double TotalMs { get; set; }
        public double MaximumMs { get; set; }
    }
}
