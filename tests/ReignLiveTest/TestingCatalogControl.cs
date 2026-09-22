using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static Dictionary<string, object> TestingCatalogControl(string[] args)
        {
            const string resourceName = "Reign.TestingCatalog.json";
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("The embedded Reign testing catalog is unavailable.");
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    Dictionary<string, object> catalog = Json.Deserialize<Dictionary<string, object>>(json);
                    catalog["ok"] = true;
                    catalog["catalogFingerprintSha256"] = Sha256Hex(json);
                    catalog["controllerCommands"] = new[]
                    {
                        "arm", "arrest", "cancel", "catalog", "checkpoint", "close", "control",
                        "export", "game", "game-restart", "game-save", "game-start", "game-status",
                        "game-stop", "gauntlet", "manifest", "open", "pause", "plan", "prepare",
                        "qualify", "qualify-status", "rebellion", "record", "report", "reset",
                        "restart", "resume", "rollback", "run", "save", "send", "party-agency", "server",
                        "server-restart", "server-start", "server-status", "server-stop", "skill-exam",
                        "social-balance", "start", "start-menu", "status", "stop", "targets", "ui-action", "ui-back",
                        "ui-close", "ui-open", "ui-snapshot", "ui-status", "world-test"
                    };
                    catalog["partyAgencyController"] = new Dictionary<string, object>
                    {
                        ["observationalProfiles"] = new[] { "contracts", "status", "preflight" },
                        ["mutationRoute"] = "reign_start_party_agency_test",
                        ["naturalLanguageOnly"] = true,
                        ["requiresGuardedCampaignTestCurrentSave"] = true
                    };
                    return catalog;
                }
            }
        }

        private static string Sha256Hex(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))
                    .Select(valueByte => valueByte.ToString("x2")));
            }
        }
    }
}
