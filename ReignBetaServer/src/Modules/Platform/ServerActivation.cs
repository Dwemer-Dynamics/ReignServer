using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static int ActiveServerPort;

        private static int ActivateExistingServerFromLauncher(int port, string[] args)
        {
            return IsExistingServerHealthy(port) ? 0 : IsLocalPortAvailable(port) ? 2 : 3;
        }

        private static bool TryActivateExistingServer(int port, string[] args, bool healthy) => healthy;

        private static Dictionary<string, object> ActivateRunningServer(Dictionary<string, object> payload)
        {
            return new Dictionary<string, object> { ["ok"] = true, ["port"] = ActiveServerPort,
                ["managedBy"] = "DwemerDistro", ["url"] = "http://127.0.0.1:8089/" };
        }

        private static bool IsLocalPortAvailable(int port)
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { probe.Stop(); } catch { }
            }
        }

        private static string ReadArgument(string[] args, string name, string fallback)
        {
            args = args ?? new string[0];
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1] ?? fallback;
            }
            return fallback;
        }

        private static int ReadIntArgument(string[] args, string name, int fallback)
        {
            string value = ReadArgument(args, name, "");
            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }

        private static string QuoteCommandLineArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

    }
}
