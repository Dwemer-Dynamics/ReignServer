using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Diplomacy;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        internal static ArrayList BuildNationalPowerAssessments(Dictionary<string, object> world, Dictionary<string, object> state)
        {
            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(world, "kingdoms");
            if (kingdoms.Count == 0) return new ArrayList();
            double militaryMedian = DirectorMedian(kingdoms.Select(x => Math.Max(1d, ReadDouble(x, "strength", 1d))));
            double territoryMedian = DirectorMedian(kingdoms.Select(x => DirectorTerritoryPower(x)));
            double economyMedian = DirectorMedian(kingdoms.Select(x => DirectorEconomicPower(x)));
            double politicalMedian = DirectorMedian(kingdoms.Select(x => Math.Max(1d, ReadDouble(x, "clanCount", 1d))));
            ArrayList assessments = new ArrayList();
            foreach (Dictionary<string, object> kingdom in kingdoms)
            {
                double military = Math.Max(1d, ReadDouble(kingdom, "strength", 1d)) / militaryMedian;
                double territory = DirectorTerritoryPower(kingdom) / territoryMedian;
                double economy = DirectorEconomicPower(kingdom) / economyMedian;
                double political = Math.Max(1d, ReadDouble(kingdom, "clanCount", 1d)) / politicalMedian;
                int warCount = ReadStringList(kingdom, "enemies").Count;
                double fatigue = DirectorKingdomAverageFatigue(state, ReadString(kingdom, "kingdomId", ""));
                double negativeIncome = Math.Max(0d, -ReadDouble(kingdom, "dailyGoldChange", 0d));
                double incomePenalty = Clamp(0d, 0.10d, negativeIncome / Math.Max(1000d, ReadDouble(kingdom, "treasury", 0d)));
                double readinessPenalty = Math.Min(0.24d, warCount * 0.08d)
                    + Math.Min(0.15d, fatigue * 0.0015d)
                    + (ReadBool(kingdom, "rulerIsPrisoner", false) ? 0.10d : 0d)
                    + incomePenalty;
                double score = Math.Max(0.05d, (0.55d * military + 0.20d * territory + 0.15d * economy + 0.10d * political) * Math.Max(0.35d, 1d - readinessPenalty));
                assessments.Add(new Dictionary<string, object>
                {
                    ["kingdomId"] = ReadString(kingdom, "kingdomId", ""), ["score"] = score,
                    ["military"] = military, ["territory"] = territory, ["economy"] = economy, ["politicalDepth"] = political,
                    ["warCount"] = warCount, ["averageWarFatigue"] = fatigue, ["readinessPenalty"] = readinessPenalty
                });
            }
            return assessments;
        }

        private static double DirectorTerritoryPower(Dictionary<string, object> kingdom)
        {
            return Math.Max(1d, 1000d * ReadDouble(kingdom, "fiefCount", 0d)
                + 0.15d * ReadDouble(kingdom, "settlementProsperity", 0d)
                + 5d * ReadDouble(kingdom, "garrisonStrength", 0d));
        }

        private static double DirectorEconomicPower(Dictionary<string, object> kingdom)
        {
            return Math.Max(1d, ReadDouble(kingdom, "treasury", 0d) + 126d * Math.Max(0d, ReadDouble(kingdom, "dailyGoldChange", 0d)));
        }

        private static double DirectorMedian(IEnumerable<double> values)
        {
            List<double> ordered = values.Where(x => x > 0d).OrderBy(x => x).ToList();
            if (ordered.Count == 0) return 1d;
            int middle = ordered.Count / 2;
            return ordered.Count % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2d;
        }

        private static double DirectorKingdomAverageFatigue(Dictionary<string, object> state, string kingdomId)
        {
            List<double> values = new List<double>();
            foreach (Dictionary<string, object> war in (ReadDictionary(state, "wars") ?? new Dictionary<string, object>()).Values.OfType<Dictionary<string, object>>())
            {
                if (!ReadBool(war, "active", false)) continue;
                if (string.Equals(ReadString(war, "kingdomAId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)) values.Add(ReadDouble(war, "fatigueA", 0d));
                else if (string.Equals(ReadString(war, "kingdomBId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)) values.Add(ReadDouble(war, "fatigueB", 0d));
            }
            return values.Count == 0 ? 0d : values.Average();
        }

        private static Dictionary<string, object> CompactDirectorWorld(
            Dictionary<string, object> world,
            string perspectiveKingdomId,
            string counterpartKingdomId = "",
            string intent = "")
        {
            bool includeMarriageOptions = string.IsNullOrWhiteSpace(intent)
                || intent.Equals("security", StringComparison.OrdinalIgnoreCase);
            HashSet<string> counterpartIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(counterpartKingdomId)) counterpartIds.Add(counterpartKingdomId);
            else
            {
                foreach (Dictionary<string, object> kingdom in ReadDictionaryList(world, "kingdoms"))
                {
                    string id = ReadString(kingdom, "kingdomId", "");
                    if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase))
                        counterpartIds.Add(id);
                }
                foreach (Dictionary<string, object> candidate in includeMarriageOptions
                    ? ReadDictionaryList(world, "allianceMarriageCandidates")
                    : new List<Dictionary<string, object>>())
                {
                    string id = ReadString(candidate, "kingdomId", "");
                    if (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase))
                        counterpartIds.Add(id);
                }
                foreach (Dictionary<string, object> option in includeMarriageOptions
                    ? ReadDictionaryList(world, "allianceMarriageOptions")
                    : new List<Dictionary<string, object>>())
                {
                    string id = DirectorMarriageCounterparty(option, perspectiveKingdomId);
                    if (!string.IsNullOrWhiteSpace(id)) counterpartIds.Add(id);
                }
            }
            List<Dictionary<string, object>> idealMarriageOptions =
                includeMarriageOptions
                    ? counterpartIds
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .Select(x => SelectIdealDirectorMarriageOption(world,
                            perspectiveKingdomId, x))
                        .Where(x => x != null).ToList()
                    : new List<Dictionary<string, object>>();
            ArrayList marriageSummary = new ArrayList(counterpartIds
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => (object)new Dictionary<string, object>
                {
                    ["counterpartyKingdomId"] = x,
                    ["selected"] = idealMarriageOptions.Any(y => DirectorMarriageOptionInvolvesKingdom(y, x))
                })
                .ToList());
            Dictionary<string, object> perspective = ReadDictionaryList(world,
                "kingdoms").FirstOrDefault(row => ReadString(row, "kingdomId", "")
                    .Equals(perspectiveKingdomId, StringComparison.OrdinalIgnoreCase));
            int toneDay = (int)Math.Floor(ReadDouble(world, "worldDay", 0d));
            ArrayList relationshipGuidance = new ArrayList((perspective == null
                    ? new List<Dictionary<string, object>>()
                    : ReadDictionaryList(perspective, "rulerAttitudes"))
                .Where(attitude => counterpartIds.Contains(ReadString(attitude,
                    "targetKingdomId", "")))
                .Select(attitude =>
                {
                    int effective = ReadInt(attitude, "effectiveAttitude", 0);
                    int generousChance = Clamp(25 + effective / 5, 10, 45);
                    int harshChance = Clamp(25 - effective / 5, 10, 45);
                    int toneRoll = 1 + (int)((uint)StableDirectorSeed(
                        ReadString(world, "campaignId", "") + "|tone", toneDay,
                        perspectiveKingdomId + "|" + ReadString(attitude,
                            "targetKingdomId", "")) % 100u);
                    string tone = toneRoll <= generousChance ? "generous"
                        : toneRoll <= generousChance + harshChance ? "harsh"
                        : "balanced";
                    return (object)new Dictionary<string, object>
                    {
                        ["targetKingdomId"] = ReadString(attitude,
                            "targetKingdomId", ""),
                        ["effectiveAttitude"] = effective,
                        ["cooperationPreferencePoints"] = Clamp(effective / 4,
                            -25, 25),
                        ["hostilityPreferencePoints"] = Clamp(-effective / 4,
                            -25, 25),
                        ["generousChance"] = generousChance,
                        ["harshChance"] = harshChance,
                        ["toneRoll"] = toneRoll, ["termTone"] = tone
                    };
                }).ToList());
            HashSet<string> relevantKingdomIds = new HashSet<string>(
                counterpartIds, StringComparer.OrdinalIgnoreCase)
            {
                perspectiveKingdomId
            };
            List<Dictionary<string, object>> compactKingdoms =
                ReadDictionaryList(world, "kingdoms")
                    .Where(row => relevantKingdomIds.Contains(ReadString(row,
                        "kingdomId", "")))
                    .Select(row => CompactDirectorKingdomRow(row,
                        relevantKingdomIds)).ToList();
            IEnumerable<Dictionary<string, object>> relevantSettlements =
                ReadDictionaryList(world, "settlements")
                    .Where(row => relevantKingdomIds.Contains(ReadFirstString(
                        row, "ownerKingdomId", "kingdomId")));
            if (string.IsNullOrWhiteSpace(counterpartKingdomId))
            {
                relevantSettlements = relevantSettlements
                    .GroupBy(row => ReadFirstString(row, "ownerKingdomId",
                        "kingdomId"), StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group
                        .OrderByDescending(row => ReadBool(row,
                            "isFortification", false))
                        .ThenBy(row => ReadInt(row, "garrison", int.MaxValue))
                        .ThenBy(row => ReadDouble(row, "loyalty", double.MaxValue))
                        .ThenByDescending(row => ReadDouble(row, "prosperity", 0d))
                        .ThenBy(row => ReadString(row, "settlementId", ""),
                            StringComparer.OrdinalIgnoreCase)
                        .Take(group.Key.Equals(perspectiveKingdomId,
                            StringComparison.OrdinalIgnoreCase) ? 8 : 3));
            }
            List<Dictionary<string, object>> compactSettlements =
                relevantSettlements.Select(CompactDirectorSettlementRow)
                    .OrderByDescending(row => ReadBool(row,
                        "isFortification", false))
                    .ThenBy(row => ReadString(row, "settlementId", ""),
                        StringComparer.OrdinalIgnoreCase)
                    .Take(80).ToList();
            List<Dictionary<string, object>> compactWars =
                ReadDictionaryList(world, "wars")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "warId", "kingdomAId", "kingdomBId", "attackerKingdomId",
                        "defenderKingdomId", "active", "startedDay", "durationDays",
                        "fatigueA", "fatigueB", "casualtiesA", "casualtiesB",
                        "raidsA", "raidsB", "settlementsGainedA", "settlementsGainedB"
                    })).Take(40).ToList();
            List<Dictionary<string, object>> compactAgreements =
                ReadDictionaryList(world, "agreements")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "agreementId", "kind", "actorKingdomId", "targetKingdomId",
                        "createdDay", "expireDay", "isActive", "reason"
                    })).Take(60).ToList();
            List<Dictionary<string, object>> compactRelations =
                ReadDictionaryList(world, "relations")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "actorKingdomId", "targetKingdomId", "kingdomAId",
                        "kingdomBId", "relation", "value", "stance"
                    })).Take(80).ToList();
            List<Dictionary<string, object>> compactPrisoners =
                ReadDictionaryList(world, "prisoners")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "heroId", "name", "kingdomId", "captorKingdomId",
                        "captorHeroId", "isRuler", "value"
                    })).Take(50).ToList();
            List<Dictionary<string, object>> compactRecentEvents =
                ReadDictionaryList(world, "recentDiplomaticEvents")
                    .Where(row => DirectorRecordInvolvesAnyKingdom(row,
                        relevantKingdomIds))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "eventId", "worldDay", "command", "accepted", "outcome",
                        "summary", "actorKingdomId", "targetKingdomId",
                        "consequenceTags"
                    })).Take(20).ToList();
            List<Dictionary<string, object>> compactPower =
                ReadDictionaryList(world, "nationalPowerAssessments")
                    .Where(row => relevantKingdomIds.Contains(ReadString(row,
                        "kingdomId", "")))
                    .Select(row => CompactDirectorRecord(row, new[]
                    {
                        "kingdomId", "strength", "relativeStrength", "treasury",
                        "fortificationCount", "fiefCount", "warCount", "fatigue",
                        "assessment"
                    })).ToList();
            return new Dictionary<string, object>
            {
                ["worldDay"] = ReadDouble(world, "worldDay", 0d),
                ["calendar"] = world.ContainsKey("calendar") ? world["calendar"] : new Dictionary<string, object>
                {
                    ["daysPerYear"] = DirectorCampaignDaysPerYear,
                    ["daysPerSeason"] = 31.5d,
                    ["seasonsPerYear"] = 4
                },
                ["playerKingdomId"] = ReadString(world, "playerKingdomId", ""),
                ["perspectiveKingdomId"] = perspectiveKingdomId,
                ["kingdoms"] = new ArrayList(compactKingdoms),
                ["settlements"] = new ArrayList(compactSettlements),
                ["wars"] = new ArrayList(compactWars),
                ["agreements"] = new ArrayList(compactAgreements),
                ["relations"] = new ArrayList(compactRelations),
                ["prisoners"] = new ArrayList(compactPrisoners),
                ["recentDiplomaticEvents"] = new ArrayList(compactRecentEvents)
                , ["nationalPowerAssessments"] = new ArrayList(compactPower)
                , ["allianceMarriageOptions"] = new ArrayList(idealMarriageOptions)
                , ["allianceMarriageOptionSummary"] = marriageSummary
                , ["relationshipTargetGuidance"] = relationshipGuidance
            };
        }

        private static string DirectorRulerPromptJson(Dictionary<string, object> ruler)
        {
            // The native row remains authoritative for execution. Diplomacy needs the
            // complete public matters and aggregate shares, rather than a second copy
            // of every participant, member declaration and historical ballot.
            var projected = new Dictionary<string, object>(ruler
                ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            var government = ReadDictionary(ruler, "government");
            if (government == null) return Json.Serialize(projected);
            var publicGovernment = new Dictionary<string, object>(government,
                StringComparer.OrdinalIgnoreCase);
            publicGovernment["governmentBusiness"] = new ArrayList(
                ReadDictionaryList(government, "governmentBusiness").Select(business =>
                {
                    var matter = new Dictionary<string, object>(business,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string field in new[] { "participants", "ballots", "publicPositions" })
                    {
                        matter[field + "Count"] = ReadDictionaryList(business, field).Count;
                        matter.Remove(field);
                    }
                    var forecast = ReadDictionary(business, "forecast");
                    if (forecast != null)
                    {
                        var aggregate = new Dictionary<string, object>(forecast,
                            StringComparer.OrdinalIgnoreCase);
                        aggregate["options"] = new ArrayList(
                            ReadDictionaryList(forecast, "options").Select(option =>
                            {
                                var share = new Dictionary<string, object>(option,
                                    StringComparer.OrdinalIgnoreCase);
                                share["namedVoterCount"] = ReadStringList(option, "names").Count;
                                share.Remove("names");
                                return share;
                            }).ToList());
                        matter["forecast"] = aggregate;
                    }
                    return matter;
                }).ToList());
            publicGovernment["votingDetailProjection"] = "Aggregate shares and counts only. Individual participants, declarations and ballots remain in the authoritative government record; this summary grants no vote or execution authority.";
            projected["government"] = publicGovernment;
            return Json.Serialize(projected);
        }

        private static Dictionary<string, object> CompactDirectorKingdomRow(
            Dictionary<string, object> row, HashSet<string> relevantKingdomIds)
        {
            Dictionary<string, object> compact = CompactDirectorRecord(row,
                new[]
                {
                    "kingdomId", "name", "leaderHeroId", "leaderName", "rulerHeroId",
                    "rulerName", "isPlayerKingdom", "isEliminated", "isRebelRealm",
                    "civilWarOpponentKingdomId", "strength", "treasury", "leaderGold", "fiefCount",
                    "fortificationCount", "rulerIsPrisoner", "enemies", "allies"
                });
            compact["rulerAttitudes"] = new ArrayList(ReadDictionaryList(row,
                    "rulerAttitudes")
                .Where(attitude => relevantKingdomIds.Contains(ReadString(attitude,
                    "targetKingdomId", "")))
                .Select(attitude => CompactDirectorRecord(attitude, new[]
                {
                    "targetKingdomId", "targetRulerHeroId", "effectiveAttitude",
                    "personalAffinity", "publicStanding", "tag"
                })).ToList());
            return compact;
        }

        private static Dictionary<string, object> CompactDirectorSettlementRow(
            Dictionary<string, object> row)
        {
            return CompactDirectorRecord(row, new[]
            {
                "settlementId", "name", "ownerKingdomId", "kingdomId", "kind",
                "isFortification", "prosperity", "security", "loyalty", "garrison"
            });
        }

        private static Dictionary<string, object> CompactDirectorRecord(
            Dictionary<string, object> row, IEnumerable<string> keys)
        {
            Dictionary<string, object> compact =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (row == null) return compact;
            foreach (string key in keys ?? Enumerable.Empty<string>())
                if (row.TryGetValue(key, out object value)) compact[key] = value;
            return compact;
        }

        private static bool DirectorRecordInvolvesAnyKingdom(
            Dictionary<string, object> row, HashSet<string> kingdomIds)
        {
            if (row == null || kingdomIds == null || kingdomIds.Count == 0)
                return false;
            foreach (string key in new[]
            {
                "kingdomId", "ownerKingdomId", "captorKingdomId",
                "actorKingdomId", "targetKingdomId", "kingdomAId", "kingdomBId",
                "attackerKingdomId", "defenderKingdomId", "rebelKingdomId",
                "parentKingdomId", "firstKingdomId", "secondKingdomId"
            })
                if (kingdomIds.Contains(ReadString(row, key, ""))) return true;
            foreach (string key in new[] { "kingdomIds", "participants", "enemies", "allies" })
                if (ReadStringList(row, key).Any(kingdomIds.Contains)) return true;
            return false;
        }

        private static Dictionary<string, object> SelectIdealDirectorMarriageOption(
            Dictionary<string, object> world,
            string firstKingdomId,
            string secondKingdomId)
        {
            List<Dictionary<string, object>> candidates = ReadDictionaryList(world, "allianceMarriageCandidates");
            List<Dictionary<string, object>> firstCandidates = candidates
                .Where(x => string.Equals(ReadString(x, "kingdomId", ""), firstKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(DirectorMarriageCandidateEligible)
                .ToList();
            List<Dictionary<string, object>> secondCandidates = candidates
                .Where(x => string.Equals(ReadString(x, "kingdomId", ""), secondKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(DirectorMarriageCandidateEligible)
                .ToList();
            Dictionary<string, object> selected = firstCandidates
                .SelectMany(first => secondCandidates
                    .Where(second => DirectorMarriageCandidatesCompatible(first, second))
                    .Select(second => new Dictionary<string, object>
                    {
                        ["kingdomAId"] = firstKingdomId,
                        ["kingdomBId"] = secondKingdomId,
                        ["heroAId"] = ReadString(first, "heroId", ""),
                        ["heroAName"] = ReadString(first, "heroName", ReadString(first, "heroId", "")),
                        ["clanAId"] = ReadString(first, "clanId", ""),
                        ["heroBId"] = ReadString(second, "heroId", ""),
                        ["heroBName"] = ReadString(second, "heroName", ReadString(second, "heroId", "")),
                        ["clanBId"] = ReadString(second, "clanId", ""),
                        ["selectionScore"] = DirectorMarriagePairScore(first, second),
                        ["selectionReason"] = "Highest deterministic dynastic value among the current eligible ruling-clan candidates; native suitability is revalidated before execution."
                    }))
                .OrderByDescending(x => ReadDouble(x, "selectionScore", 0d))
                .ThenBy(DirectorMarriageOptionKey, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (selected != null) return selected;

            return ReadDictionaryList(world, "allianceMarriageOptions")
                .Where(x => DirectorMarriageOptionInvolvesKingdom(x, firstKingdomId)
                    && DirectorMarriageOptionInvolvesKingdom(x, secondKingdomId))
                .OrderBy(DirectorMarriageOptionKey, StringComparer.OrdinalIgnoreCase)
                .Select(x => new Dictionary<string, object>(x, StringComparer.OrdinalIgnoreCase)
                {
                    ["selectionReason"] = "Best deterministic legacy native-suitable pair supplied by the current client."
                })
                .FirstOrDefault();
        }

        private static bool DirectorMarriageCandidateEligible(Dictionary<string, object> candidate)
        {
            return ReadBool(candidate, "eligibleForPoliticalMarriage", true)
                && !string.IsNullOrWhiteSpace(ReadString(candidate, "heroId", ""))
                && ReadDouble(candidate, "age", 18d) >= 18d
                && !ReadBool(candidate, "isProtectedHeir", false)
                && !ReadBool(candidate, "isMarried", false);
        }

        private static bool DirectorMarriageCandidatesCompatible(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            return !string.Equals(ReadString(first, "heroId", ""), ReadString(second, "heroId", ""), StringComparison.OrdinalIgnoreCase)
                && ReadBool(first, "isFemale", false) != ReadBool(second, "isFemale", false);
        }

        private static double DirectorMarriagePairScore(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            return DirectorMarriageCandidateScore(first)
                + DirectorMarriageCandidateScore(second)
                - 2d * Math.Abs(ReadDouble(first, "age", 30d) - ReadDouble(second, "age", 30d));
        }

        private static double DirectorMarriageCandidateScore(Dictionary<string, object> candidate)
        {
            double age = ReadDouble(candidate, "age", 30d);
            double score = 100d - 2d * Math.Abs(age - 26d);
            if (ReadBool(candidate, "isRulerChild", false))
                score += 160d - 10d * Math.Min(10, Math.Max(1, ReadInt(candidate, "successionRank", 10)));
            if (ReadBool(candidate, "isClanLeader", false)) score += 20d;
            score += 8d * Math.Max(0, ReadInt(candidate, "clanTier", 0));
            score += 0.2d * Clamp(-100d, 100d, ReadDouble(candidate, "relationToRuler", 0d));
            return score;
        }

        private static bool DirectorMarriageOptionInvolvesKingdom(Dictionary<string, object> option, string kingdomId)
        {
            return !string.IsNullOrWhiteSpace(kingdomId)
                && (string.Equals(ReadString(option, "kingdomAId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ReadString(option, "kingdomBId", ""), kingdomId, StringComparison.OrdinalIgnoreCase));
        }

        private static string DirectorMarriageCounterparty(Dictionary<string, object> option, string perspectiveKingdomId)
        {
            string first = ReadString(option, "kingdomAId", "");
            string second = ReadString(option, "kingdomBId", "");
            if (string.Equals(first, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase)) return second;
            if (string.Equals(second, perspectiveKingdomId, StringComparison.OrdinalIgnoreCase)) return first;
            return string.Empty;
        }

        private static string DirectorMarriageOptionKey(Dictionary<string, object> option)
        {
            return ReadString(option, "kingdomAId", "") + "|"
                + ReadString(option, "kingdomBId", "") + "|"
                + ReadString(option, "heroAId", "") + "|"
                + ReadString(option, "heroBId", "");
        }

        private static Dictionary<string, object> PublicDirectorEventMemory(Dictionary<string, object> item)
        {
            return new Dictionary<string, object>
            {
                ["worldDay"] = ReadDouble(item, "worldDay", 0d),
                ["command"] = ReadString(item, "command", ""),
                ["accepted"] = ReadBool(item, "accepted", false),
                ["outcome"] = ReadString(item, "outcome", ""),
                ["actorKingdomId"] = ReadString(item, "actorKingdomId", ""),
                ["targetKingdomId"] = ReadString(item, "targetKingdomId", ""),
                ["actorPublicReason"] = ReadString(item, "actorPublicReason", ""),
                ["targetPublicReason"] = ReadString(item, "targetPublicReason", "")
            };
        }

        private static void AppendDiplomaticConsequence(string campaignId, Dictionary<string, object> diplomaticEvent)
        {
            Program.AppendJsonLineToPath(Program.CampaignFile(campaignId, "diplomacy", "consequences.jsonl"), new Dictionary<string, object>
            {
                ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                ["worldDay"] = ReadDouble(diplomaticEvent, "worldDay", 0d),
                ["actorKingdomId"] = ReadString(diplomaticEvent, "actorKingdomId", ""),
                ["targetKingdomId"] = ReadString(diplomaticEvent, "targetKingdomId", ""),
                ["command"] = ReadString(diplomaticEvent, "command", ""),
                ["accepted"] = ReadBool(diplomaticEvent, "accepted", false),
                ["tags"] = diplomaticEvent.ContainsKey("consequenceTags") ? diplomaticEvent["consequenceTags"] : new ArrayList()
            });
        }

        internal static Dictionary<string, object> FindDirectorKingdom(Dictionary<string, object> world, string id)
        {
            return ReadDictionaryList(world, "kingdoms").FirstOrDefault(x => string.Equals(ReadString(x, "kingdomId", ""), id, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> GetDirectorWarState(Dictionary<string, object> state, string sideId, string enemyId)
        {
            Dictionary<string, object> wars = ReadDictionary(state, "wars") ?? new Dictionary<string, object>();
            if (!(wars.TryGetValue(DirectorPairKey(sideId, enemyId), out object value) && value is Dictionary<string, object> record)) return new Dictionary<string, object>();
            bool sideIsA = string.Equals(ReadString(record, "kingdomAId", ""), sideId, StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object> { ["fatigue"] = ReadDouble(record, sideIsA ? "fatigueA" : "fatigueB", 0d), ["active"] = ReadBool(record, "active", false) };
        }

        private static bool IsDirectorPeaceCommand(string command)
        {
            return command == "make_peace" || command == "offer_tribute_peace" || command == "demand_reparations_peace" || command == "demand_settlement_peace" || command == "demand_surrender_peace";
        }

        private static bool IsDirectorCivilWarPair(
            Dictionary<string, object> world, string firstKingdomId,
            string secondKingdomId)
        {
            if (string.IsNullOrWhiteSpace(firstKingdomId)
                || string.IsNullOrWhiteSpace(secondKingdomId)) return false;
            Dictionary<string, object> first = FindDirectorKingdom(world,
                firstKingdomId);
            Dictionary<string, object> second = FindDirectorKingdom(world,
                secondKingdomId);
            return string.Equals(ReadString(first,
                    "civilWarOpponentKingdomId", ""), secondKingdomId,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(second,
                    "civilWarOpponentKingdomId", ""), firstKingdomId,
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static bool DirectorCommandAllowedForIntent(string intent, string command)
        {
            return DirectorIntentCommands.TryGetValue(intent ?? string.Empty, out HashSet<string> commands)
                && commands.Contains(command ?? string.Empty);
        }

        internal static string DirectorIntentForCandidate(
            Dictionary<string, object> candidate,
            Dictionary<string, object> world)
        {
            string command = Program.CanonicalCommand(ReadString(candidate, "command", ""));
            if (IsDirectorPeaceCommand(command)) return "peace";
            if (IsDirectorAllianceProposal(candidate)
                || DirectorIntentCommands["security"].Contains(command))
                return "security";
            if (command == "diplomatic_package")
            {
                string actorId = ReadFirstString(candidate,
                    "actorKingdomStringId", "actorKingdomId");
                string targetId = ReadFirstString(candidate,
                    "targetKingdomStringId", "targetKingdomId");
                Dictionary<string, object> actor = FindDirectorKingdom(world,
                    actorId);
                if (actor != null && ReadStringList(actor, "enemies").Contains(
                        targetId, StringComparer.OrdinalIgnoreCase))
                    return "peace";
            }
            if (DirectorIntentCommands["prosperity"].Contains(command))
                return "prosperity";
            return "expansion";
        }

        private static bool IsDirectorPeacefulAgreement(string command)
        {
            return command == "sign_trade_agreement" || command == "sign_non_aggression_pact" || command == "sign_alliance" || command == "sign_defensive_pact" || command == "demilitarized_border";
        }

        private static bool IsDirectorCaptiveCommand(string command)
        {
            return IsDirectorPeaceCommand(command) || command == "exchange_prisoners" || command == "ransom_package";
        }

        private static string DirectorPairKey(string first, string second)
        {
            return string.Compare(first ?? "", second ?? "", StringComparison.OrdinalIgnoreCase) <= 0 ? (first + "|" + second) : (second + "|" + first);
        }

        private static double StableDirectorRoll(string campaignId, int day, string subject)
        {
            uint hash = (uint)StableDirectorSeed(campaignId, day, subject);
            return (hash % 1000000) / 1000000d;
        }

        internal static int StableDirectorSeed(string campaignId, int day, string subject)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in (campaignId + "|" + day.ToString(CultureInfo.InvariantCulture) + "|" + subject + "|world_diplomacy_v1"))
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7fffffff);
            }
        }

        private static double DirectorVirtue(Dictionary<string, object> virtues, string key) => Clamp(0d, 100d, ReadDouble(virtues, key, 50d));

        internal static string RequirePublicReason(Dictionary<string, object> source, string fallback)
        {
            string reason = ReadFirstString(source, "publicReason", "reason", "explanation").Trim();
            return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Length <= 900 ? reason : reason.Substring(0, 900);
        }

        private static string SelectDiplomaticOutcomeReason(
            Dictionary<string, object> source, string reportedAnswerKind,
            bool accepted, string acceptFallback, string refuseFallback)
        {
            string explicitReason = accepted
                ? ReadFirstString(source, "acceptReason", "accept_reason")
                : ReadFirstString(source, "refuseReason", "refuse_reason");
            if (!string.IsNullOrWhiteSpace(explicitReason))
                return explicitReason.Length <= 900 ? explicitReason : explicitReason.Substring(0, 900);

            bool reportedMatches = accepted
                ? string.Equals(reportedAnswerKind, "accept", StringComparison.OrdinalIgnoreCase)
                : string.Equals(reportedAnswerKind, "refuse", StringComparison.OrdinalIgnoreCase);
            if (reportedMatches)
            {
                string legacyReason = RequirePublicReason(source, string.Empty);
                if (!string.IsNullOrWhiteSpace(legacyReason)) return legacyReason;
            }
            return accepted ? acceptFallback : refuseFallback;
        }

        private static string DirectorEventTitle(string command, bool accepted)
        {
            string subject = command.Replace('_', ' ');
            subject = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(subject);
            return accepted ? subject : subject + " Refused";
        }

        private static string HumanizeDirectorOutcome(string outcome)
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase((outcome ?? "resolved").Replace('_', ' '));
        }

        private static string BuildDirectorEventSummary(string command, string actorName, string actorKingdom, string targetName, string targetKingdom, bool accepted, string outcome)
        {
            if (DirectorUnilateralCommands.Contains(command)) return actorName + " of " + actorKingdom + " has acted against " + targetKingdom + ".";
            return accepted
                ? actorName + " of " + actorKingdom + " and " + targetName + " of " + targetKingdom + " have concluded an agreement."
                : actorName + " of " + actorKingdom + " approached " + targetName + " of " + targetKingdom + ", but no agreement was reached.";
        }

        private static string BuildDirectorTermsText(Dictionary<string, object> terms)
        {
            if (terms == null || terms.Count == 0) return "No additional material terms were announced.";
            return string.Join("\n", terms.Select(x => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Key.Replace('_', ' ')) + ": " + FormatDirectorTerm(x.Value)));
        }

        private static string FormatDirectorTerm(object value)
        {
            if (value is ArrayList array) return string.Join(", ", array.Cast<object>().Select(Convert.ToString));
            if (value is IEnumerable<object> enumerable) return string.Join(", ", enumerable.Select(Convert.ToString));
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static ArrayList DirectorConsequenceTags(string command, bool accepted, Dictionary<string, object> terms)
        {
            ArrayList tags = new ArrayList { accepted ? "resolved" : "refused_offer" };
            if (command == "declare_war") tags.Add("aggression");
            if (IsDirectorPeaceCommand(command)) tags.Add("war_weariness");
            if (command.Contains("settlement") || command.Contains("surrender")) tags.Add("territorial_stakes");
            if (command.Contains("tribute") || command.Contains("reparations") || command.Contains("indemnity") || command.Contains("subsidy")) tags.Add("financial_burden");
            if (command == "break_treaty") tags.Add("broken_oath");
            if (command.Contains("alliance") || command.Contains("pact") || command.Contains("guarantee")) tags.Add("fulfilled_or_rejected_obligation");
            if (string.Equals(ReadString(terms, "treatyKind", ""), "alliance_package", StringComparison.OrdinalIgnoreCase)) tags.Add("fulfilled_or_rejected_obligation");
            return tags;
        }

        private static Dictionary<string, object> DirectorError(string error)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["status"] = "paused", ["error"] = DiplomacyErrorForDisplay(error) };
        }

        private static string DiplomacyErrorForDisplay(string error)
        {
            string raw = error ?? string.Empty;
            string lowered = raw.ToLowerInvariant();
            if (lowered.Contains("missing_api_key") || lowered.Contains("invalid authentication")
                || lowered.Contains("invalid_api_key") || lowered.Contains("unauthorized") || lowered.Contains("\"status\":401"))
            {
                return "The main LLM API key is missing or rejected. Open the Reign Control Center, save the key under Server Diagnostics, then run Test LLM Key.";
            }

            if (lowered.Contains("forbidden") || lowered.Contains("\"status\":403"))
            {
                return "The AI provider rejected the configured key, endpoint, or diplomacy model. Open Server Diagnostics and run Test LLM Key.";
            }

            return string.IsNullOrWhiteSpace(raw) ? "Unknown diplomacy director error." : raw;
        }

        private static Dictionary<string, object> DirectorRejected(string status, List<string> errors)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = status,
                ["errors"] = errors ?? new List<string>()
            };
        }

        private static Dictionary<string, object> DirectorValidationRejected(string status, List<string> errors)
        {
            Dictionary<string, object> result = DirectorRejected(status, errors);
            result["failureClass"] = "validation_failed";
            result["retryable"] = false;
            return result;
        }

        internal static Dictionary<string, object> ReadDirectorState(string campaignId)
        {
            Dictionary<string, object> state = Program.ReadJsonObject(Program.CampaignFile(campaignId, "diplomacy", "director-state.json"));
            if (!state.ContainsKey("version")) state["version"] = 1;
            return state;
        }

        private static void SaveDirectorState(string campaignId, Dictionary<string, object> state)
        {
            lock (Program.FileLock) WriteDirectorState(campaignId, state);
        }

        private static void WriteDirectorState(string campaignId, Dictionary<string, object> state)
        {
            Program.WriteJsonObject(Program.CampaignFile(campaignId, "diplomacy", "director-state.json"), state ?? new Dictionary<string, object>());
        }

        internal static List<Dictionary<string, object>> ReadDiplomaticEventQueue(string campaignId, bool requireValid = false)
        {
            string path = Program.CampaignFile(campaignId, "diplomacy", "event-queue.json");
            if (!File.Exists(path)) return new List<Dictionary<string, object>>();
            try
            {
                var queue = Json.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(path));
                if (requireValid && queue == null) throw new InvalidDataException("The public diplomatic queue is not a valid event list.");
                return queue ?? new List<Dictionary<string, object>>();
            }
            catch when (!requireValid)
            {
                return new List<Dictionary<string, object>>();
            }
        }

        internal static void WriteDiplomaticEventQueue(string campaignId, List<Dictionary<string, object>> queue)
        {
            string path = Program.CampaignFile(campaignId, "diplomacy", "event-queue.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(queue ?? new List<Dictionary<string, object>>()));
        }

        private static void WriteDirectorAudit(string campaignId, string phase, Dictionary<string, object> ruler, Dictionary<string, object> candidate, List<string> errors)
        {
            Program.WriteAudit(campaignId, ReadString(candidate, "correlationId", ""), "server", "diplomacy", "diplomacy." + phase,
                ReadString(ruler, "leaderHeroId", ""), "", "", "failed", 0, "World diplomacy candidate failed validation.", new Dictionary<string, object>
                {
                    ["candidate"] = candidate,
                    ["errors"] = errors
                });
        }
    }
}
