using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
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
                        string clanId = CharactersService.RemoveObjectPrefix((string)settlement.Attribute("owner"));
                        if (!string.IsNullOrWhiteSpace(clanId))
                        {
                            int fiefCount;
                            fiefCountsByClan.TryGetValue(clanId, out fiefCount);
                            fiefCountsByClan[clanId] = fiefCount + 1;
                        }
                        if (string.Equals((string)town.Attribute("is_castle"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                        XElement clan;
                        string kingdomId = clans.TryGetValue(clanId, out clan) ? CharactersService.RemoveObjectPrefix((string)clan.Attribute("super_faction")) : "";
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
                            ["name"] = CharactersService.CleanGameText((string)settlement.Attribute("name"))
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
                        double age = CharactersService.ParseDouble((string)npc.Attribute("age"));
                        if (!alive || age < 18d || heroRows.Any(x => ReadString(x, "heroStringId", "").Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                        string clanId = CharactersService.RemoveObjectPrefix((string)hero.Attribute("faction"));
                        XElement clan;
                        clans.TryGetValue(clanId, out clan);
                        string kingdomId = CharactersService.RemoveObjectPrefix((string)clan?.Attribute("super_faction"));
                        string cultureId = CharactersService.RemoveObjectPrefix((string)npc.Attribute("culture"));
                        Dictionary<string, object> nativeTraits = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        foreach (string key in new[] { "valor", "generosity", "honor", "mercy", "calculating" }) nativeTraits[key] = 0;
                        foreach (XElement trait in npc.Descendants("Trait"))
                        {
                            string key = ((string)trait.Attribute("id") ?? "").ToLowerInvariant();
                            if (nativeTraits.ContainsKey(key)) nativeTraits[key] = CharactersService.ClampTrait(ParseInt((string)trait.Attribute("value")));
                        }
                        Dictionary<string, object> skills = CharactersService.ResolveCatalogNativeSkills(npc, skillSets);
                        string homeSettlementId = CharactersService.RemoveObjectPrefix((string)clan?.Attribute("initial_home_settlement"));
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
                            ["name"] = CharactersService.CleanGameText((string)npc.Attribute("name")), ["age"] = age,
                            ["isAlive"] = true, ["isPrisoner"] = false, ["isLord"] = true, ["isNotable"] = false, ["isWanderer"] = false,
                            ["isFemale"] = string.Equals((string)npc.Attribute("is_female"), "true", StringComparison.OrdinalIgnoreCase),
                            ["cultureId"] = cultureId, ["clanId"] = clanId, ["kingdomId"] = kingdomId,
                            ["clanTier"] = clanTier, ["clanFiefCount"] = fiefCount, ["fiefCount"] = fiefCount,
                            ["clanRenown"] = clanRenown, ["renown"] = clanRenown,
                            ["clanGold"] = clanGold, ["gold"] = clanGold * 0.10d, ["influence"] = influence,
                            ["leadership"] = leadership, ["partyStrength"] = 80d + leadership * 1.2d,
                            ["clanStrength"] = 180d + clanTier * 160d + fiefCount * 120d,
                            ["visibleStatusScore"] = DialogueService.ClampDouble(clanTier * 12d + fiefCount * 6d + (kingdom != null && CharactersService.RemoveObjectPrefix((string)kingdom.Attribute("owner")).Equals(id, StringComparison.OrdinalIgnoreCase) ? 18d : 0d), 0d, 100d),
                            ["spouseId"] = CharactersService.RemoveObjectPrefix((string)hero.Attribute("spouse")),
                            ["fatherId"] = CharactersService.RemoveObjectPrefix((string)hero.Attribute("father")),
                            ["motherId"] = CharactersService.RemoveObjectPrefix((string)hero.Attribute("mother")),
                            ["currentSettlementId"] = homeSettlementId, ["currentSettlementKingdomId"] = kingdomId,
                            ["isRuler"] = kingdom != null && CharactersService.RemoveObjectPrefix((string)kingdom.Attribute("owner")).Equals(id, StringComparison.OrdinalIgnoreCase),
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
                        ["kingdomId"] = pair.Key, ["name"] = CharactersService.CleanGameText((string)pair.Value.Attribute("name")),
                        ["cultureId"] = CharactersService.RemoveObjectPrefix((string)pair.Value.Attribute("culture")),
                        ["leaderId"] = CharactersService.RemoveObjectPrefix((string)pair.Value.Attribute("owner")),
                        ["cityIds"] = cityRows.Select(x => ReadString(x, "settlementId", "")).ToList(), ["cities"] = cityRows
                    };
                }).Where(x => heroRows.Any(hero => ReadString(hero, "kingdomId", "").Equals(ReadString(x, "kingdomId", ""), StringComparison.OrdinalIgnoreCase))).ToList();

                List<Dictionary<string, object>> clanRows = clans.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new Dictionary<string, object>
                {
                    ["fiefCount"] = fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0,
                    ["clanId"] = pair.Key, ["name"] = CharactersService.CleanGameText((string)pair.Value.Attribute("name")),
                    ["kingdomId"] = CharactersService.RemoveObjectPrefix((string)pair.Value.Attribute("super_faction")),
                    ["cultureId"] = CharactersService.RemoveObjectPrefix((string)pair.Value.Attribute("culture")),
                    ["tier"] = ParseInt((string)pair.Value.Attribute("tier")),
                    ["renown"] = ParseInt((string)pair.Value.Attribute("tier")) * 225d + (fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0) * 90d,
                    ["gold"] = 50000d + ParseInt((string)pair.Value.Attribute("tier")) * 60000d + (fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0) * 85000d,
                    ["influence"] = ParseInt((string)pair.Value.Attribute("tier")) * 100d + (fiefCountsByClan.ContainsKey(pair.Key) ? fiefCountsByClan[pair.Key] : 0) * 75d,
                    ["leaderId"] = CharactersService.RemoveObjectPrefix((string)pair.Value.Attribute("owner")),
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

        internal static string FindRelationshipSimulationModulesRoot()
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

        private static string RelationshipSimulationRoot()
        {
            return Path.Combine(Program.DataDir, "relationship_simulations");
        }

        private static string RelationshipSimulationRunDirectory(string runId)
        {
            return Path.Combine(RelationshipSimulationRoot(), CampaignFileService.SafePathSegment(runId, "run"));
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

            Dictionary<string, Dictionary<string, object>> library = CharactersService.LoadCharacterProfileLibrary();
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
                Dictionary<string, object> storedProfile = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, id, "profile.json"));
                foreach (KeyValuePair<string, object> item in storedProfile) if (!data.ContainsKey(item.Key)) data[item.Key] = item.Value;
                Dictionary<string, object> traits = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, id, "traits.json"));
                if (traits.Count == 0 && library.TryGetValue(id, out shipped)) traits = ReadDictionary(shipped, "traits") ?? new Dictionary<string, object>();
                if (traits.Count == 0) traits = CharactersService.BuildTraitDocument(data);
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
                double delta = DialogueService.ClampDouble(gap * 0.02d * exposureWeight, -0.50d, 0.50d);
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
            thread.Intensity=DialogueService.ClampDouble(current+delta,0d,100d);
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
            CampaignFileService.WriteJsonObject(Path.Combine(directory, "status.json"), status);
            CampaignFileService.WriteJsonObject(Path.Combine(RelationshipSimulationRoot(), "latest.json"), new Dictionary<string, object>
            {
                ["runId"] = run.RunId, ["campaignId"] = run.CampaignId, ["state"] = run.State,
                ["statusPath"] = Path.Combine(directory, "status.json"), ["updatedUtc"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            });
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
