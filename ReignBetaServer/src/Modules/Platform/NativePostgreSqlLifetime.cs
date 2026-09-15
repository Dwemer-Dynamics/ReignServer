using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Reign.Core.Contracts.Platform;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static NativePostgreSqlLifetime BeginInstalledDatabase(string[] args)
        {
#if REIGN_LINUX
            // DwemerDistro owns PostgreSQL; the server must never start or stop its shared cluster.
            return null;
#else
            // Tool/verification commands have their own isolated database owner.
            string[] runtimeOptions = { "--port", "--terminal-window-name", "--terminal-tab-index", "--no-browser" };
            if (args.Any(value => value.StartsWith("--", StringComparison.Ordinal)
                && !runtimeOptions.Contains(value))) return null;
            ReignInstallation installation = ReignInstallation.TryLoadCurrent();
            if (installation == null) return null;
            if (!Path.GetFullPath(DataDir).TrimEnd('\\').Equals(installation.DataRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Start ReignServer through its installed shortcut so the configured data location is used.");
            InitializeUnifiedProcessLifetime();
            if (UnifiedLifetimeJobHandle == IntPtr.Zero)
                throw new InvalidOperationException("ReignServer could not establish its required Windows process lifetime group.");
            return NativePostgreSqlLifetime.Start(installation);
#endif
        }
    }

    // Started after the visible server joins its kill-on-close Windows job. All
    // PostgreSQL children inherit that job; normal exit requests a fast shutdown.
    internal sealed class NativePostgreSqlLifetime : IDisposable
    {
        private readonly ReignInstallation installation;
        private readonly string cluster;
        private int stopped;

        private NativePostgreSqlLifetime(ReignInstallation installation)
        {
            this.installation = installation;
            cluster = Path.Combine(installation.DataRoot, "postgresql", "cluster");
        }

        internal static NativePostgreSqlLifetime Start(ReignInstallation installation)
        {
            var lifetime = new NativePostgreSqlLifetime(installation);
            string state = Path.GetDirectoryName(lifetime.cluster);
            string ownership = Path.Combine(state, "reign-cluster.json");
            string passwordFile = Path.Combine(state, "owner.password");
            if (!File.Exists(Path.Combine(lifetime.cluster, "PG_VERSION")) || !File.Exists(ownership) || !File.Exists(passwordFile))
                throw new InvalidDataException("The installed PostgreSQL cluster is incomplete. Run ReignServer setup to repair it.");
            if (File.ReadAllText(Path.Combine(lifetime.cluster, "PG_VERSION")).Trim() != "15")
                throw new InvalidDataException("The installed database requires an explicit major-version migration.");
            var metadata = new System.Web.Script.Serialization.JavaScriptSerializer()
                .Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(ownership));
            if (Convert.ToString(metadata["schema"]) != "reign-native-postgres-v1"
                || Convert.ToString(metadata["database"]) != "Reign"
                || Convert.ToString(metadata["username"]) != "reign"
                || Convert.ToInt32(metadata["port"], CultureInfo.InvariantCulture) != installation.PostgresPort)
                throw new InvalidDataException("PostgreSQL ownership does not match this installation.");
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(passwordFile),
                Encoding.UTF8.GetBytes("Reign.PostgreSQL.v1"), DataProtectionScope.CurrentUser);
            try
            {
                Environment.SetEnvironmentVariable("REIGN_DB_PASSWORD", Encoding.UTF8.GetString(clear));
            }
            finally { Array.Clear(clear, 0, clear.Length); }
            Environment.SetEnvironmentVariable("REIGN_DB_HOST", "127.0.0.1");
            Environment.SetEnvironmentVariable("REIGN_DB_PORT", installation.PostgresPort.ToString(CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("REIGN_DB_USER", "reign");
            Environment.SetEnvironmentVariable("REIGN_DB_NAME", "Reign");
            Environment.SetEnvironmentVariable("REIGN_POSTGRES_BIN", installation.PostgresBin);
            if (lifetime.Control("status", "-D", lifetime.cluster) == 0)
                throw new InvalidOperationException("This Reign database is already owned by another launch. Close the current ReignServer before starting another.");
            int result = lifetime.Control("start", "-D", lifetime.cluster, "-l", Path.Combine(state, "postgresql.log"),
                "-o", "-p " + installation.PostgresPort.ToString(CultureInfo.InvariantCulture) + " -h 127.0.0.1", "-w", "-t", "45");
            if (result != 0) throw new InvalidOperationException("The installed PostgreSQL server did not start. Check its local log and repair the package if needed.");
            AppDomain.CurrentDomain.ProcessExit += lifetime.OnProcessExit;
            return lifetime;
        }

        private int Control(params string[] arguments)
        {
            string executable = Path.Combine(installation.PostgresBin, "pg_ctl.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("The installed PostgreSQL controller is missing.", executable);
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", arguments.Select(PostgreSqlTools.QuoteWindowsArgument)),
                WorkingDirectory = installation.PostgresBin,
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                if (!process.WaitForExit(60000)) throw new TimeoutException("PostgreSQL lifecycle operation timed out.");
                return process.ExitCode;
            }
        }

        private void OnProcessExit(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0) return;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            try
            {
                if (Control("stop", "-D", cluster, "-m", "fast", "-w", "-t", "30") != 0)
                    Console.WriteLine("PostgreSQL shutdown was not confirmed; the owned process group will close on exit.");
            }
            catch (Exception error) { Console.WriteLine("PostgreSQL shutdown: " + error.Message); }
        }
    }
}
