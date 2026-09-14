using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int RelationshipSimulationCensusVersion = 1;
        private const int RelationshipSimulationModelVersion = 4;
        private static readonly object RelationshipSimulationLock = new object();
        private static readonly object RelationshipSimulationStaticCensusLock = new object();
        private static RelationshipSimulationRun ActiveRelationshipSimulation;
        private static Dictionary<string, object> CachedRelationshipSimulationStaticCensus;
        private static string CachedRelationshipSimulationStaticCensusSignature = "";

        private static Dictionary<string, object> BuildRelationshipSimulationStaticCensus()
        {
            string modulesRoot = FindRelationshipSimulationModulesRoot();
            if (string.IsNullOrWhiteSpace(modulesRoot))
                return new Dictionary<string, object>
                {
                    ["error"] = "The installed Bannerlord Modules directory could not be found."
                };

            string sandboxData = Path.Combine(modulesRoot, "SandBox", "ModuleData");
            string navalData = Path.Combine(modulesRoot, "NavalDLC", "ModuleData");
            string reignData = Path.Combine(modulesRoot, "ReignBeta", "ModuleData");
            List<string> sourceFiles = new[]
            {
                Path.Combine(sandboxData, "lords.xml"), Path.Combine(sandboxData, "heroes.xml"),
                Path.Combine(sandboxData, "sandbox_skill_sets.xml"), Path.Combine(sandboxData, "spclans.xml"),
                Path.Combine(sandboxData, "spkingdoms.xml"), Path.Combine(sandboxData, "settlements.xml"),
                Path.Combine(navalData, "naval_lords.xml"), Path.Combine(navalData, "heroes.xml"),
                Path.Combine(navalData, "naval_skill_sets.xml"), Path.Combine(navalData, "clans.xml"),
                Path.Combine(navalData, "kingdoms.xml"), Path.Combine(navalData, "settlements.xml"),
                Path.Combine(reignData, "reign_court_lords.xml"), Path.Combine(reignData, "reign_court_heroes.xml"),
                Path.Combine(reignData, "reign_court_clans.xml")
            }.Where(File.Exists).ToList();
            string signature = string.Join("|", sourceFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                FileInfo file = new FileInfo(path);
                return path.ToLowerInvariant() + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks;
            }));

            lock (RelationshipSimulationStaticCensusLock)
            {
                if (CachedRelationshipSimulationStaticCensus != null && signature == CachedRelationshipSimulationStaticCensusSignature)
                    return CachedRelationshipSimulationStaticCensus;

                Dictionary<string, XElement> clans = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in new[]
                {
                    Path.Combine(sandboxData, "spclans.xml"),
                    Path.Combine(navalData, "clans.xml"),
                    Path.Combine(reignData, "reign_court_clans.xml")
                }.Where(File.Exists))
                    foreach (XElement node in XDocument.Load(path).Descendants("Faction").Where(x => x.Attribute("id") != null))
                        clans[(string)node.Attribute("id")] = node;

                Dictionary<string, XElement> kingdoms = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in new[] { Path.Combine(sandboxData, "spkingdoms.xml"), Path.Combine(navalData, "kingdoms.xml") }.Where(File.Exists))
                    foreach (XElement node in XDocument.Load(path).Descendants("Kingdom").Where(x => x.Attribute("id") != null))
                        kingdoms[(string)node.Attribute("id")] = node;

                Dictionary<string, XElement> skillSets = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in new[] { Path.Combine(sandboxData, "sandbox_skill_sets.xml"), Path.Combine(navalData, "naval_skill_sets.xml") }.Where(File.Exists))
                    foreach (XElement node in XDocument.Load(path).Descendants("SkillSet").Where(x => x.Attribute("id") != null))
                        skillSets[(string)node.Attribute("id")] = node;

                Dictionary<string, List<Dictionary<string, object>>> citiesByKingdom = kingdoms.Keys.ToDictionary(
                    x => x, x => new List<Dictionary<string, object>>(), StringComparer.OrdinalIgnoreCase);
                Dictionary<string, int> fiefCountsByClan = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in new[] { Path.Combine(sandboxData, "settlements.xml"), Path.Combine(navalData, "settlements.xml") }.Where(File.Exists))
                {
                    foreach (XElement settlement in XDocument.Load(path).Descendants("Settlement").Where(x => x.Attribute("id") != null))
                    {
                        XElement town = settlement.Descendants("Town").FirstOrDefault();
                        if (town == null) continue;
                        string clanId = RemoveObjectPrefix((string)settlement.Attribute("owner"));
                        if (!string.IsNullOrWhiteSpace(clanId))
                        {
                            int fiefCount;
                            fiefCountsByClan.TryGetValue(clanId, out fiefCount);
                            fiefCountsByClan[clanId] = fiefCount + 1;
                        }
                        if (string.Equals((string)town.Attribute("is_castle"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                        XElement clan;
                        string kingdomId = clans.TryGetValue(clanId, out clan) ? RemoveObjectPrefix((string)clan.Attribute("super_faction")) : "";
                        if (string.IsNullOrWhiteSpace(kingdomId)) continue;
                        List<Dictionary<string, object>> cityRows;
                        if (!citiesByKingdom.TryGetValue(kingdomId, out cityRows))
                        {
                            cityRows = new List<Dictionary<string, object>>();
                            citiesByKingdom[kingdomId] = cityRows;
                        }
                        cityRows.Add(new Dictionary<string, object>
                        {
                            ["settlementId"] = (string)settlement.Attribute("id"),
                            ["name"] = CleanGameText((string)settlement.Attribute("name"))
                        });
                    }
                }

                List<Dictionary<string, object>> heroRows = new List<Dictionary<string, object>>();
                Dictionary<string, int> moduleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                Action<string, string, string, string> addModule = (moduleId, npcPath, heroPath, sourceLabel) =>
                {
                    if (!File.Exists(npcPath) || !File.Exists(heroPath)) return;
                    Dictionary<string, XElement> heroNodes = XDocument.Load(heroPath).Descendants("Hero")
                        .Where(x => x.Attribute("id") != null)
                        .GroupBy(x => (string)x.Attribute("id"), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
                    int added = 0;
                    foreach (XElement npc in XDocument.Load(npcPath).Descendants("NPCCharacter")
                        .Where(x => string.Equals((string)x.Attribute("occupation"), "Lord", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(x => (string)x.Attribute("id"), StringComparer.OrdinalIgnoreCase))
                    {
                        string id = (string)npc.Attribute("id");
                        XElement hero;
                        if (string.IsNullOrWhiteSpace(id) || id.Equals("main_hero", StringComparison.OrdinalIgnoreCase) || !heroNodes.TryGetValue(id, out hero)) continue;
                        bool alive = !string.Equals((string)hero.Attribute("alive"), "false", StringComparison.OrdinalIgnoreCase);
                        double age = ParseDouble((string)npc.Attribute("age"));
                        if (!alive || age < 18d || heroRows.Any(x => ReadString(x, "heroStringId", "").Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                        string clanId = RemoveObjectPrefix((string)hero.Attribute("faction"));
                        XElement clan;
                        clans.TryGetValue(clanId, out clan);
                        string kingdomId = RemoveObjectPrefix((string)clan?.Attribute("super_faction"));
                        string cultureId = RemoveObjectPrefix((string)npc.Attribute("culture"));
                        Dictionary<string, object> nativeTraits = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (string key in new[] { "valor", "generosity", "honor", "mercy", "calculating" }) nativeTraits[key] = 0;
                        foreach (XElement trait in npc.Descendants("Trait"))
                        {
                            string key = ((string)trait.Attribute("id") ?? "").ToLowerInvariant();
                            if (nativeTraits.ContainsKey(key)) nativeTraits[key] = ClampTrait(ParseInt((string)trait.Attribute("value")));
                        }
                        Dictionary<string, object> skills = ResolveCatalogNativeSkills(npc, skillSets);
                        string homeSettlementId = RemoveObjectPrefix((string)clan?.Attribute("initial_home_settlement"));
                        XElement kingdom;
                        kingdoms.TryGetValue(kingdomId, out kingdom);
                        int clanTier = ParseInt((string)clan?.Attribute("tier"));
                        int fiefCount;
                        fiefCountsByClan.TryGetValue(clanId, out fiefCount);
                        double clanRenown = clanTier * 225d + fiefCount * 90d;
                        double clanGold = 50000d + clanTier * 60000d + fiefCount * 85000d;
                        double influence = clanTier * 100d + fiefCount * 75d;
                        double leadership = ReadDouble(skills, "leadership", 0d);
                        heroRows.Add(new Dictionary<string, object>
                        {
                            ["heroStringId"] = id, ["characterObjectId"] = id,
                            ["name"] = CleanGameText((string)npc.Attribute("name")), ["age"] = age,
                            ["isAlive"] = true, ["isPrisoner"] = false, ["isLord"] = true, ["isNotable"] = false, ["isWanderer"] = false,
                            ["isFemale"] = string.Equals((string)npc.Attribute("is_female"), "true", StringComparison.OrdinalIgnoreCase),
                            ["cultureId"] = cultureId, ["clanId"] = clanId, ["kingdomId"] = kingdomId,
                            ["clanTier"] = clanTier, ["clanFiefCount"] = fiefCount, ["fiefCount"] = fiefCount,
                            ["clanRenown"] = clanRenown, ["renown"] = clanRenown,
                            ["clanGold"] = clanGold, ["gold"] = clanGold * 0.10d, ["influence"] = influence,
                            ["leadership"] = leadership, ["partyStrength"] = 80d + leadership * 1.2d,
                            ["clanStrength"] = 180d + clanTier * 160d + fiefCount * 120d,
                            ["visibleStatusScore"] = ClampDouble(clanTier * 12d + fiefCount * 6d + (kingdom != null && RemoveObjectPrefix((string)kingdom.Attribute("owner")).Equals(id, StringComparison.OrdinalIgnoreCase) ? 18d : 0d), 0d, 100d),
                            ["spouseId"] = RemoveObjectPrefix((string)hero.Attribute("spouse")),
                            ["fatherId"] = RemoveObjectPrefix((string)hero.Attribute("father")),
                            ["motherId"] = RemoveObjectPrefix((string)hero.Attribute("mother")),
                            ["currentSettlementId"] = homeSettlementId, ["currentSettlementKingdomId"] = kingdomId,
                            ["isRuler"] = kingdom != null && RemoveObjectPrefix((string)kingdom.Attribute("owner")).Equals(id, StringComparison.OrdinalIgnoreCase),
                            ["traits"] = nativeTraits, ["skills"] = skills, ["source"] = sourceLabel, ["sourceModule"] = moduleId
                        });
                        added++;
                    }
                    moduleCounts[moduleId] = added;
                };
                addModule("SandBox", Path.Combine(sandboxData, "lords.xml"), Path.Combine(sandboxData, "heroes.xml"), "native_sandbox");
                addModule("NavalDLC", Path.Combine(navalData, "naval_lords.xml"), Path.Combine(navalData, "heroes.xml"), "war_sails");
                addModule("ReignBeta", Path.Combine(reignData, "reign_court_lords.xml"), Path.Combine(reignData, "reign_court_heroes.xml"), "reign_court");

                List<Dictionary<string, object>> kingdomRows = kingdoms.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
                {
                    List<Dictionary<string, object>> cityRows;
                    if (!citiesByKingdom.TryGetValue(pair.Key, out cityRows)) cityRows = new List<Dictionary<string, object>>();
                    cityRows = cityRows.GroupBy(x => ReadString(x, "settlementId", ""), StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                        .OrderBy(x => ReadString(x, "settlementId", ""), StringComparer.OrdinalIgnoreCase).ToList();
                    return new Dictionary<string, object>
                    {
                        ["kingdomId"] = pair.Key, ["name"] = CleanGameText((string)pair.Value.Attribute("name")),
                        ["cultureId"] = RemoveObjectPrefix((string)pair.Value.Attribute("culture")),
                        ["leaderId"] = RemoveObjectPrefix((string)pair.Value.Attribute("owner")),
                        ["cityIds"] = cityRows.Select(x => ReadString(x, "settlementId", "")).ToList(), ["cities"] = cityRows
                    };
                }).Where(x => heroRows.Any(hero => ReadString(hero, "kingdomId", "").Equals(ReadString(x, "kingdomId", ""), StringComparison.OrdinalIgnoreCase))).ToList();

                List<Dictionary<string, object>> clanRows = clans.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new Dictionary<string, object>
                {
                    ["fiefCount"] = fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0,
                    ["clanId"] = pair.Key, ["name"] = CleanGameText((string)pair.Value.Attribute("name")),
                    ["kingdomId"] = RemoveObjectPrefix((string)pair.Value.Attribute("super_faction")),
                    ["cultureId"] = RemoveObjectPrefix((string)pair.Value.Attribute("culture")),
                    ["tier"] = ParseInt((string)pair.Value.Attribute("tier")),
                    ["renown"] = ParseInt((string)pair.Value.Attribute("tier")) * 225d + (fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0) * 90d,
                    ["gold"] = 50000d + ParseInt((string)pair.Value.Attribute("tier")) * 60000d + (fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0) * 85000d,
                    ["influence"] = ParseInt((string)pair.Value.Attribute("tier")) * 100d + (fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0) * 75d,
                    ["leaderId"] = RemoveObjectPrefix((string)pair.Value.Attribute("owner")),
                    ["memberIds"] = heroRows.Where(x => ReadString(x, "clanId", "").Equals(pair.Key, StringComparison.OrdinalIgnoreCase)).Select(x => ReadString(x, "heroStringId", "")).ToList()
                }).Where(x => ReadStringList(x, "memberIds").Count > 0).ToList();

                CachedRelationshipSimulationStaticCensus = new Dictionary<string, object>
                {
                    ["version"] = RelationshipSimulationCensusVersion,
                    ["campaignId"] = "installed_module_roster", ["timelineId"] = "static", ["worldDay"] = 0d,
                    ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["source"] = "installed_modules", ["modulesRoot"] = modulesRoot,
                    ["sourceModules"] = moduleCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new Dictionary<string, object> { ["moduleId"] = x.Key, ["npcCount"] = x.Value }).ToList(),
                    ["heroes"] = heroRows, ["nobles"] = heroRows, ["clans"] = clanRows, ["kingdoms"] = kingdomRows,
                    ["nativePresenceGroups"] = new List<Dictionary<string, object>>()
                };
                CachedRelationshipSimulationStaticCensusSignature = signature;
                return CachedRelationshipSimulationStaticCensus;
            }
        }

        private static string FindRelationshipSimulationModulesRoot()
        {
            string configured = Environment.GetEnvironmentVariable("REIGN_BANNERLORD_MODULES") ?? "";
            if (Directory.Exists(Path.Combine(configured, "SandBox", "ModuleData"))) return Path.GetFullPath(configured);
            DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                if (current.Name.Equals("Modules", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(current.FullName, "SandBox", "ModuleData")))
                    return current.FullName;
                current = current.Parent;
            }
            foreach (string candidate in new[]
            {
                @"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules",
                @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules"
            })
                if (Directory.Exists(Path.Combine(candidate, "SandBox", "ModuleData"))) return candidate;
            return "";
        }

        private static void CaptureRelationshipSimulationCensus(Dictionary<string, object> payload)
        {
            if (payload == null || ReadDictionaryList(payload, "heroes").Count == 0) return;
            string campaignId = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaignId)) return;
            Dictionary<string, object> census = new Dictionary<string, object>
            {
                ["version"] = RelationshipSimulationCensusVersion,
                ["campaignId"] = campaignId,
                ["timelineId"] = ReadString(payload, "timelineId", "main"),
                ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                ["playerId"] = ReadString(payload, "playerId", ""),
                ["playerClanId"] = ReadString(payload, "playerClanId", ""),
                ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["heroes"] = ReadDictionaryList(payload, "heroes"),
                ["nobles"] = ReadDictionaryList(payload, "nobles"),
                ["clans"] = ReadDictionaryList(payload, "clans"),
                ["kingdoms"] = ReadDictionaryList(payload, "kingdoms"),
                ["nativePresenceGroups"] = ReadDictionaryList(payload, "presenceGroups")
            };
            WriteJsonObject(RelationshipSimulationCensusPath(campaignId), census);
        }

        private static string RelationshipSimulationCensusPath(string campaignId)
        {
            return CampaignFile(campaignId, "relationship_simulation_census.json");
        }

        private static string RelationshipSimulationRoot()
        {
            return Path.Combine(DataDir, "relationship_simulations");
        }

        private static string RelationshipSimulationRunDirectory(string runId)
        {
            return Path.Combine(RelationshipSimulationRoot(), SafePathSegment(runId, "run"));
        }

        private static Dictionary<string, object> RelationshipSimulationCensusStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> census = BuildRelationshipSimulationStaticCensus();
            string error = ReadString(census, "error", "");
            Dictionary<string, object> selected = string.IsNullOrWhiteSpace(error) ? new Dictionary<string, object>
            {
                ["campaignId"] = "installed_module_roster", ["source"] = "installed_modules",
                ["timelineId"] = "static", ["worldDay"] = 0d,
                ["capturedUtc"] = ReadString(census, "capturedUtc", ""),
                ["heroCount"] = ReadDictionaryList(census, "heroes").Count,
                ["kingdomCount"] = ReadDictionaryList(census, "kingdoms").Count,
                ["cityCount"] = ReadDictionaryList(census, "kingdoms").Sum(x => ReadStringList(x, "cityIds").Count),
                ["modulesRoot"] = ReadString(census, "modulesRoot", ""),
                ["sourceModules"] = ReadDictionaryList(census, "sourceModules")
            } : new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["ok"] = string.IsNullOrWhiteSpace(error),
                ["selected"] = selected,
                ["censuses"] = string.IsNullOrWhiteSpace(error) ? new List<Dictionary<string, object>> { selected } : new List<Dictionary<string, object>>(),
                ["hasCensus"] = string.IsNullOrWhiteSpace(error),
                ["source"] = "installed_modules",
                ["error"] = error
            };
        }

        private static Dictionary<string, object> RelationshipSimulationStartApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            lock (RelationshipSimulationLock)
            {
                if (ActiveRelationshipSimulation != null && ActiveRelationshipSimulation.State == "running")
                    return RelationshipSimulationStatusDictionary(ActiveRelationshipSimulation, ReadString(payload, "facet", "trust"), ReadString(payload, "kingdomId", ""));

                Dictionary<string, object> census = ReadDictionary(payload, "census");
                if (census == null || census.Count == 0)
                    census = BuildRelationshipSimulationStaticCensus();
                string censusError = ReadString(census, "error", "");
                if (census.Count == 0 || !string.IsNullOrWhiteSpace(censusError))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = FirstNonEmpty(censusError, "The installed all-campaign NPC roster is unavailable.") };

                int durationDays = Clamp(ReadInt(payload, "durationDays", 365), 7, 3650);
                int seed = ReadInt(payload, "seed", 24017);
                RelationshipSimulationRun run = CreateRelationshipSimulation(census, durationDays, seed, true);
                if (run.Heroes.Count < 2)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The selected census does not contain at least two eligible NPCs." };
                ActiveRelationshipSimulation = run;
                Task.Run(() => ExecuteRelationshipSimulation(run));
                return RelationshipSimulationStatusDictionary(run, ReadString(payload, "facet", "trust"), ReadString(payload, "kingdomId", ""));
            }
        }

        private static Dictionary<string, object> RelationshipSimulationStopApi(Dictionary<string, object> payload)
        {
            lock (RelationshipSimulationLock)
            {
                if (ActiveRelationshipSimulation == null)
                    return new Dictionary<string, object> { ["ok"] = true, ["state"] = "idle" };
                ActiveRelationshipSimulation.CancelRequested = true;
                return RelationshipSimulationStatusDictionary(ActiveRelationshipSimulation, ReadString(payload, "facet", "trust"), ReadString(payload, "kingdomId", ""));
            }
        }

        private static Dictionary<string, object> RelationshipSimulationStatusApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            lock (RelationshipSimulationLock)
            {
                if (ActiveRelationshipSimulation == null)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["state"] = "idle",
                        ["census"] = ReadDictionary(RelationshipSimulationCensusStatusApi(payload), "selected") ?? new Dictionary<string, object>()
                    };
                return RelationshipSimulationStatusDictionary(ActiveRelationshipSimulation, ReadString(payload, "facet", "trust"), ReadString(payload, "kingdomId", ""));
            }
        }

        private static Dictionary<string,object> RunRelationshipSimulationAuditCli(int durationDays,int seed)
        {
            Dictionary<string,object> census=BuildRelationshipSimulationStaticCensus();
            if(!string.IsNullOrWhiteSpace(ReadString(census,"error","")))return new Dictionary<string,object>{{"ok",false},{"error",ReadString(census,"error","")}};
            RelationshipSimulationRun run=CreateRelationshipSimulation(census,durationDays,seed,false);
            run.State="running";
            for(int offset=0;offset<durationDays;offset++)
            {
                if(offset>0&&offset%7==0)AssignRelationshipSimulationLocations(run,offset/7);
                int day=run.StartDay+offset;
                RunRelationshipSimulationDay(run,day);
                run.CurrentDay=day;
                run.CompletedDays=offset+1;
                run.Progress=(offset+1d)/durationDays;
                if(offset==0||(offset+1)%7==0||offset==durationDays-1)CaptureRelationshipSimulationWeek(run,day);
            }
            run.State="completed";
            run.CompletedUtc=DateTimeOffset.UtcNow.ToString("o",CultureInfo.InvariantCulture);
            return RelationshipSimulationStatusDictionary(run,"trust","");
        }

        private static RelationshipSimulationRun CreateRelationshipSimulation(Dictionary<string, object> census, int durationDays, int seed, bool persist)
        {
            string campaignId = ReadString(census, "campaignId", "simulation");
            RelationshipSimulationRun run = new RelationshipSimulationRun
            {
                RunId = "relationship_sim_" + DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                CampaignId = campaignId,
                TimelineId = ReadString(census, "timelineId", "main"),
                State = "running",
                StartDay = (int)Math.Floor(ReadDouble(census, "worldDay", 0d)),
                DurationDays = durationDays,
                Seed = seed,
                Persist = persist,
                StartedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            Dictionary<string, Dictionary<string, object>> library = LoadCharacterProfileLibrary();
            foreach (Dictionary<string, object> row in ReadDictionaryList(census, "heroes"))
            {
                string id = ReadFirstString(row, "heroStringId", "heroId", "id");
                if (string.IsNullOrWhiteSpace(id) || id.Equals(ReadString(census, "playerId", ""), StringComparison.OrdinalIgnoreCase)) continue;
                if (!ReadBool(row, "isAlive", true) || ReadBool(row, "isPrisoner", false) || ReadDouble(row, "age", 18d) < 18d) continue;
                Dictionary<string, object> data = new Dictionary<string, object>(row, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> shipped;
                if (library.TryGetValue(id, out shipped))
                {
                    Dictionary<string, object> facts = ReadDictionary(shipped, "sourceFacts") ?? new Dictionary<string, object>();
                    foreach (KeyValuePair<string, object> item in facts) if (!data.ContainsKey(item.Key)) data[item.Key] = item.Value;
                }
                Dictionary<string, object> storedProfile = ReadJsonObject(CharacterFile(campaignId, id, "profile.json"));
                foreach (KeyValuePair<string, object> item in storedProfile) if (!data.ContainsKey(item.Key)) data[item.Key] = item.Value;
                Dictionary<string, object> traits = ReadJsonObject(CharacterFile(campaignId, id, "traits.json"));
                if (traits.Count == 0 && library.TryGetValue(id, out shipped)) traits = ReadDictionary(shipped, "traits") ?? new Dictionary<string, object>();
                if (traits.Count == 0) traits = BuildTraitDocument(data);
                Dictionary<string, object> foundations = ReadDictionary(traits, "foundationTraits") ?? new Dictionary<string, object>();
                data["foundationTraits"] = foundations;
                double visibleAttractiveness = RelationshipVisibleAttractiveness(data, traits);
                data["visibleAttractiveness"] = visibleAttractiveness;
                data["appearance"] = new Dictionary<string, object>
                {
                    ["attractiveness"] = visibleAttractiveness,
                    ["visibleStatusScore"] = ReadDouble(data, "visibleStatusScore", 50d)
                };
                string kingdomId = FirstNonEmpty(ReadString(data, "kingdomId", ""), ReadString(data, "currentSettlementKingdomId", ""));
                if (string.IsNullOrWhiteSpace(kingdomId)) kingdomId = "independent:" + FirstNonEmpty(ReadString(data, "cultureId", ""), "unaligned");
                data["simulationKingdomId"] = kingdomId;
                run.Heroes[id] = new RelationshipSimulationHero { Id = id, KingdomId = kingdomId, Data = data, Traits = traits };
            }

            foreach (Dictionary<string, object> kingdom in ReadDictionaryList(census, "kingdoms"))
            {
                string id = ReadString(kingdom, "kingdomId", "");
                if (string.IsNullOrWhiteSpace(id)) continue;
                run.KingdomNames[id] = ReadString(kingdom, "name", id);
                run.RealCityCounts[id] = ReadStringList(kingdom, "cityIds").Distinct(StringComparer.OrdinalIgnoreCase).Count();
                run.LocationsByKingdom[id] = ReadStringList(kingdom, "cityIds").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
            foreach (IGrouping<string, RelationshipSimulationHero> kingdomHeroes in run.Heroes.Values.GroupBy(x => x.KingdomId, StringComparer.OrdinalIgnoreCase))
            {
                if (!run.KingdomNames.ContainsKey(kingdomHeroes.Key)) run.KingdomNames[kingdomHeroes.Key] = kingdomHeroes.Key.StartsWith("independent:", StringComparison.OrdinalIgnoreCase) ? "Independent " + kingdomHeroes.Key.Substring("independent:".Length) : kingdomHeroes.Key;
                if (!run.RealCityCounts.ContainsKey(kingdomHeroes.Key)) run.RealCityCounts[kingdomHeroes.Key] = 0;
                List<string> locations;
                if (!run.LocationsByKingdom.TryGetValue(kingdomHeroes.Key, out locations) || locations.Count == 0)
                {
                    locations = kingdomHeroes.Select(x => ReadString(x.Data, "currentSettlementId", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    if (locations.Count == 0) locations.Add(kingdomHeroes.Key.Replace(':', '_') + "_unlanded_court");
                    run.LocationsByKingdom[kingdomHeroes.Key] = locations;
                }
            }

            foreach (Dictionary<string, object> clan in ReadDictionaryList(census, "clans"))
            {
                string id = ReadString(clan, "clanId", "");
                if (!string.IsNullOrWhiteSpace(id)) run.Clans[id] = new Dictionary<string, object>(clan, StringComparer.OrdinalIgnoreCase);
            }
            foreach (Dictionary<string, object> group in ReadDictionaryList(census, "nativePresenceGroups"))
            {
                foreach (Dictionary<string, object> relation in ReadDictionaryList(group, "nativeRelations"))
                {
                    string a = ReadFirstString(relation, "heroAId", "a");
                    string b = ReadFirstString(relation, "heroBId", "b");
                    string pair = AmbientPairKey(a, b);
                    if (!string.IsNullOrWhiteSpace(pair)) run.NativeRelations[pair] = Clamp(ReadInt(relation, "value", 0), -100, 100);
                }
            }
            AssignRelationshipSimulationLocations(run, 0);
            return run;
        }

        private static void ExecuteRelationshipSimulation(RelationshipSimulationRun run)
        {
            try
            {
                for (int offset = 0; offset < run.DurationDays; offset++)
                {
                    lock (RelationshipSimulationLock)
                    {
                        if (run.CancelRequested)
                        {
                            run.State = "cancelled";
                            break;
                        }
                        if (offset > 0 && offset % 7 == 0) AssignRelationshipSimulationLocations(run, offset / 7);
                        int day = run.StartDay + offset;
                        RunRelationshipSimulationDay(run, day);
                        run.CompletedDays = offset + 1;
                        run.CurrentDay = day;
                        run.Progress = run.DurationDays <= 0 ? 1d : (double)run.CompletedDays / run.DurationDays;
                        if ((offset + 1) % 7 == 0 || offset + 1 == run.DurationDays)
                        {
                            CaptureRelationshipSimulationWeek(run, day);
                            PersistRelationshipSimulation(run);
                        }
                    }
                }
                lock (RelationshipSimulationLock)
                {
                    if (run.State == "running") run.State = "completed";
                    run.Progress = run.State == "completed" ? 1d : run.Progress;
                    run.CompletedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    PersistRelationshipSimulation(run);
                }
            }
            catch (Exception ex)
            {
                lock (RelationshipSimulationLock)
                {
                    run.State = "failed";
                    run.Error = ex.Message;
                    run.CompletedUtc = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                    PersistRelationshipSimulation(run);
                }
            }
        }

        private static void AssignRelationshipSimulationLocations(RelationshipSimulationRun run, int week)
        {
            int moved = 0;
            foreach (IGrouping<string, RelationshipSimulationHero> kingdom in run.Heroes.Values.GroupBy(x => x.KingdomId, StringComparer.OrdinalIgnoreCase))
            {
                List<string> locations = run.LocationsByKingdom[kingdom.Key];
                List<RelationshipSimulationHero> shuffled = kingdom.OrderBy(x => StableUnit(run.Seed.ToString(CultureInfo.InvariantCulture) + "|location|" + week + "|" + x.Id)).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < shuffled.Count; i++)
                {
                    string next = locations[i % locations.Count];
                    if (!string.IsNullOrWhiteSpace(shuffled[i].LocationId) && !shuffled[i].LocationId.Equals(next, StringComparison.OrdinalIgnoreCase)) moved++;
                    shuffled[i].LocationId = next;
                }
            }
            run.Relocations += moved;
            run.RelocationHistory.Add(new Dictionary<string, object> { ["week"] = week, ["day"] = run.StartDay + week * 7, ["movedNpcCount"] = moved });
        }

        private static void RunRelationshipSimulationDay(RelationshipSimulationRun run, int day)
        {
            Dictionary<string, RelationshipSimulationEdge> present = BuildRelationshipSimulationPresence(run);
            foreach (RelationshipSimulationEdge edge in present.Values)
            {
                RelationshipSimulationRelation ab = EnsureRelationshipSimulationRelation(run, edge.A, edge.B);
                RelationshipSimulationRelation ba = EnsureRelationshipSimulationRelation(run, edge.B, edge.A);
                RelationshipSimulationExposure exposure;
                if (!run.Exposures.TryGetValue(edge.PairKey, out exposure))
                {
                    exposure = new RelationshipSimulationExposure { FirstDay = day, LastDay = day - 1, LastNativeSyncDay = day };
                    run.Exposures[edge.PairKey] = exposure;
                }
                AmbientPairContext context = new AmbientPairContext
                {
                    PairKey = edge.PairKey, HeroAId = edge.A, HeroBId = edge.B, ContextKind = "settlement",
                    ContextId = edge.LocationId, ExposureWeight = AmbientSettlementExposure, NativeRelation = edge.NativeRelation,
                    SameClan = edge.SameClan, SameKingdom = edge.SameKingdom
                };
                Dictionary<string, object> compatibilityAB = CalculateAmbientCompatibility(run.Heroes[edge.A].Data, run.Heroes[edge.B].Data, context);
                Dictionary<string, object> compatibilityBA = CalculateAmbientCompatibility(run.Heroes[edge.B].Data, run.Heroes[edge.A].Data, context);
                Dictionary<string, double> driftAB = ApplyRelationshipSimulationAmbient(ab, compatibilityAB, AmbientSettlementExposure);
                Dictionary<string, double> driftBA = ApplyRelationshipSimulationAmbient(ba, compatibilityBA, AmbientSettlementExposure);
                RecordRelationshipSimulationPassiveDrift(run, driftAB);
                RecordRelationshipSimulationPassiveDrift(run, driftBA);
                double pairSignal = (AmbientSocialSignal(driftAB) + AmbientSocialSignal(driftBA)) / 2d;
                exposure.WeightedExposure += AmbientSettlementExposure;
                exposure.PendingNativeSignal += pairSignal;
                exposure.ConsecutiveDays = day - exposure.LastDay <= 1 ? exposure.ConsecutiveDays + 1 : 1;
                exposure.LastDay = day;
                exposure.LocationId = edge.LocationId;
                if (exposure.WeightedExposure >= 4d && day - exposure.LastNativeSyncDay >= 7 && Math.Abs(exposure.PendingNativeSignal) >= 1d)
                {
                    int delta = exposure.PendingNativeSignal > 0d ? 1 : -1;
                    run.NativeRelations[edge.PairKey] = Clamp(edge.NativeRelation + delta, -100, 100);
                    exposure.PendingNativeSignal -= delta;
                    exposure.LastNativeSyncDay = day;
                    run.NativeRelationChanges++;
                    if (delta > 0) run.PositiveNativeRelationChanges++;
                    else run.NegativeNativeRelationChanges++;
                }
            }

            if ((day - run.StartDay) % 2 == 0) RunRelationshipSimulationConsequentialEvent(run, day, present);
            if ((day - run.StartDay) % 7 == 0) RunRelationshipSimulationMarriageSystem(run, day, present);
        }

        private static Dictionary<string, RelationshipSimulationEdge> BuildRelationshipSimulationPresence(RelationshipSimulationRun run)
        {
            Dictionary<string, RelationshipSimulationEdge> result = new Dictionary<string, RelationshipSimulationEdge>(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, RelationshipSimulationHero> location in run.Heroes.Values.GroupBy(x => x.LocationId, StringComparer.OrdinalIgnoreCase))
            {
                List<RelationshipSimulationHero> members = location.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < members.Count; i++)
                for (int j = i + 1; j < members.Count; j++)
                {
                    string pair = AmbientPairKey(members[i].Id, members[j].Id);
                    int native;
                    run.NativeRelations.TryGetValue(pair, out native);
                    result[pair] = new RelationshipSimulationEdge
                    {
                        PairKey = pair, A = members[i].Id, B = members[j].Id, LocationId = location.Key,
                        CoLocated = true, NativeRelation = native,
                        SameClan = SameSimulationValue(members[i].Data, members[j].Data, "clanId"),
                        SameKingdom = members[i].KingdomId.Equals(members[j].KingdomId, StringComparison.OrdinalIgnoreCase),
                        Spouses = ReadString(members[i].Data, "spouseId", "").Equals(members[j].Id, StringComparison.OrdinalIgnoreCase)
                            || ReadString(members[j].Data, "spouseId", "").Equals(members[i].Id, StringComparison.OrdinalIgnoreCase),
                        Family = RelationshipSimulationFamily(members[i], members[j])
                    };
                }
            }
            return result;
        }

        private static Dictionary<string, double> ApplyRelationshipSimulationAmbient(RelationshipSimulationRelation relation, Dictionary<string, object> compatibility, double exposureWeight)
        {
            Dictionary<string, object> equilibrium = ReadDictionary(compatibility, "equilibrium") ?? new Dictionary<string, object>();
            Dictionary<string, double> changes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string facet in AmbientFacetKeys)
            {
                double current = relation.Facets[facet];
                double desired = ReadDouble(equilibrium, facet, current);
                double gap = desired - current;
                if (Math.Abs(gap) < 0.5d) continue;
                double delta = ClampDouble(gap * 0.02d * exposureWeight, -0.50d, 0.50d);
                if (Math.Abs(delta) < 0.00001d) continue;
                relation.Facets[facet] = ClampRelationship(current + delta);
                changes[facet] = delta;
            }
            return changes;
        }

        private static void RecordRelationshipSimulationPassiveDrift(RelationshipSimulationRun run, Dictionary<string,double> changes)
        {
            foreach(KeyValuePair<string,double> change in changes)
            {
                Dictionary<string,double> target=change.Value>=0d?run.PassivePositiveDrift:run.PassiveNegativeDrift;
                double prior;
                target.TryGetValue(change.Key,out prior);
                target[change.Key]=prior+Math.Abs(change.Value);
            }
        }

        private static string RelationshipSimulationStoryKey(string subject,string target,string kind)
        {
            return subject+"|"+target+"|"+kind;
        }

        private static RelationshipSimulationStoryThread RelationshipSimulationStory(RelationshipSimulationRun run,string subject,string target,string kind,int day,bool create)
        {
            string key=RelationshipSimulationStoryKey(subject,target,kind);
            RelationshipSimulationStoryThread thread;
            if(!run.StoryThreads.TryGetValue(key,out thread)&&create)
            {
                thread=new RelationshipSimulationStoryThread{SubjectId=subject,TargetId=target,Kind=kind,LastEventDay=day};
                run.StoryThreads[key]=thread;
            }
            return thread;
        }

        private static double RelationshipSimulationStoryIntensity(RelationshipSimulationRun run,string subject,string target,string kind,int day)
        {
            RelationshipSimulationStoryThread thread=RelationshipSimulationStory(run,subject,target,kind,day,false);
            return thread==null?0d:EffectiveStoryIntensity(thread.Intensity,kind,thread.Route,thread.LastEventDay,day);
        }

        private static double RelationshipSimulationPairStoryIntensity(RelationshipSimulationRun run,string a,string b,string kind,int day,bool bilateral)
        {
            double ab=RelationshipSimulationStoryIntensity(run,a,b,kind,day),ba=RelationshipSimulationStoryIntensity(run,b,a,kind,day);
            return bilateral?Math.Min(ab,ba):Math.Max(ab,ba);
        }

        private static double RelationshipSimulationStoryMultiplier(RelationshipSimulationRun run,string a,string b,int day)
        {
            double multiplier=1d;
            foreach(string kind in RelationshipStoryKinds)
            foreach(RelationshipSimulationStoryThread thread in new[]{RelationshipSimulationStory(run,a,b,kind,day,false),RelationshipSimulationStory(run,b,a,kind,day,false)})
            {
                if(thread==null)continue;
                double intensity=EffectiveStoryIntensity(thread.Intensity,kind,thread.Route,thread.LastEventDay,day);
                multiplier=Math.Max(multiplier,StoryContinuationMultiplier(intensity,kind,thread.LastEventDay,day));
            }
            return multiplier;
        }

        private static double RelationshipSimulationPairStoryMultiplier(RelationshipSimulationRun run,string a,string b,string kind,int day)
        {
            double multiplier=1d;
            foreach(RelationshipSimulationStoryThread thread in new[]{RelationshipSimulationStory(run,a,b,kind,day,false),RelationshipSimulationStory(run,b,a,kind,day,false)})
            {
                if(thread==null)continue;
                double intensity=EffectiveStoryIntensity(thread.Intensity,kind,thread.Route,thread.LastEventDay,day);
                multiplier=Math.Max(multiplier,StoryContinuationMultiplier(intensity,kind,thread.LastEventDay,day));
            }
            return multiplier;
        }

        private static void AdvanceRelationshipSimulationStory(RelationshipSimulationRun run,string subject,string target,string kind,string route,double delta,int qualifying,string eventType,int day,string motiveChannel="")
        {
            RelationshipSimulationStoryThread thread=RelationshipSimulationStory(run,subject,target,kind,day,true);
            double current=EffectiveStoryIntensity(thread.Intensity,kind,thread.Route,thread.LastEventDay,day);
            string priorStage=StoryStage(kind,current);
            thread.Intensity=ClampDouble(current+delta,0d,100d);
            if(!string.IsNullOrWhiteSpace(route))thread.Route=route;
            if(!string.IsNullOrWhiteSpace(motiveChannel))thread.MotiveChannel=motiveChannel;
            thread.LastEventDay=day;
            thread.LastEventType=eventType;
            thread.QualifyingEvents=Math.Max(0,thread.QualifyingEvents+qualifying);
            string nextStage=StoryStage(kind,thread.Intensity);
            if(kind=="rivalry"&&thread.Intensity>=75d&&thread.QualifyingEvents>=2&&priorStage!="major"&&nextStage=="major")
            {
                string pair=StoryPairKey(subject,target);
                if(run.FeudPairs.Add(pair))run.EventCounts["feuds_started"]=ReadSimulationCount(run.EventCounts,"feuds_started")+1;
            }
        }

        private static void ApplyRelationshipSimulationStoryEvent(RelationshipSimulationRun run,string eventType,string a,string b,int day,
            Dictionary<string,object> postureA=null,Dictionary<string,object> postureB=null)
        {
            string spouseA=ReadString(run.Heroes[a].Data,"spouseId",""),spouseB=ReadString(run.Heroes[b].Data,"spouseId","");
            bool married=(!string.IsNullOrWhiteSpace(spouseA)&&!spouseA.Equals(b,StringComparison.OrdinalIgnoreCase))
                ||(!string.IsNullOrWhiteSpace(spouseB)&&!spouseB.Equals(a,StringComparison.OrdinalIgnoreCase));
            switch(eventType)
            {
                case "mutual_romantic_flirtation": AdvanceRelationshipSimulationStory(run,a,b,"romance",married?"temptation":"courtship",25,1,eventType,day,RomanticMotiveChannel(postureA));AdvanceRelationshipSimulationStory(run,b,a,"romance",married?"temptation":"courtship",25,1,eventType,day,RomanticMotiveChannel(postureB));break;
                case "romantic_confidence": AdvanceRelationshipSimulationStory(run,a,b,"romance",married?"temptation":"courtship",15,1,eventType,day,RomanticMotiveChannel(postureA));AdvanceRelationshipSimulationStory(run,b,a,"romance",married?"temptation":"courtship",15,1,eventType,day,RomanticMotiveChannel(postureB));break;
                case "romantic_intimacy": AdvanceRelationshipSimulationStory(run,a,b,"romance","courtship",30,1,eventType,day,RomanticMotiveChannel(postureA));AdvanceRelationshipSimulationStory(run,b,a,"romance","courtship",30,1,eventType,day,RomanticMotiveChannel(postureB));break;
                case "secret_affair_intimacy": AdvanceRelationshipSimulationStory(run,a,b,"romance","affair",30,1,eventType,day,RomanticMotiveChannel(postureA));AdvanceRelationshipSimulationStory(run,b,a,"romance","affair",30,1,eventType,day,RomanticMotiveChannel(postureB));break;
                case "romantic_rejection":case "romantic_boundary": AdvanceRelationshipSimulationStory(run,a,b,"romance","",-30,0,eventType,day,RomanticMotiveChannel(postureA));AdvanceRelationshipSimulationStory(run,b,a,"romance","",-30,0,eventType,day,RomanticMotiveChannel(postureB));break;
                case "political_rivalry_argument":case "political_obstruction": AdvanceRelationshipSimulationStory(run,a,b,"rivalry","",20,1,eventType,day);AdvanceRelationshipSimulationStory(run,b,a,"rivalry","",20,1,eventType,day);break;
                case "marital_argument":case "marital_separation": AdvanceRelationshipSimulationStory(run,a,b,"marital_conflict","",20,1,eventType,day);AdvanceRelationshipSimulationStory(run,b,a,"marital_conflict","",20,1,eventType,day);break;
                case "private_spousal_confidence": AdvanceRelationshipSimulationStory(run,a,b,"marital_conflict","",-10,0,eventType,day);AdvanceRelationshipSimulationStory(run,b,a,"marital_conflict","",-10,0,eventType,day);break;
                case "shared_confidence": AdvanceRelationshipSimulationStory(run,a,b,"shared_secret","",15,1,eventType,day);AdvanceRelationshipSimulationStory(run,b,a,"shared_secret","",15,1,eventType,day);break;
                case "practical_favor": AdvanceRelationshipSimulationStory(run,b,a,"favor_debt","",15,1,eventType,day);break;
            }
        }

        private static RelationshipSimulationRelation EnsureRelationshipSimulationRelation(RelationshipSimulationRun run, string subjectId, string targetId)
        {
            string key = subjectId + "|" + targetId;
            RelationshipSimulationRelation existing;
            if (run.Relationships.TryGetValue(key, out existing)) return existing;
            RelationshipSimulationHero subject = run.Heroes[subjectId], target = run.Heroes[targetId];
            string pair = AmbientPairKey(subjectId, targetId);
            int native;
            run.NativeRelations.TryGetValue(pair, out native);
            bool spouse = ReadString(subject.Data, "spouseId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase);
            bool family = RelationshipSimulationFamily(subject, target);
            bool sameClan = SameSimulationValue(subject.Data, target.Data, "clanId");
            bool sameKingdom = subject.KingdomId.Equals(target.KingdomId, StringComparison.OrdinalIgnoreCase);
            Dictionary<string, double> facets = BuildInitialRelationshipFacets(subjectId, targetId, subject.Data, target.Data,
                subject.Traits, target.Traits, native, spouse, family, sameClan, sameKingdom);
            existing = new RelationshipSimulationRelation { SubjectId = subjectId, TargetId = targetId, Facets = facets };
            run.Relationships[key] = existing;
            return existing;
        }

        private static void RunRelationshipSimulationConsequentialEvent(RelationshipSimulationRun run, int day, Dictionary<string, RelationshipSimulationEdge> present)
        {
            int month = (int)Math.Floor(day / 31.5d);
            Dictionary<string, RelationshipSimulationEdge> edges = new Dictionary<string, RelationshipSimulationEdge>(present, StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, RelationshipSimulationHero> clan in run.Heroes.Values.Where(x => !string.IsNullOrWhiteSpace(ReadString(x.Data, "clanId", ""))).GroupBy(x => ReadString(x.Data, "clanId", ""), StringComparer.OrdinalIgnoreCase))
            {
                List<RelationshipSimulationHero> members = clan.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < members.Count; i++)
                for (int j = i + 1; j < members.Count; j++)
                {
                    string pair = AmbientPairKey(members[i].Id, members[j].Id);
                    if (!edges.ContainsKey(pair))
                    {
                        int native;
                        run.NativeRelations.TryGetValue(pair, out native);
                        edges[pair] = new RelationshipSimulationEdge
                        {
                            PairKey = pair, A = members[i].Id, B = members[j].Id, NativeRelation = native,
                            SameClan = true, SameKingdom = members[i].KingdomId.Equals(members[j].KingdomId, StringComparison.OrdinalIgnoreCase),
                            Spouses = ReadString(members[i].Data, "spouseId", "").Equals(members[j].Id, StringComparison.OrdinalIgnoreCase),
                            Family = RelationshipSimulationFamily(members[i], members[j])
                        };
                    }
                }
            }
            foreach(IGrouping<string,RelationshipSimulationStoryThread> storyPair in run.StoryThreads.Values
                .Where(x=>EffectiveStoryIntensity(x.Intensity,x.Kind,x.Route,x.LastEventDay,day)>0d&&day-x.LastEventDay<=120)
                .GroupBy(x=>StoryPairKey(x.SubjectId,x.TargetId),StringComparer.OrdinalIgnoreCase))
            {
                if(edges.ContainsKey(storyPair.Key))continue;
                RelationshipSimulationStoryThread thread=storyPair.First();
                RelationshipSimulationHero a=run.Heroes[thread.SubjectId],b=run.Heroes[thread.TargetId];
                int native;
                run.NativeRelations.TryGetValue(storyPair.Key,out native);
                edges[storyPair.Key]=new RelationshipSimulationEdge
                {
                    PairKey=storyPair.Key,A=a.Id,B=b.Id,NativeRelation=native,CoLocated=false,
                    SameClan=SameSimulationValue(a.Data,b.Data,"clanId"),SameKingdom=a.KingdomId.Equals(b.KingdomId,StringComparison.OrdinalIgnoreCase),
                    Spouses=ReadString(a.Data,"spouseId","").Equals(b.Id,StringComparison.OrdinalIgnoreCase)||ReadString(b.Data,"spouseId","").Equals(a.Id,StringComparison.OrdinalIgnoreCase),
                    Family=RelationshipSimulationFamily(a,b)
                };
            }
            List<RelationshipSimulationEdge> candidates = new List<RelationshipSimulationEdge>();
            foreach (RelationshipSimulationEdge edge in edges.Values)
            {
                if(edge.CoLocated)run.RomanceFunnelPairs["coLocated"].Add(edge.PairKey);
                RelationshipSimulationHero funnelA=run.Heroes[edge.A],funnelB=run.Heroes[edge.B];
                bool funnelSuitable=ReadDouble(funnelA.Data,"age",0d)>=18d&&ReadDouble(funnelB.Data,"age",0d)>=18d
                    &&ReadBool(funnelA.Data,"isFemale",false)!=ReadBool(funnelB.Data,"isFemale",false)&&!RelationshipSimulationCloseKin(funnelA,funnelB);
                if(funnelSuitable)run.RomanceFunnelPairs["suitable"].Add(edge.PairKey);else run.RomanceBlockedSuitability++;
                int lastDay;
                if (run.LastEventDay.TryGetValue(edge.PairKey, out lastDay) && day - lastDay < DirectorPairCooldownDays){run.RomanceBlockedCooldown++;continue;}
                int aCount, bCount;
                run.HeroMonthEventCounts.TryGetValue(month + "|" + edge.A, out aCount);
                run.HeroMonthEventCounts.TryGetValue(month + "|" + edge.B, out bCount);
                if (aCount >= 2 || bCount >= 2){run.RomanceBlockedMonthlyCap++;continue;}
                RelationshipSimulationRelation ab = EnsureRelationshipSimulationRelation(run, edge.A, edge.B);
                RelationshipSimulationRelation ba = EnsureRelationshipSimulationRelation(run, edge.B, edge.A);
                RelationshipSimulationExposure exposure;
                run.Exposures.TryGetValue(edge.PairKey, out exposure);
                double affinity = (ab.Facets["trust"] + ab.Facets["respect"] + ab.Facets["affection"] + ba.Facets["trust"] + ba.Facets["respect"] + ba.Facets["affection"]) / 600d;
                double tension = (ab.Facets["resentment"] + ab.Facets["rivalry"] + ba.Facets["resentment"] + ba.Facets["rivalry"]) / 400d;
                double exposureScore = Math.Min(0.18d, (exposure == null ? 0d : exposure.WeightedExposure) / 60d);
                edge.AmbientAffinity = affinity;
                edge.AmbientTension = tension;
                edge.AmbientExposure = exposure == null ? 0d : exposure.WeightedExposure;
                edge.Score = 0.10d + (edge.CoLocated ? 0.30d : 0d) + (edge.SameClan ? 0.12d : 0d) + (edge.Spouses ? 0.18d : 0d)
                    + Math.Min(0.20d, Math.Abs(edge.NativeRelation) / 500d) + exposureScore + Math.Min(0.16d, Math.Max(affinity, tension) * 0.16d);
                edge.ContinuationMultiplier = RelationshipSimulationStoryMultiplier(run, edge.A, edge.B, day);
                edge.Score *= edge.ContinuationMultiplier;
                if (edge.Score >= 0.20d) candidates.Add(edge);
            }
            List<IGrouping<string,RelationshipSimulationEdge>> candidateGroups=candidates.GroupBy(
                x => run.Heroes[x.A].KingdomId, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            run.SkippedKingdomCycles+=run.KingdomNames.Keys.Count(key=>!candidateGroups.Any(group=>group.Key.Equals(key,StringComparison.OrdinalIgnoreCase)));
            foreach (IGrouping<string, RelationshipSimulationEdge> kingdomCandidates in candidateGroups)
            {
                string monthKey = month.ToString(CultureInfo.InvariantCulture) + "|" + kingdomCandidates.Key;
                int monthCount = ReadSimulationCount(run.KingdomMonthEventCounts, monthKey);
                List<RelationshipSimulationEdge> pool=kingdomCandidates.OrderByDescending(x=>x.Score).Take(16).ToList();
                RelationshipSimulationEdge selected = SelectRelationshipSimulationCandidate(pool, run.Seed, day, kingdomCandidates.Key);
                if (selected == null)
                {
                    run.SkippedKingdomCycles++;
                    continue;
                }

                Dictionary<string, object> postureA = RelationshipSimulationRomanticPosture(run, selected.A, selected.B, day);
                Dictionary<string, object> postureB = RelationshipSimulationRomanticPosture(run, selected.B, selected.A, day);
                RecordRelationshipSimulationRomanceFunnel(run,selected,postureA,postureB,day);
                string kind = ChooseRelationshipSimulationEventKind(run, selected, postureA, postureB, day);
                string summary = PassiveEventSummary(kind, ReadString(run.Heroes[selected.A].Data, "name", selected.A), ReadString(run.Heroes[selected.B].Data, "name", selected.B));
                double preEventRomanceIntensity=RelationshipSimulationPairStoryIntensity(run,selected.A,selected.B,"romance",day,true);
                ApplyRelationshipSimulationEventDirection(run, selected.A, selected.B, kind, summary, postureA,preEventRomanceIntensity);
                ApplyRelationshipSimulationEventDirection(run, selected.B, selected.A, kind, summary, postureB,preEventRomanceIntensity);
                ApplyRelationshipSimulationStoryEvent(run,kind,selected.A,selected.B,day,postureA,postureB);
                run.LastEventDay[selected.PairKey] = day;
                run.KingdomMonthEventCounts[monthKey] = monthCount + 1;
                run.HeroMonthEventCounts[month + "|" + selected.A] = ReadSimulationCount(run.HeroMonthEventCounts, month + "|" + selected.A) + 1;
                run.HeroMonthEventCounts[month + "|" + selected.B] = ReadSimulationCount(run.HeroMonthEventCounts, month + "|" + selected.B) + 1;
                run.EventCounts[kind] = ReadSimulationCount(run.EventCounts, kind) + 1;
                run.EventCountsByKingdom[kingdomCandidates.Key] = ReadSimulationCount(run.EventCountsByKingdom, kingdomCandidates.Key) + 1;
                run.TotalRandomEvents++;
                if(selected.ContinuationMultiplier>1d)run.ContinuationEvents++;else run.NewPairEvents++;
                if (kind == "secret_affair_intimacy") TryRelationshipSimulationAffairConception(run, selected.A, selected.B, day);
                if (kind == "marital_separation") HandleRelationshipSimulationSeparation(run, selected, day);
            }
        }

        private static RelationshipSimulationEdge SelectRelationshipSimulationCandidate(List<RelationshipSimulationEdge> candidates, int seed, int day, string kingdomId)
        {
            List<RelationshipSimulationEdge> pool = candidates.OrderByDescending(x => x.Score).Take(16).ToList();
            double total = pool.Sum(x => Math.Max(0.05d, x.Score));
            if (pool.Count == 0 || total <= 0d) return null;
            double roll = StableUnit(seed.ToString(CultureInfo.InvariantCulture) + "|candidate|" + day + "|" + kingdomId) * total;
            foreach (RelationshipSimulationEdge candidate in pool)
            {
                roll -= Math.Max(0.05d, candidate.Score);
                if (roll <= 0d) return candidate;
            }
            return pool[pool.Count - 1];
        }

        private static void RecordRelationshipSimulationRomanceFunnel(RelationshipSimulationRun run,RelationshipSimulationEdge edge,
            Dictionary<string,object> postureA,Dictionary<string,object> postureB,int day)
        {
            run.RomanceFunnelPairs["evaluated"].Add(edge.PairKey);
            double baseA=Math.Max(ReadDouble(postureA,"baseGenuineInterest",0d),ReadDouble(postureA,"baseStrategicInterest",0d));
            double baseB=Math.Max(ReadDouble(postureB,"baseGenuineInterest",0d),ReadDouble(postureB,"baseStrategicInterest",0d));
            if(baseA>=25d&&baseB>=25d)run.RomanceFunnelPairs["baseInterest25"].Add(edge.PairKey);
            if(ReadDouble(postureA,"receptivity",0d)>=40d&&ReadDouble(postureB,"receptivity",0d)>=40d)run.RomanceFunnelPairs["receptivity40"].Add(edge.PairKey);
            if(ReadDouble(postureA,"receptivity",0d)>=60d&&ReadDouble(postureB,"receptivity",0d)>=60d)run.RomanceFunnelPairs["receptivity60"].Add(edge.PairKey);
            double thread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"romance",day,true);
            if(thread>=25d)run.RomanceFunnelPairs["thread25"].Add(edge.PairKey);
            if(thread>=50d)run.RomanceFunnelPairs["thread50"].Add(edge.PairKey);
            if(thread>=75d)run.RomanceFunnelPairs["thread75"].Add(edge.PairKey);
            if(thread>=50d&&!edge.CoLocated)run.RomanceBlockedCoLocation++;
            if(thread>=50d&&(ReadDouble(postureA,"receptivity",0d)<60d||ReadDouble(postureB,"receptivity",0d)<60d))run.RomanceBlockedReceptivity++;
            foreach(Dictionary<string,object> posture in new[]{postureA,postureB})
            {
                Dictionary<string,object> continuity=ReadDictionary(posture,"continuity")??new Dictionary<string,object>();
                if(ReadBool(continuity,"eligible",false))
                {
                    run.RomanceContinuityApplications++;
                    run.RomanceContinuityGenuineBonus+=ReadDouble(continuity,"genuineBonus",0d);
                    run.RomanceContinuityStrategicBonus+=ReadDouble(continuity,"strategicBonus",0d);
                    run.RomanceContinuityReceptivityBonus+=ReadDouble(continuity,"receptivityBonus",0d);
                }
                TrackRelationshipSimulationRomanceMetric(run,"genuineInterest",ReadDouble(posture,"genuineInterest",0d));
                TrackRelationshipSimulationRomanceMetric(run,"strategicInterest",ReadDouble(posture,"strategicInterest",0d));
                TrackRelationshipSimulationRomanceMetric(run,"receptivity",ReadDouble(posture,"receptivity",0d));
            }
        }

        private static void TrackRelationshipSimulationRomanceMetric(RelationshipSimulationRun run,string key,double value)
        {
            double prior;
            if(!run.RomanceMetricMinimum.TryGetValue(key,out prior)||value<prior)run.RomanceMetricMinimum[key]=value;
            if(!run.RomanceMetricMaximum.TryGetValue(key,out prior)||value>prior)run.RomanceMetricMaximum[key]=value;
        }

        private static Dictionary<string, object> RelationshipSimulationRomanticPosture(RelationshipSimulationRun run, string actorId, string targetId, int day)
        {
            RelationshipSimulationHero actor = run.Heroes[actorId], target = run.Heroes[targetId];
            RelationshipSimulationRelation relationship = EnsureRelationshipSimulationRelation(run, actorId, targetId);
            string spouseId = ReadString(actor.Data, "spouseId", "");
            Dictionary<string, object> spouseFacets = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(spouseId) && run.Heroes.ContainsKey(spouseId))
                spouseFacets = EnsureRelationshipSimulationRelation(run, actorId, spouseId).Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> characteristics = new Dictionary<string, object> { ["traits"] = actor.Traits };
            Dictionary<string, object> opportunityPayload = new Dictionary<string, object>
            {
                ["opportunitySnapshot"] = new Dictionary<string, object>
                {
                    ["identityKnown"] = true,
                    ["observer"] = actor.Data,
                    ["target"] = target.Data
                }
            };
            Dictionary<string, object> opportunity = CalculatePerceivedOpportunity(actor.Data, characteristics, opportunityPayload, new Dictionary<string, object> { ["identityState"] = "known" });
            bool closeKin = RelationshipSimulationCloseKin(actor, target);
            bool suitable = ReadBool(actor.Data, "isFemale", false) != ReadBool(target.Data, "isFemale", false) && !closeKin;
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["worldDay"] = day,
                ["skipCooldownQuery"] = true,
                ["opportunitySnapshot"] = new Dictionary<string, object>
                {
                    ["identityKnown"] = true,
                    ["observer"] = actor.Data,
                    ["target"] = target.Data,
                    ["suitability"] = new Dictionary<string, object> { ["adults"] = true, ["nativeSuitable"] = suitable, ["closeKin"] = closeKin }
                },
                ["sceneOpportunity"] = new Dictionary<string, object> { ["private"] = true, ["exposure"] = 0.12d, ["coercive"] = false, ["witnessIds"] = new List<string>() }
            };
            RelationshipSimulationStoryThread romanceThread = RelationshipSimulationStory(run, actorId, targetId, "romance", day, false);
            Dictionary<string, object> relationshipContext = new Dictionary<string, object>
            {
                ["facets"] = relationship.Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase)
            };
            if (romanceThread != null)
            {
                double effective = EffectiveStoryIntensity(romanceThread.Intensity, "romance", romanceThread.Route, romanceThread.LastEventDay, day);
                if (effective > 0d) relationshipContext["romanceThread"] = new Dictionary<string, object>
                {
                    ["kind"] = "romance", ["route"] = romanceThread.Route, ["motiveChannel"] = romanceThread.MotiveChannel,
                    ["status"] = "active", ["intensity"] = effective, ["stage"] = StoryStage("romance", effective),
                    ["lastEventDay"] = romanceThread.LastEventDay
                };
            }
            return CalculateRomanticPosture("simulation:" + run.RunId, actorId, targetId, actor.Data, characteristics,
                relationshipContext,
                new Dictionary<string, object> { ["facets"] = spouseFacets }, opportunity, payload, "autonomous_relationship", "A private autonomous social opportunity.");
        }

        private static string ChooseRelationshipSimulationEventKind(RelationshipSimulationRun run, RelationshipSimulationEdge edge, Dictionary<string, object> postureA, Dictionary<string, object> postureB, int day)
        {
            double ambition = Math.Max(0d, (TraitFromProfile(run.Heroes[edge.A].Data, "ambition") + TraitFromProfile(run.Heroes[edge.B].Data, "ambition")
                + TraitFromProfile(run.Heroes[edge.A].Data, "pride") + TraitFromProfile(run.Heroes[edge.B].Data, "pride")) / 8d);
            double favorMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"favor_debt",day);
            double secretMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"shared_secret",day);
            double rivalryMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"rivalry",day);
            double maritalMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"marital_conflict",day);
            double romanceMultiplier=RelationshipSimulationPairStoryMultiplier(run,edge.A,edge.B,"romance",day);
            List<KeyValuePair<string, double>> eligible = new List<KeyValuePair<string, double>>
            {
                new KeyValuePair<string, double>("practical_favor", (24d + 14d * Math.Max(0d, edge.AmbientAffinity) + 8d * Math.Min(1d, edge.AmbientExposure / 30d))*favorMultiplier*CourtCharacterEventWeight(postureA,postureB,"favors","bargaining")),
                new KeyValuePair<string, double>("shared_confidence", (18d + 24d * Math.Max(0d, edge.AmbientAffinity) + 12d * Math.Min(1d, edge.AmbientExposure / 30d))*secretMultiplier*CourtCharacterEventWeight(postureA,postureB,"secrets","information"))
            };
            if (edge.SameClan || edge.Family) eligible.Add(new KeyValuePair<string, double>("family_duty_cooperation", (20d + 18d * Math.Max(0d, edge.AmbientAffinity))*CourtCharacterEventWeight(postureA,postureB,"protection","favors")));
            if (edge.Spouses) eligible.Add(new KeyValuePair<string, double>("private_spousal_confidence", (18d + 22d * Math.Max(0d, edge.AmbientAffinity))*CourtCharacterEventWeight(postureA,postureB,"secrets","mediation")));
            RelationshipSimulationRelation ab = EnsureRelationshipSimulationRelation(run, edge.A, edge.B);
            RelationshipSimulationRelation ba = EnsureRelationshipSimulationRelation(run, edge.B, edge.A);
            double rivalryThread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"rivalry",day,false);
            double maritalThread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"marital_conflict",day,false);
            double romanceThread=RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"romance",day,true);
            double competition = Math.Max(Math.Max(ab.Facets["rivalry"], ba.Facets["rivalry"]),
                Math.Max(Math.Max(ab.Facets["envy"], ba.Facets["envy"]), Math.Max(ab.Facets["resentment"], ba.Facets["resentment"]))) / 100d;
            if (edge.AmbientTension >= 0.025d || ambition >= 0.25d || competition >= 0.10d || rivalryThread>0d)
                eligible.Add(new KeyValuePair<string, double>(rivalryThread>=50d?"political_obstruction":"political_rivalry_argument",
                    (10d + 45d * Math.Max(edge.AmbientTension, competition) + 20d * ambition)*rivalryMultiplier*CourtCharacterEventWeight(postureA,postureB,"rivalry","threats","schemes")));
            if(edge.Spouses&&(edge.AmbientTension>=0.025d||competition>=0.10d||maritalThread>0d))
                eligible.Add(new KeyValuePair<string,double>("marital_argument",(8d+35d*Math.Max(edge.AmbientTension,competition))*maritalMultiplier));
            if (edge.Spouses && (maritalThread>=60d || ab.Facets["resentment"] >= 25d || ba.Facets["resentment"] >= 25d || edge.NativeRelation <= -15
                || ((ab.Facets["resentment"] >= 15d || ba.Facets["resentment"] >= 15d) && (ab.Facets["affection"] <= 0d || ba.Facets["affection"] <= 0d))))
                eligible.Add(new KeyValuePair<string, double>("marital_separation", ((maritalThread>=80d?45d:20d) + 45d * Math.Max(0d, edge.AmbientTension))*maritalMultiplier));
            bool flirt = edge.CoLocated && DirectorRomanceEligible(postureA, 40d) && DirectorRomanceEligible(postureB, 40d);
            if (flirt) eligible.Add(new KeyValuePair<string, double>("mutual_romantic_flirtation", (16d + 12d * Math.Min(1d, edge.AmbientExposure / 30d))*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction")));
            bool confidence=DirectorRomanceEligible(postureA,40d)&&DirectorRomanceEligible(postureB,40d);
            if(confidence&&romanceThread>=25d)eligible.Add(new KeyValuePair<string,double>("romantic_confidence",(14d+10d*Math.Min(1d,edge.AmbientExposure/30d))*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"flirtation","strategic_seduction","secrets")));
            string spouseA=ReadString(run.Heroes[edge.A].Data,"spouseId",""),spouseB=ReadString(run.Heroes[edge.B].Data,"spouseId","");
            bool outsideMarriage=(!string.IsNullOrWhiteSpace(spouseA)&&!spouseA.Equals(edge.B,StringComparison.OrdinalIgnoreCase))
                ||(!string.IsNullOrWhiteSpace(spouseB)&&!spouseB.Equals(edge.A,StringComparison.OrdinalIgnoreCase));
            bool bothUnmarried=string.IsNullOrWhiteSpace(spouseA)&&string.IsNullOrWhiteSpace(spouseB);
            if(StoryPhysicalRomanceEligible(edge.CoLocated,bothUnmarried,romanceThread,50d,postureA,postureB))eligible.Add(new KeyValuePair<string,double>("romantic_intimacy",(12d+8d*Math.Min(1d,edge.AmbientExposure/30d))*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"strategic_seduction","flirtation")));
            if (StoryPhysicalRomanceEligible(edge.CoLocated,outsideMarriage,romanceThread,75d,postureA,postureB)) eligible.Add(new KeyValuePair<string, double>("secret_affair_intimacy", (10d + 8d * Math.Min(1d, edge.AmbientExposure / 30d))*romanceMultiplier*CourtCharacterEventWeight(postureA,postureB,"strategic_seduction")));
            return SelectWeightedPassiveEvent(eligible, run.Seed.ToString(CultureInfo.InvariantCulture) + "|" + edge.PairKey + "|" + Math.Floor(day / DirectorRunIntervalDays));
        }

        private static void ApplyRelationshipSimulationEventDirection(RelationshipSimulationRun run, string subjectId, string targetId, string eventType, string summary,
            Dictionary<string, object> posture,double preEventRomanceIntensity)
        {
            RelationshipSimulationRelation relation = EnsureRelationshipSimulationRelation(run, subjectId, targetId);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["motiveDecision"] = new Dictionary<string, object> { ["romance"] = posture },
                ["preEventRomanceIntensity"] = preEventRomanceIntensity
            };
            Dictionary<string, object> evaluation = DeterministicRelationshipEvaluation(eventType, summary, payload,
                relation.Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase));
            string impact = ReadString(evaluation, "impactLevel", "ordinary").ToLowerInvariant();
            int maxDelta = impact == "transformative" ? 60 : impact == "major" ? 30 : 10;
            Dictionary<string, object> raw = ReadDictionary(evaluation, "facetDeltas") ?? new Dictionary<string, object>();
            foreach (string facet in raw.Keys.Where(x => RelationshipFacetKeys.Contains(x, StringComparer.OrdinalIgnoreCase) && Math.Abs(ReadDouble(raw, x, 0d)) > 0.001d).Take(8))
                relation.Facets[facet] = ClampRelationship(relation.Facets[facet] + ClampDouble(ReadDouble(raw, facet, 0d), -maxDelta, maxDelta));
            int nativeDelta = ReadInt(evaluation, "nativeRelationDelta", 0);
            if (nativeDelta != 0)
            {
                string pair = AmbientPairKey(subjectId, targetId);
                int prior;
                run.NativeRelations.TryGetValue(pair, out prior);
                run.NativeRelations[pair] = Clamp(prior + Clamp(nativeDelta, -2, 2), -100, 100);
            }
        }

        private static void RunRelationshipSimulationMarriageSystem(RelationshipSimulationRun run, int day, Dictionary<string, RelationshipSimulationEdge> present)
        {
            foreach (RelationshipSimulationEdge edge in present.Values)
            {
                if (edge.Spouses || edge.Family) continue;
                RelationshipSimulationHero a = run.Heroes[edge.A], b = run.Heroes[edge.B];
                if (!RelationshipSimulationMarriageEligible(a, b)) continue;
                if (RomanticMarriageReady(RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(run, edge.A, edge.B)),
                    RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(run, edge.B, edge.A))))
                    MarryRelationshipSimulationHeroes(run, a, b, "romantic",day);
            }

            int period = (int)Math.Floor(day / 7d);
            foreach (Dictionary<string, object> clan in run.Clans.Values.OrderBy(x => ReadString(x, "clanId", ""), StringComparer.OrdinalIgnoreCase))
            {
                string clanId = ReadString(clan, "clanId", ""), leaderId = ReadString(clan, "leaderId", "");
                string rollKey = period + "|" + leaderId;
                if (string.IsNullOrWhiteSpace(leaderId) || run.MarriageRolls.Contains(rollKey) || !run.Heroes.ContainsKey(leaderId)) continue;
                run.MarriageRolls.Add(rollKey);
                RelationshipSimulationHero leader = run.Heroes[leaderId];
                List<RelationshipSimulationHero> members = run.Heroes.Values.Where(x => ReadString(x.Data, "clanId", "").Equals(clanId, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ReadString(x.Data, "spouseId", ""))).ToList();
                double urgency = MarriageUrgency(clan, leader.Data, members.Select(x => x.Data).ToList());
                double chance = ClampDouble(0.01d + 0.04d * urgency, 0.01d, 0.05d);
                if (members.Count == 0 || StableUnit(run.Seed.ToString(CultureInfo.InvariantCulture) + "|marriage|" + period + "|" + leaderId) >= chance) continue;
                RelationshipSimulationHero best = null, other = null;
                Dictionary<string, object> bestOtherClan = null;
                double bestScore = double.MinValue;
                foreach (RelationshipSimulationHero member in members)
                foreach (RelationshipSimulationHero candidate in run.Heroes.Values.Where(x => !ReadString(x.Data, "clanId", "").Equals(clanId, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(ReadString(x.Data, "spouseId", ""))))
                {
                    if (!RelationshipSimulationMarriageEligible(member, candidate)) continue;
                    Dictionary<string, object> candidateClan;
                    if (!run.Clans.TryGetValue(ReadString(candidate.Data, "clanId", ""), out candidateClan)) continue;
                    double score = DynasticMarriageScore(clan, candidateClan, leader.Data, member.Data, candidate.Data)
                        *RelationshipSimulationArrangedCourtshipMultiplier(run,member.Id,candidate.Id,day);
                    if (score > bestScore) { bestScore = score; best = member; other = candidate; bestOtherClan = candidateClan; }
                }
                if (best != null && other != null && bestOtherClan != null && bestScore >= 25d)
                {
                    RelationshipSimulationHero otherLeader;
                    double otherApproval = run.Heroes.TryGetValue(ReadString(bestOtherClan, "leaderId", ""), out otherLeader)
                        ? DynasticMarriageScore(bestOtherClan, clan, otherLeader.Data, other.Data, best.Data)
                            *RelationshipSimulationArrangedCourtshipMultiplier(run,best.Id,other.Id,day) : 0d;
                    if (otherApproval >= 25d) MarryRelationshipSimulationHeroes(run, best, other, "arranged",day);
                }
            }
        }

        private static double RelationshipSimulationThirdPartyCourtshipMultiplier(RelationshipSimulationRun run,string heroId,string proposedSpouseId,int day)
        {
            double strongest=run.StoryThreads.Values.Where(x=>x.SubjectId.Equals(heroId,StringComparison.OrdinalIgnoreCase)
                    &&x.Kind=="romance"&&x.Route=="courtship"&&!x.TargetId.Equals(proposedSpouseId,StringComparison.OrdinalIgnoreCase))
                .Select(x=>RelationshipSimulationPairStoryIntensity(run,heroId,x.TargetId,"romance",day,true)).DefaultIfEmpty(0d).Max();
            return strongest>=75d?0.50d:strongest>=50d?0.75d:1d;
        }

        private static double RelationshipSimulationArrangedCourtshipMultiplier(RelationshipSimulationRun run,string a,string b,int day)
        {
            return Math.Min(RelationshipSimulationThirdPartyCourtshipMultiplier(run,a,b,day),
                RelationshipSimulationThirdPartyCourtshipMultiplier(run,b,a,day));
        }

        private static int ConvertRelationshipSimulationDisplacedCourtships(RelationshipSimulationRun run,string newlywed,string spouse,int day)
        {
            List<string> partners=run.StoryThreads.Values.Where(x=>x.SubjectId.Equals(newlywed,StringComparison.OrdinalIgnoreCase)
                    &&x.Kind=="romance"&&x.Route=="courtship"&&!x.TargetId.Equals(spouse,StringComparison.OrdinalIgnoreCase))
                .Select(x=>x.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int converted=0;
            foreach(string partner in partners)
            {
                if(RelationshipSimulationPairStoryIntensity(run,newlywed,partner,"romance",day,true)<50d)continue;
                RelationshipSimulationStoryThread a=RelationshipSimulationStory(run,newlywed,partner,"romance",day,false);
                RelationshipSimulationStoryThread b=RelationshipSimulationStory(run,partner,newlywed,"romance",day,false);
                if(a!=null){a.Route="temptation";a.LastEventType="arranged_marriage_displacement";a.LastEventDay=day;}
                if(b!=null){b.Route="temptation";b.LastEventType="arranged_marriage_displacement";b.LastEventDay=day;}
                converted++;
            }
            return converted;
        }

        private static void MarryRelationshipSimulationHeroes(RelationshipSimulationRun run, RelationshipSimulationHero a, RelationshipSimulationHero b, string route,int day)
        {
            if (!RelationshipSimulationMarriageEligible(a, b)) return;
            a.Data["spouseId"] = b.Id;
            b.Data["spouseId"] = a.Id;
            run.EventCounts["marriages"] = ReadSimulationCount(run.EventCounts, "marriages") + 1;
            run.EventCounts[route + "_marriages"] = ReadSimulationCount(run.EventCounts, route + "_marriages") + 1;
            EnsureRelationshipSimulationRelation(run, a.Id, b.Id);
            EnsureRelationshipSimulationRelation(run, b.Id, a.Id);
            if(route=="arranged")
            {
                int displaced=ConvertRelationshipSimulationDisplacedCourtships(run,a.Id,b.Id,day)
                    +ConvertRelationshipSimulationDisplacedCourtships(run,b.Id,a.Id,day);
                run.CourtshipDisplacements+=displaced;
                if(displaced>0)run.EventCounts["courtship_displacements"]=ReadSimulationCount(run.EventCounts,"courtship_displacements")+displaced;
            }
        }

        private static void HandleRelationshipSimulationSeparation(RelationshipSimulationRun run, RelationshipSimulationEdge edge, int day)
        {
            if (!edge.Spouses) return;
            double conflict=Math.Max(60d,RelationshipSimulationPairStoryIntensity(run,edge.A,edge.B,"marital_conflict",day,false));
            RelationshipSimulationReadiness readiness;
            if (!run.DivorceReadiness.TryGetValue(edge.PairKey, out readiness))
            {
                readiness = new RelationshipSimulationReadiness { FirstDay = day, Score = conflict };
                run.DivorceReadiness[edge.PairKey] = readiness;
            }
            else readiness.Score = conflict;
            if (day - readiness.FirstDay < 30 || readiness.Score < 80d || readiness.Triggered) return;
            run.Heroes[edge.A].Data["spouseId"] = "";
            run.Heroes[edge.B].Data["spouseId"] = "";
            readiness.Triggered = true;
            run.EventCounts["divorces"] = ReadSimulationCount(run.EventCounts, "divorces") + 1;
        }

        private static void TryRelationshipSimulationAffairConception(RelationshipSimulationRun run, string aId, string bId, int day)
        {
            RelationshipSimulationHero a = run.Heroes[aId], b = run.Heroes[bId];
            RelationshipSimulationHero mother = ReadBool(a.Data, "isFemale", false) && !ReadBool(b.Data, "isFemale", false) ? a
                : ReadBool(b.Data, "isFemale", false) && !ReadBool(a.Data, "isFemale", false) ? b : null;
            RelationshipSimulationHero father = mother == a ? b : mother == b ? a : null;
            if (mother == null || father == null || ReadBool(mother.Data, "isPregnant", false)) return;
            double age = ReadDouble(mother.Data, "age", 0d);
            if (age < 18d || age > 45d) return;
            double chance = NpcConceptionChance(age,
                ReadInt(mother.Data, "childrenCount", 0));
            int affairIndex = ReadSimulationCount(run.EventCounts, "secret_affair_intimacy");
            if (StableUnit(run.Seed.ToString(CultureInfo.InvariantCulture) + "|affair_conception|" + day + "|" + mother.Id + "|" + father.Id + "|" + affairIndex) < chance)
            {
                mother.Data["isPregnant"] = true;
                run.EventCounts["affair_conceptions"] = ReadSimulationCount(run.EventCounts, "affair_conceptions") + 1;
            }
        }

        private static bool RelationshipSimulationMarriageEligible(RelationshipSimulationHero a, RelationshipSimulationHero b)
        {
            if (a == null || b == null || a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) return false;
            if (!ReadBool(a.Data, "isAlive", true) || !ReadBool(b.Data, "isAlive", true) || ReadDouble(a.Data, "age", 0d) < 18d || ReadDouble(b.Data, "age", 0d) < 18d) return false;
            if (ReadBool(a.Data, "isFemale", false) == ReadBool(b.Data, "isFemale", false)) return false;
            if (!string.IsNullOrWhiteSpace(ReadString(a.Data, "spouseId", "")) || !string.IsNullOrWhiteSpace(ReadString(b.Data, "spouseId", ""))) return false;
            return !RelationshipSimulationCloseKin(a, b);
        }

        private static bool RelationshipSimulationCloseKin(RelationshipSimulationHero a, RelationshipSimulationHero b)
        {
            if (ReadString(a.Data, "fatherId", "").Equals(b.Id, StringComparison.OrdinalIgnoreCase) || ReadString(a.Data, "motherId", "").Equals(b.Id, StringComparison.OrdinalIgnoreCase)
                || ReadString(b.Data, "fatherId", "").Equals(a.Id, StringComparison.OrdinalIgnoreCase) || ReadString(b.Data, "motherId", "").Equals(a.Id, StringComparison.OrdinalIgnoreCase)) return true;
            string af = ReadString(a.Data, "fatherId", ""), bf = ReadString(b.Data, "fatherId", ""), am = ReadString(a.Data, "motherId", ""), bm = ReadString(b.Data, "motherId", "");
            return (!string.IsNullOrWhiteSpace(af) && af.Equals(bf, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(am) && am.Equals(bm, StringComparison.OrdinalIgnoreCase));
        }

        private static bool RelationshipSimulationFamily(RelationshipSimulationHero a, RelationshipSimulationHero b)
        {
            return ReadString(a.Data, "spouseId", "").Equals(b.Id, StringComparison.OrdinalIgnoreCase)
                || ReadString(b.Data, "spouseId", "").Equals(a.Id, StringComparison.OrdinalIgnoreCase)
                || RelationshipSimulationCloseKin(a, b);
        }

        private static bool SameSimulationValue(Dictionary<string, object> a, Dictionary<string, object> b, string key)
        {
            string value = ReadString(a, key, "");
            return !string.IsNullOrWhiteSpace(value) && value.Equals(ReadString(b, key, ""), StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> RelationshipSimulationFacetObject(RelationshipSimulationRelation relation)
        {
            return relation.Facets.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static int ReadSimulationCount(Dictionary<string, int> counts, string key)
        {
            int value;
            return counts.TryGetValue(key, out value) ? value : 0;
        }

        private static void CaptureRelationshipSimulationWeek(RelationshipSimulationRun run, int day)
        {
            Dictionary<string, object> facetBands = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string facet in RelationshipFacetKeys)
                facetBands[facet] = RelationshipSimulationBands(run, facet, "");
            run.Timeline.Add(new Dictionary<string, object>
            {
                ["day"] = day,
                ["completedDays"] = run.CompletedDays,
                ["directionalRelationshipCount"] = run.Relationships.Count,
                ["randomEventCount"] = run.TotalRandomEvents,
                ["majorEventCount"] = ReadSimulationCount(run.EventCounts, "marriages") + ReadSimulationCount(run.EventCounts, "divorces")
                    + ReadSimulationCount(run.EventCounts, "secret_affair_intimacy") + ReadSimulationCount(run.EventCounts, "affair_conceptions"),
                ["facetBands"] = facetBands
            });
            if (run.Timeline.Count > 600) run.Timeline.RemoveAt(0);
        }

        private static List<Dictionary<string, object>> RelationshipSimulationBands(RelationshipSimulationRun run, string facet, string kingdomId)
        {
            int[][] ranges =
            {
                new[] {-100,-81}, new[] {-80,-61}, new[] {-60,-41}, new[] {-40,-21}, new[] {-20,20},
                new[] {21,40}, new[] {41,60}, new[] {61,80}, new[] {81,100}
            };
            List<RelationshipSimulationRelation> relations = RelationshipSimulationFilteredRelations(run, kingdomId);
            return ranges.Select(range =>
            {
                List<RelationshipSimulationRelation> inBand = relations.Where(x => x.Facets[facet] >= range[0] && x.Facets[facet] <= range[1]).ToList();
                return new Dictionary<string, object>
                {
                    ["min"] = range[0], ["max"] = range[1], ["label"] = range[0].ToString(CultureInfo.InvariantCulture) + ".." + range[1].ToString(CultureInfo.InvariantCulture),
                    ["directionalRelationshipCount"] = inBand.Count,
                    ["distinctNpcCount"] = inBand.Select(x => x.SubjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                };
            }).ToList();
        }

        private static List<RelationshipSimulationRelation> RelationshipSimulationFilteredRelations(RelationshipSimulationRun run, string kingdomId)
        {
            IEnumerable<RelationshipSimulationRelation> query = run.Relationships.Values;
            if (!string.IsNullOrWhiteSpace(kingdomId))
                query = query.Where(x => run.Heroes.ContainsKey(x.SubjectId) && run.Heroes[x.SubjectId].KingdomId.Equals(kingdomId, StringComparison.OrdinalIgnoreCase));
            return query.ToList();
        }

        private static Dictionary<string, object> RelationshipSimulationStatusDictionary(RelationshipSimulationRun run, string selectedFacet, string selectedKingdom)
        {
            if (!RelationshipFacetKeys.Contains(selectedFacet, StringComparer.OrdinalIgnoreCase)) selectedFacet = "trust";
            List<RelationshipSimulationRelation> filtered = RelationshipSimulationFilteredRelations(run, selectedKingdom);
            List<Dictionary<string, object>> exact = filtered.GroupBy(x => (int)Math.Round(x.Facets[selectedFacet], MidpointRounding.AwayFromZero))
                .OrderBy(x => x.Key)
                .Select(x => new Dictionary<string, object>
                {
                    ["level"] = x.Key,
                    ["directionalRelationshipCount"] = x.Count(),
                    ["distinctNpcCount"] = x.Select(y => y.SubjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                }).ToList();
            List<Dictionary<string, object>> kingdoms = run.Heroes.Values.GroupBy(x => x.KingdomId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => run.RealCityCounts.ContainsKey(x.Key) ? run.RealCityCounts[x.Key] : 0).ThenBy(x => run.KingdomNames[x.Key], StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                {
                    List<string> locations = run.LocationsByKingdom[x.Key];
                    List<RelationshipSimulationRelation> kingdomRelations = RelationshipSimulationFilteredRelations(run, x.Key);
                    int kingdomEvents = ReadSimulationCount(run.EventCountsByKingdom, x.Key);
                    return new Dictionary<string, object>
                    {
                        ["kingdomId"] = x.Key, ["name"] = run.KingdomNames[x.Key],
                        ["cityCount"] = run.RealCityCounts[x.Key], ["simulationLocationCount"] = locations.Count, ["npcCount"] = x.Count(),
                        ["randomEventCount"] = kingdomEvents,
                        ["eventsPer100Npcs"] = Math.Round(100d * kingdomEvents / Math.Max(1, x.Count()), 2),
                        ["selectedFacetMean"] = kingdomRelations.Count == 0 ? 0d : Math.Round(kingdomRelations.Average(relation => relation.Facets[selectedFacet]), 2),
                        ["locations"] = locations.Select(location => new Dictionary<string, object>
                        {
                            ["locationId"] = location, ["npcCount"] = x.Count(hero => hero.LocationId.Equals(location, StringComparison.OrdinalIgnoreCase))
                        }).ToList()
                    };
                }).ToList();
            List<Dictionary<string, object>> cityCountCohorts = kingdoms.GroupBy(x => ReadInt(x, "cityCount", 0)).OrderBy(x => x.Key)
                .Select(x =>
                {
                    List<string> kingdomIds = x.Select(y => ReadString(y, "kingdomId", "")).ToList();
                    List<RelationshipSimulationRelation> cohortRelations = run.Relationships.Values.Where(relation =>
                        run.Heroes.ContainsKey(relation.SubjectId) && kingdomIds.Contains(run.Heroes[relation.SubjectId].KingdomId, StringComparer.OrdinalIgnoreCase)).ToList();
                    int cohortEvents = kingdomIds.Sum(id => ReadSimulationCount(run.EventCountsByKingdom, id));
                    int cohortNpcs = x.Sum(y => ReadInt(y, "npcCount", 0));
                    return new Dictionary<string, object>
                    {
                        ["cityCount"] = x.Key, ["kingdomCount"] = x.Count(), ["npcCount"] = cohortNpcs,
                        ["kingdomIds"] = kingdomIds, ["directionalRelationshipCount"] = cohortRelations.Count,
                        ["selectedFacetMean"] = cohortRelations.Count == 0 ? 0d : Math.Round(cohortRelations.Average(relation => relation.Facets[selectedFacet]), 2),
                        ["negativeRelationshipCount"] = cohortRelations.Count(relation => relation.Facets[selectedFacet] <= -21d),
                        ["centralRelationshipCount"] = cohortRelations.Count(relation => relation.Facets[selectedFacet] >= -20d && relation.Facets[selectedFacet] <= 20d),
                        ["positiveRelationshipCount"] = cohortRelations.Count(relation => relation.Facets[selectedFacet] >= 21d),
                        ["randomEventCount"] = cohortEvents,
                        ["eventsPer100Npcs"] = Math.Round(100d * cohortEvents / Math.Max(1, cohortNpcs), 2)
                    };
                }).ToList();
            double pairExposureDays = run.Exposures.Values.Sum(x => x.WeightedExposure / Math.Max(0.0001d, AmbientSettlementExposure));
            double selectedMean = filtered.Count == 0 ? 0d : filtered.Average(x => x.Facets[selectedFacet]);
            List<Dictionary<string,object>> threadRows=run.StoryThreads.Values.Select(thread=>
            {
                double effective=EffectiveStoryIntensity(thread.Intensity,thread.Kind,thread.Route,thread.LastEventDay,run.CurrentDay);
                return new Dictionary<string,object>{{"pairKey",StoryPairKey(thread.SubjectId,thread.TargetId)},{"subjectId",thread.SubjectId},{"targetId",thread.TargetId},
                    {"kind",thread.Kind},{"route",thread.Route},{"motiveChannel",thread.MotiveChannel},{"intensity",Math.Round(effective,2)},{"stage",StoryStage(thread.Kind,effective)},{"lastEventDay",thread.LastEventDay}};
            }).Where(x=>ReadDouble(x,"intensity",0d)>0d).ToList();
            List<Dictionary<string,object>> threadCounts=threadRows.GroupBy(x=>ReadString(x,"kind","")+"|"+ReadString(x,"stage",""),StringComparer.OrdinalIgnoreCase)
                .Select(group=>new Dictionary<string,object>{{"kind",group.Key.Split('|')[0]},{"stage",group.Key.Split('|')[1]},{"directionalThreadCount",group.Count()},
                    {"pairCount",group.Select(x=>ReadString(x,"pairKey","")).Distinct(StringComparer.OrdinalIgnoreCase).Count()}}).ToList();
            int courtships=threadRows.Where(x=>ReadString(x,"kind","")=="romance"&&ReadString(x,"route","")=="courtship"&&ReadDouble(x,"intensity",0d)>=50d)
                .Select(x=>ReadString(x,"pairKey","")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int temptations=threadRows.Where(x=>ReadString(x,"kind","")=="romance"&&ReadString(x,"route","")=="temptation"&&ReadDouble(x,"intensity",0d)>=50d)
                .Select(x=>ReadString(x,"pairKey","")).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Dictionary<string,object> romanceRanges=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
            foreach(string facet in new[]{"attraction","affection","trust"})
            {
                List<double> values=filtered.Select(x=>x.Facets[facet]).ToList();
                romanceRanges[facet]=new Dictionary<string,object>{{"min",values.Count==0?0d:Math.Round(values.Min(),2)},
                    {"max",values.Count==0?0d:Math.Round(values.Max(),2)},{"mean",values.Count==0?0d:Math.Round(values.Average(),2)}};
            }
            foreach(string metric in new[]{"genuineInterest","strategicInterest","receptivity"})
                romanceRanges[metric]=new Dictionary<string,object>{{"min",run.RomanceMetricMinimum.ContainsKey(metric)?Math.Round(run.RomanceMetricMinimum[metric],2):0d},
                    {"max",run.RomanceMetricMaximum.ContainsKey(metric)?Math.Round(run.RomanceMetricMaximum[metric],2):0d}};
            double continuityDivisor=Math.Max(1,run.RomanceContinuityApplications);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["modelVersion"] = RelationshipSimulationModelVersion, ["runId"] = run.RunId,
                ["campaignId"] = run.CampaignId, ["timelineId"] = run.TimelineId, ["state"] = run.State,
                ["error"] = run.Error, ["startedUtc"] = run.StartedUtc, ["completedUtc"] = run.CompletedUtc,
                ["startDay"] = run.StartDay, ["currentDay"] = run.CurrentDay, ["durationDays"] = run.DurationDays,
                ["completedDays"] = run.CompletedDays, ["progress"] = Math.Round(run.Progress, 4),
                ["seed"] = run.Seed, ["npcCount"] = run.Heroes.Count, ["kingdomCount"] = kingdoms.Count,
                ["realCityCount"] = kingdoms.Sum(x => ReadInt(x, "cityCount", 0)),
                ["simulationLocationCount"] = kingdoms.Sum(x => ReadInt(x, "simulationLocationCount", 0)),
                ["directionalRelationshipCount"] = run.Relationships.Count,
                ["unorderedPairCount"] = run.Relationships.Keys.Select(x => AmbientPairKey(x.Split('|')[0], x.Split('|')[1])).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["relocations"] = run.Relocations, ["nativeRelationChanges"] = run.NativeRelationChanges,
                ["positiveNativeRelationChanges"] = run.PositiveNativeRelationChanges,
                ["negativeNativeRelationChanges"] = run.NegativeNativeRelationChanges,
                ["randomEventCount"] = run.TotalRandomEvents, ["llmCallCount"] = 0, ["noLlmConfirmed"] = true,
                ["eventPolicy"] = "one_guaranteed_event_per_eligible_kingdom_every_two_days",
                ["continuationEventCount"] = run.ContinuationEvents, ["newPairEventCount"] = run.NewPairEvents,
                ["skippedKingdomCycles"] = run.SkippedKingdomCycles,
                ["passivePositiveDriftByFacet"] = run.PassivePositiveDrift.ToDictionary(x=>x.Key,x=>(object)Math.Round(x.Value,3),StringComparer.OrdinalIgnoreCase),
                ["passiveNegativeDriftByFacet"] = run.PassiveNegativeDrift.ToDictionary(x=>x.Key,x=>(object)Math.Round(x.Value,3),StringComparer.OrdinalIgnoreCase),
                ["threadCounts"] = threadCounts,
                ["romanceFunnel"] = new Dictionary<string,object>
                {
                    ["uniquePairs"]=run.RomanceFunnelPairs.ToDictionary(x=>x.Key,x=>(object)x.Value.Count,StringComparer.OrdinalIgnoreCase),
                    ["continuity"]=new Dictionary<string,object>{{"directionalApplicationCount",run.RomanceContinuityApplications},
                        {"averageGenuineBonus",Math.Round(run.RomanceContinuityGenuineBonus/continuityDivisor,2)},
                        {"averageStrategicBonus",Math.Round(run.RomanceContinuityStrategicBonus/continuityDivisor,2)},
                        {"averageReceptivityBonus",Math.Round(run.RomanceContinuityReceptivityBonus/continuityDivisor,2)}},
                    ["blockedEvaluations"]=new Dictionary<string,object>{{"suitability",run.RomanceBlockedSuitability},{"coLocation",run.RomanceBlockedCoLocation},
                        {"receptivity60",run.RomanceBlockedReceptivity},{"pairCooldown",run.RomanceBlockedCooldown},
                        {"monthlyCharacterCap",run.RomanceBlockedMonthlyCap},{"arrangedMarriageDisplacement",run.CourtshipDisplacements}},
                    ["ranges"]=romanceRanges
                },
                ["pairExposureDays"] = Math.Round(pairExposureDays, 1),
                ["eventParticipationsPerNpc"] = Math.Round(2d * run.TotalRandomEvents / Math.Max(1, run.Heroes.Count), 3),
                ["eventsPer100NpcDays"] = Math.Round(100d * run.TotalRandomEvents / Math.Max(1d, run.Heroes.Count * Math.Max(1, run.CompletedDays)), 4),
                ["eventsPer1000PairExposureDays"] = Math.Round(1000d * run.TotalRandomEvents / Math.Max(1d, pairExposureDays), 4),
                ["eventCounts"] = run.EventCounts.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase),
                ["eventCountsByKingdom"] = run.EventCountsByKingdom.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase),
                ["majorEvents"] = new Dictionary<string, object>
                {
                    ["affairs"] = ReadSimulationCount(run.EventCounts, "secret_affair_intimacy"),
                    ["divorces"] = ReadSimulationCount(run.EventCounts, "divorces"),
                    ["marriages"] = ReadSimulationCount(run.EventCounts, "marriages"),
                    ["romanticMarriages"] = ReadSimulationCount(run.EventCounts, "romantic_marriages"),
                    ["arrangedMarriages"] = ReadSimulationCount(run.EventCounts, "arranged_marriages"),
                    ["courtships"] = courtships,
                    ["temptations"] = temptations,
                    ["romanticIntimacy"] = ReadSimulationCount(run.EventCounts,"romantic_intimacy"),
                    ["feuds"] = ReadSimulationCount(run.EventCounts,"feuds_started"),
                    ["maritalArguments"] = ReadSimulationCount(run.EventCounts,"marital_argument"),
                    ["maritalSeparations"] = ReadSimulationCount(run.EventCounts, "marital_separation"),
                    ["affairConceptions"] = ReadSimulationCount(run.EventCounts, "affair_conceptions")
                },
                ["selectedFacet"] = selectedFacet, ["selectedKingdomId"] = selectedKingdom,
                ["selectedDistribution"] = new Dictionary<string, object>
                {
                    ["exactLevels"] = exact,
                    ["bands"] = RelationshipSimulationBands(run, selectedFacet, selectedKingdom),
                    ["directionalRelationshipCount"] = filtered.Count,
                    ["distinctNpcCount"] = filtered.Select(x => x.SubjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ["mean"] = Math.Round(selectedMean, 2)
                },
                ["kingdoms"] = kingdoms, ["cityCountCohorts"] = cityCountCohorts,
                ["timeline"] = run.Timeline.Select(row => new Dictionary<string, object>
                {
                    ["day"] = ReadInt(row, "day", 0),
                    ["completedDays"] = ReadInt(row, "completedDays", 0),
                    ["directionalRelationshipCount"] = ReadInt(row, "directionalRelationshipCount", 0),
                    ["randomEventCount"] = ReadInt(row, "randomEventCount", 0),
                    ["majorEventCount"] = ReadInt(row, "majorEventCount", 0),
                    ["facetBands"] = new Dictionary<string, object>
                    {
                        [selectedFacet] = ReadDictionaryList(ReadDictionary(row, "facetBands"), selectedFacet)
                    }
                }).ToList(),
                ["relocationHistory"] = run.RelocationHistory
            };
        }

        private static void PersistRelationshipSimulation(RelationshipSimulationRun run)
        {
            if (!run.Persist) return;
            string directory = RelationshipSimulationRunDirectory(run.RunId);
            Directory.CreateDirectory(directory);
            Dictionary<string, object> status = RelationshipSimulationStatusDictionary(run, "trust", "");
            WriteJsonObject(Path.Combine(directory, "status.json"), status);
            WriteJsonObject(Path.Combine(RelationshipSimulationRoot(), "latest.json"), new Dictionary<string, object>
            {
                ["runId"] = run.RunId, ["campaignId"] = run.CampaignId, ["state"] = run.State,
                ["statusPath"] = Path.Combine(directory, "status.json"), ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            });
        }

        private static List<Dictionary<string, object>> RunRelationshipSimulationSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["summary"] = summary });
            Dictionary<string, object> traits = CoreTraitKeys.ToDictionary(x => x, x => (object)0, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>();
            for (int i = 0; i < 12; i++)
            {
                bool first = i < 8;
                heroes.Add(new Dictionary<string, object>
                {
                    ["heroStringId"] = "sim_hero_" + i, ["name"] = "Hero " + i, ["age"] = 24 + i, ["isAlive"] = true,
                    ["isFemale"] = i % 2 == 0, ["isLord"] = true, ["kingdomId"] = first ? "kingdom_a" : "kingdom_b",
                    ["clanId"] = (first ? "clan_a_" : "clan_b_") + (i / 2), ["spouseId"] = "",
                    ["foundationTraits"] = traits
                });
            }
            Dictionary<string, object> census = new Dictionary<string, object>
            {
                ["campaignId"] = "relationship_simulation_self_test", ["worldDay"] = 100d, ["heroes"] = heroes,
                ["kingdoms"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { ["kingdomId"] = "kingdom_a", ["name"] = "Kingdom A", ["cityIds"] = new List<string> { "a1", "a2", "a3" } },
                    new Dictionary<string, object> { ["kingdomId"] = "kingdom_b", ["name"] = "Kingdom B", ["cityIds"] = new List<string> { "b1" } }
                },
                ["clans"] = new List<Dictionary<string, object>>()
            };
            RelationshipSimulationRun one = CreateRelationshipSimulation(census, 28, 77, false);
            RelationshipSimulationRun two = CreateRelationshipSimulation(census, 28, 77, false);
            for (int offset = 0; offset < 28; offset++)
            {
                if (offset > 0 && offset % 7 == 0) { AssignRelationshipSimulationLocations(one, offset / 7); AssignRelationshipSimulationLocations(two, offset / 7); }
                RunRelationshipSimulationDay(one, one.StartDay + offset);
                RunRelationshipSimulationDay(two, two.StartDay + offset);
                one.CompletedDays = two.CompletedDays = offset + 1;
            }
            add("kingdom_city_grouping", one.RealCityCounts["kingdom_a"] == 3 && one.RealCityCounts["kingdom_b"] == 1 && one.Heroes.Count == 12,
                "All eligible NPCs are grouped by kingdom and each kingdom retains its real city count.");
            add("seven_day_relocation", one.RelocationHistory.Count == 4 && one.RelocationHistory.All(x => (ReadInt(x, "day", 0) - one.StartDay) % 7 == 0)
                && one.Heroes.Values.All(x => one.LocationsByKingdom[x.KingdomId].Contains(x.LocationId, StringComparer.OrdinalIgnoreCase)),
                "Locations reshuffle only on seven-day boundaries and never cross kingdom membership.");
            string fingerprintOne = RelationshipSimulationFingerprint(one), fingerprintTwo = RelationshipSimulationFingerprint(two);
            add("seed_determinism", fingerprintOne == fingerprintTwo, "Identical census and seed produce identical relationships, relocations, and event totals.");
            Dictionary<string, object> status = RelationshipSimulationStatusDictionary(one, "trust", "");
            Dictionary<string, object> selectedDistribution = ReadDictionary(status, "selectedDistribution") ?? new Dictionary<string, object>();
            add("exact_and_banded_distributions", ReadDictionaryList(selectedDistribution, "exactLevels").Count > 0
                && ReadDictionaryList(selectedDistribution, "bands").Count == 9,
                "The monitor exposes exact integer levels and readable -100..100 bands.");
            add("all_relationship_facets", RelationshipFacetKeys.All(x => one.Relationships.Values.All(r => r.Facets.ContainsKey(x))),
                "Every directional relationship tracks every production relationship facet.");
            add("no_llm", ReadInt(status, "llmCallCount", -1) == 0 && ReadBool(status, "noLlmConfirmed", false),
                "Simulation event generation and directional evaluation make no LLM calls.");
            add("random_event_evaluator", ReadDictionary(DeterministicRelationshipEvaluation("political_rivalry_argument", "Two nobles argued over status.", new Dictionary<string, object>(), new Dictionary<string, object>()), "facetDeltas") != null,
                "Random simulation events use the production deterministic relationship evaluator.");
            Dictionary<string,object> sincereRomance=new Dictionary<string,object>{{"presentation","sincere"},{"genuineInterest",65d},{"strategicInterest",20d},
                {"hardConstraints",new Dictionary<string,object>{{"coercive",false}}}};
            Dictionary<string,object> exactFlirt=DeterministicRelationshipEvaluation("mutual_romantic_flirtation","They exchanged mutual flirtation.",
                new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",sincereRomance}}},{"preEventRomanceIntensity",25d}},new Dictionary<string,object>());
            Dictionary<string,object> exactFlirtDeltas=ReadDictionary(exactFlirt,"facetDeltas")??new Dictionary<string,object>();
            add("explicit_romance_delta_precedence",ReadDouble(exactFlirtDeltas,"attraction",0d)==5d&&ReadDouble(exactFlirtDeltas,"affection",0d)==3d,
                "Registered flirtation keeps its exact +5 attraction and +3 affection instead of being overwritten by generic wording.");
            Dictionary<string,double[]> exactRomanceOutcomes=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["mutual_romantic_flirtation"]=new[]{5d,3d,0d},["romantic_confidence"]=new[]{3d,4d,4d},
                ["romantic_intimacy"]=new[]{8d,7d,3d},["secret_affair_intimacy"]=new[]{10d,8d,3d}
            };
            bool exactOutcomes=exactRomanceOutcomes.All(expected=>
            {
                Dictionary<string,object> deltas=ReadDictionary(DeterministicRelationshipEvaluation(expected.Key,expected.Key,
                    new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",sincereRomance}}},{"preEventRomanceIntensity",25d}},
                    new Dictionary<string,object>()),"facetDeltas")??new Dictionary<string,object>();
                return ReadDouble(deltas,"attraction",0d)==expected.Value[0]&&ReadDouble(deltas,"affection",0d)==expected.Value[1]
                    &&ReadDouble(deltas,"trust",0d)==expected.Value[2];
            });
            add("all_explicit_romance_outcomes",exactOutcomes,"All four registered romantic events use their exact shared attraction, affection, and trust deltas.");
            Dictionary<string,object> developedFlirt=DeterministicRelationshipEvaluation("mutual_romantic_flirtation","They exchanged mutual flirtation.",
                new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",sincereRomance}}},{"preEventRomanceIntensity",50d}},new Dictionary<string,object>());
            Dictionary<string,object> developedFlirtDeltas=ReadDictionary(developedFlirt,"facetDeltas")??new Dictionary<string,object>();
            add("developed_romance_bond_bonus",ReadDouble(developedFlirtDeltas,"attraction",0d)==7d&&ReadDouble(developedFlirtDeltas,"affection",0d)==5d,
                "Developed sincere romance adds exactly +2 attraction and +2 affection.");
            Dictionary<string,object> calculatedRomance=new Dictionary<string,object>{{"presentation","calculated"},{"genuineInterest",20d},{"strategicInterest",70d},
                {"hardConstraints",new Dictionary<string,object>{{"coercive",false}}}};
            Dictionary<string,object> calculatedFlirt=DeterministicRelationshipEvaluation("mutual_romantic_flirtation","They exchanged mutual flirtation.",
                new Dictionary<string,object>{{"motiveDecision",new Dictionary<string,object>{{"romance",calculatedRomance}}},{"preEventRomanceIntensity",75d}},new Dictionary<string,object>());
            Dictionary<string,object> calculatedFlirtDeltas=ReadDictionary(calculatedFlirt,"facetDeltas")??new Dictionary<string,object>();
            add("calculated_romance_no_fabricated_attraction",ReadDouble(calculatedFlirtDeltas,"attraction",1d)==0d
                &&ReadDouble(calculatedFlirtDeltas,"interest_alignment",0d)>=5d&&ReadDouble(calculatedFlirtDeltas,"dependence",0d)>=2d,
                "Calculated romance receives no attraction or stage bonus and redirects the mechanical effect into strategic facets.");
            add("census_isolation", ReadDictionaryList(census, "heroes").All(x => string.IsNullOrWhiteSpace(ReadString(x, "simulationLocationId", ""))),
                "Simulation state is cloned and does not alter the source census or live campaign.");
            Dictionary<string, object> installed = BuildRelationshipSimulationStaticCensus();
            Dictionary<string, int> installedModules = ReadDictionaryList(installed, "sourceModules")
                .ToDictionary(x => ReadString(x, "moduleId", ""), x => ReadInt(x, "npcCount", 0), StringComparer.OrdinalIgnoreCase);
            add("installed_all_campaign_roster", string.IsNullOrWhiteSpace(ReadString(installed, "error", ""))
                && installedModules.ContainsKey("SandBox") && installedModules["SandBox"] > 0
                && installedModules.ContainsKey("NavalDLC") && installedModules["NavalDLC"] > 0
                && installedModules.ContainsKey("ReignBeta") && installedModules["ReignBeta"] > 0,
                "The default roster merges fixed adult NPCs from Native/Sandbox, War Sails, and Reign module data.");
            add("campaign_independent_roster", ReadString(installed, "campaignId", "") == "installed_module_roster"
                && !ReadDictionaryList(installed, "heroes").Any(x => ReadString(x, "heroStringId", "").Equals("main_hero", StringComparison.OrdinalIgnoreCase)),
                "The simulator no longer requires a save selection and excludes the player template from the NPC pool.");
            add("pair_specific_first_contact_chemistry", one.Relationships.Values.Select(x => Math.Round(x.Facets["attraction"], 2)).Distinct().Count() > 5
                && one.Relationships.Values.Any(x => x.Facets["rivalry"] >= 10d)
                && one.Relationships.Values.Select(x => Math.Round(x.Facets["jealousy"], 2)).Distinct().Count() > 5,
                "First contact deterministically varies attraction, rivalry, jealousy, and other directional chemistry by pair.");
            Dictionary<string, object> neutralTraits = new Dictionary<string, object> { ["foundationTraits"] = traits };
            Dictionary<string, object> observer = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = false, ["clanTier"] = 2, ["fiefCount"] = 0 };
            Dictionary<string, object> lowStatusTarget = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["clanTier"] = 1, ["fiefCount"] = 0 };
            Dictionary<string, object> highStatusTarget = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["clanTier"] = 6, ["fiefCount"] = 5, ["isRuler"] = true, ["renown"] = 2000, ["influence"] = 1000 };
            Dictionary<string, double> lowStatusChemistry = BuildInitialRelationshipFacets("observer", "target", observer, lowStatusTarget, neutralTraits, neutralTraits, 0, false, false, false, true);
            Dictionary<string, double> highStatusChemistry = BuildInitialRelationshipFacets("observer", "target", observer, highStatusTarget, neutralTraits, neutralTraits, 0, false, false, false, true);
            add("target_relative_status_chemistry", highStatusChemistry["fear"] > lowStatusChemistry["fear"] + 10d
                && highStatusChemistry["envy"] > lowStatusChemistry["envy"],
                "Target power and status now change fear and envy while holding the deterministic pair roll constant.");
            Dictionary<string, object> normalized = RelationshipSimulationStatusDictionary(one, "trust", "");
            add("one_guaranteed_event_per_kingdom", ReadString(normalized, "eventPolicy", "") == "one_guaranteed_event_per_eligible_kingdom_every_two_days"
                && one.EventCountsByKingdom.Keys.Contains("kingdom_a", StringComparer.OrdinalIgnoreCase)
                && one.EventCountsByKingdom.Keys.Contains("kingdom_b", StringComparer.OrdinalIgnoreCase)
                && one.EventCountsByKingdom.Values.All(x => x <= 14),
                "Each eligible kingdom receives one guaranteed consequential event every two days.");
            add("normalized_simulation_reporting", normalized.ContainsKey("eventParticipationsPerNpc")
                && normalized.ContainsKey("positiveNativeRelationChanges") && normalized.ContainsKey("negativeNativeRelationChanges")
                && normalized.ContainsKey("threadCounts") && normalized.ContainsKey("romanceFunnel") && normalized.ContainsKey("passivePositiveDriftByFacet")
                && ReadDictionaryList(normalized, "cityCountCohorts").All(x => x.ContainsKey("selectedFacetMean") && x.ContainsKey("eventsPer100Npcs")),
                "Status reports normalized event participation, storyline stages, passive drift, signed native changes, and relationship outcomes by city-count cohort.");
            RelationshipSimulationRun storyRun=CreateRelationshipSimulation(census,365,91,false);
            string storyA=storyRun.Heroes.Keys.First(),storyB=storyRun.Heroes.Keys.Skip(1).First();
            ApplyRelationshipSimulationStoryEvent(storyRun,"mutual_romantic_flirtation",storyA,storyB,storyRun.StartDay);
            double recentMultiplier=RelationshipSimulationStoryMultiplier(storyRun,storyA,storyB,storyRun.StartDay+7);
            double monthMultiplier=RelationshipSimulationStoryMultiplier(storyRun,storyA,storyB,storyRun.StartDay+20);
            double laterMultiplier=RelationshipSimulationStoryMultiplier(storyRun,storyA,storyB,storyRun.StartDay+45);
            add("story_continuation_multipliers",Math.Abs(recentMultiplier-3d)<0.0001d&&Math.Abs(monthMultiplier-2.5d)<0.0001d&&Math.Abs(laterMultiplier-2d)<0.0001d,
                "Active storylines receive exact 3x, 2.5x, and 2x recency multipliers without stacking.");
            AssignRelationshipSimulationLocations(storyRun,1);
            add("story_survives_rotation",RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"romance",storyRun.StartDay+7,true)==25d,
                "Location rotation does not erase a developing relationship storyline.");
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+8,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+9,sincereRomance,sincereRomance);
            RelationshipSimulationHero storyHero=storyRun.Heroes[storyA],storyPartner=storyRun.Heroes[storyB];
            RelationshipSimulationHero politicalMatch=storyRun.Heroes.Values.First(x=>x.Id!=storyA&&x.Id!=storyB
                &&ReadBool(x.Data,"isFemale",false)!=ReadBool(storyHero.Data,"isFemale",false));
            add("developed_courtship_arrangement_penalty",Math.Abs(RelationshipSimulationArrangedCourtshipMultiplier(storyRun,storyA,politicalMatch.Id,storyRun.StartDay+9)-.75d)<.0001d
                &&Math.Abs(RelationshipSimulationArrangedCourtshipMultiplier(storyRun,storyA,storyB,storyRun.StartDay+9)-1d)<.0001d,
                "A developed courtship applies a 25% third-party arrangement penalty but never penalizes its own couple.");
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+10,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_confidence",storyA,storyB,storyRun.StartDay+11,sincereRomance,sincereRomance);
            add("major_courtship_arrangement_penalty",Math.Abs(RelationshipSimulationArrangedCourtshipMultiplier(storyRun,storyA,politicalMatch.Id,storyRun.StartDay+11)-.50d)<.0001d,
                "A major courtship applies the exact 50% third-party arrangement penalty without becoming an absolute block.");
            foreach(RelationshipSimulationRelation relation in new[]{EnsureRelationshipSimulationRelation(storyRun,storyA,storyB),EnsureRelationshipSimulationRelation(storyRun,storyB,storyA)})
            {
                relation.Facets["attraction"]=45d;relation.Facets["affection"]=70d;relation.Facets["trust"]=55d;relation.Facets["resentment"]=0d;
            }
            Dictionary<string,object> storyPostureA=RelationshipSimulationRomanticPosture(storyRun,storyA,storyB,storyRun.StartDay+11);
            Dictionary<string,object> storyPostureB=RelationshipSimulationRomanticPosture(storyRun,storyB,storyA,storyRun.StartDay+11);
            double storyIntensity=RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"romance",storyRun.StartDay+11,true);
            add("targeted_sincere_progression",StoryPhysicalRomanceEligible(true,true,storyIntensity,50d,storyPostureA,storyPostureB)
                &&RomanticMarriageReady(RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(storyRun,storyA,storyB)),
                    RelationshipSimulationFacetObject(EnsureRelationshipSimulationRelation(storyRun,storyB,storyA))),
                "A suitable co-located bilateral courtship can cross both the intimacy posture gate and the unchanged romantic-marriage facet gate.");
            MarryRelationshipSimulationHeroes(storyRun,storyHero,politicalMatch,"arranged",storyRun.StartDay+12);
            add("arranged_marriage_preserves_displaced_romance",storyRun.CourtshipDisplacements==1
                &&RelationshipSimulationStory(storyRun,storyA,storyB,"romance",storyRun.StartDay+12,false).Route=="temptation"
                &&RelationshipSimulationStory(storyRun,storyB,storyA,"romance",storyRun.StartDay+12,false).Route=="temptation",
                "An overriding political marriage preserves the bilateral romance as a private temptation.");
            Dictionary<string,object> affairPostureA=RelationshipSimulationRomanticPosture(storyRun,storyA,storyB,storyRun.StartDay+12);
            Dictionary<string,object> affairPostureB=RelationshipSimulationRomanticPosture(storyRun,storyB,storyA,storyRun.StartDay+12);
            add("targeted_affair_gate",StoryPhysicalRomanceEligible(true,true,storyIntensity,75d,affairPostureA,affairPostureB),
                "An established bilateral temptation can cross the unchanged affair posture gate when at least one participant is married.");
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_rejection",storyA,storyB,storyRun.StartDay+13,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_boundary",storyA,storyB,storyRun.StartDay+14,sincereRomance,sincereRomance);
            ApplyRelationshipSimulationStoryEvent(storyRun,"romantic_boundary",storyA,storyB,storyRun.StartDay+15,sincereRomance,sincereRomance);
            add("rejection_removes_continuity",RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"romance",storyRun.StartDay+15,true)==0d,
                "Repeated rejection or boundary events reduce romance by 30 and can remove its continuity advantage entirely.");
            for(int i=0;i<4;i++)ApplyRelationshipSimulationStoryEvent(storyRun,"political_rivalry_argument",storyA,storyB,storyRun.StartDay+i*7);
            add("repeated_rivalry_creates_feud",ReadSimulationCount(storyRun.EventCounts,"feuds_started")==1
                && RelationshipSimulationPairStoryIntensity(storyRun,storyA,storyB,"rivalry",storyRun.StartDay+28,false)>=75d,
                "Repeated qualifying rivalry events create one feud milestone.");
            return results;
        }

        private static string RelationshipSimulationFingerprint(RelationshipSimulationRun run)
        {
            string relations = string.Join(";", run.Relationships.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + ":" +
                string.Join(",", RelationshipFacetKeys.Select(f => Math.Round(x.Value.Facets[f], 4).ToString("0.0000", CultureInfo.InvariantCulture)))));
            string events = string.Join(";", run.EventCounts.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.Key + "=" + x.Value));
            string locations = string.Join(";", run.Heroes.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.Id + "=" + x.LocationId));
            return relations + "|" + events + "|" + locations;
        }

        private sealed class RelationshipSimulationRun
        {
            public string RunId = "", CampaignId = "", TimelineId = "main", State = "idle", Error = "", StartedUtc = "", CompletedUtc = "";
            public int StartDay, CurrentDay, DurationDays, CompletedDays, Seed, Relocations, NativeRelationChanges, PositiveNativeRelationChanges, NegativeNativeRelationChanges, TotalRandomEvents;
            public int ContinuationEvents, NewPairEvents, SkippedKingdomCycles, CourtshipDisplacements;
            public double Progress;
            public bool Persist, CancelRequested;
            public readonly Dictionary<string, RelationshipSimulationHero> Heroes = new Dictionary<string, RelationshipSimulationHero>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Dictionary<string, object>> Clans = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, string> KingdomNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> RealCityCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<string>> LocationsByKingdom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> NativeRelations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, RelationshipSimulationRelation> Relationships = new Dictionary<string, RelationshipSimulationRelation>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, RelationshipSimulationExposure> Exposures = new Dictionary<string, RelationshipSimulationExposure>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> LastEventDay = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> KingdomMonthEventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> HeroMonthEventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> EventCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> EventCountsByKingdom = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, RelationshipSimulationStoryThread> StoryThreads = new Dictionary<string, RelationshipSimulationStoryThread>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> PassivePositiveDrift = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, double> PassiveNegativeDrift = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, HashSet<string>> RomanceFunnelPairs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["suitable"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),["coLocated"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ["evaluated"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),["baseInterest25"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ["receptivity40"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),["receptivity60"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ["thread25"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),["thread50"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ["thread75"]=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
            public readonly Dictionary<string,double> RomanceMetricMinimum=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string,double> RomanceMetricMaximum=new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);
            public int RomanceContinuityApplications,RomanceBlockedSuitability,RomanceBlockedCooldown,RomanceBlockedMonthlyCap,
                RomanceBlockedCoLocation,RomanceBlockedReceptivity;
            public double RomanceContinuityGenuineBonus,RomanceContinuityStrategicBonus,RomanceContinuityReceptivityBonus;
            public readonly Dictionary<string, RelationshipSimulationReadiness> DivorceReadiness = new Dictionary<string, RelationshipSimulationReadiness>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> MarriageRolls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> FeudPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Dictionary<string, object>> Timeline = new List<Dictionary<string, object>>();
            public readonly List<Dictionary<string, object>> RelocationHistory = new List<Dictionary<string, object>>();
        }

        private sealed class RelationshipSimulationHero
        {
            public string Id = "", KingdomId = "", LocationId = "";
            public Dictionary<string, object> Data = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, object> Traits = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class RelationshipSimulationRelation
        {
            public string SubjectId = "", TargetId = "";
            public Dictionary<string, double> Facets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class RelationshipSimulationExposure
        {
            public int FirstDay, LastDay, ConsecutiveDays, LastNativeSyncDay;
            public double WeightedExposure, PendingNativeSignal;
            public string LocationId = "";
        }

        private sealed class RelationshipSimulationEdge
        {
            public string PairKey = "", A = "", B = "", LocationId = "";
            public bool CoLocated, SameClan, SameKingdom, Spouses, Family;
            public int NativeRelation;
            public double Score, AmbientExposure, AmbientAffinity, AmbientTension, ContinuationMultiplier = 1d;
        }

        private sealed class RelationshipSimulationStoryThread
        {
            public string SubjectId="",TargetId="",Kind="",Route="",MotiveChannel="",LastEventType="";
            public double Intensity;
            public int LastEventDay,QualifyingEvents;
        }

        private sealed class RelationshipSimulationReadiness
        {
            public int FirstDay;
            public double Score;
            public bool Triggered;
        }
    }
}
