using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ReignTelemetrySourceName = "Bannerlord.Reign";
        private static readonly ActivitySource ReignActivitySource = new ActivitySource(ReignTelemetrySourceName, "1.0.0");
        private static readonly object ReignTelemetryLock = new object();
        private static TracerProvider ReignTracerProvider;
        private static bool ReignTelemetryInitialized;

        private sealed class ReignTraceScope : IDisposable
        {
            private readonly Activity _activity;
            private readonly Stopwatch _timer;
            private readonly string _name;
            private readonly Dictionary<string, object> _fields;
            private bool _disposed;

            public ReignTraceScope(string name, Dictionary<string, object> fields)
            {
                _name = name ?? "reign.operation";
                _fields = fields ?? new Dictionary<string, object>();
                _timer = Stopwatch.StartNew();
                _activity = ReignActivitySource.StartActivity(_name, ActivityKind.Internal);
                foreach (KeyValuePair<string, object> pair in _fields)
                {
                    _activity?.SetTag("reign." + pair.Key, pair.Value == null ? "" : pair.Value.ToString());
                }
            }

            public void Set(string key, object value)
            {
                if (string.IsNullOrWhiteSpace(key)) return;
                _fields[key] = value;
                _activity?.SetTag("reign." + key, value == null ? "" : value.ToString());
            }

            public void Fail(Exception exception)
            {
                if (exception == null) return;
                _fields["error"] = LimitText(exception.Message, 1000);
                _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                _activity?.SetTag("error.type", exception.GetType().FullName);
                _activity?.SetTag("error.message", LimitText(exception.Message, 1000));
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _timer.Stop();
                _fields["durationMs"] = _timer.ElapsedMilliseconds;
                _fields["traceId"] = _activity == null ? "" : _activity.TraceId.ToString();
                _fields["spanId"] = _activity == null ? "" : _activity.SpanId.ToString();
                _fields["name"] = _name;
                _fields["utc"] = DateTime.UtcNow.ToString("o");
                _activity?.SetTag("reign.duration_ms", _timer.ElapsedMilliseconds);
                _activity?.Dispose();
                try
                {
                    AppendJsonLineToPath(Path.Combine(LogsDir, "otel-traces.ndjson"), _fields);
                }
                catch
                {
                }
            }
        }

        private static void InitializeReignTelemetry(Dictionary<string, object> settings)
        {
            lock (ReignTelemetryLock)
            {
                if (ReignTelemetryInitialized) return;
                TracerProviderBuilder builder = Sdk.CreateTracerProviderBuilder().AddSource(ReignTelemetrySourceName);
                string endpoint = ReadString(settings, "openTelemetryOtlpEndpoint", "").Trim();
                if (!string.IsNullOrWhiteSpace(endpoint) && Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri))
                {
                    builder = builder.AddOtlpExporter(options => options.Endpoint = uri);
                }
                ReignTracerProvider = builder.Build();
                ReignTelemetryInitialized = true;
            }
        }

        private static ReignTraceScope BeginReignSpan(string name, Dictionary<string, object> fields = null)
        {
            if (!ReignTelemetryInitialized) InitializeReignTelemetry(LoadSettings());
            return new ReignTraceScope(name, fields);
        }

        private static Dictionary<string, object> ReignTelemetryStatus()
        {
            Dictionary<string, object> settings = LoadSettings();
            string path = Path.Combine(LogsDir, "otel-traces.ndjson");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["source"] = ReignTelemetrySourceName,
                ["initialized"] = ReignTelemetryInitialized,
                ["otlpEndpoint"] = ReadString(settings, "openTelemetryOtlpEndpoint", ""),
                ["localTracePath"] = path,
                ["localTraceBytes"] = File.Exists(path) ? new FileInfo(path).Length : 0L
            };
        }
    }
}
