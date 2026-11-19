using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class ActionPipelineService
    {
        private static void ResolveActionReferences(Dictionary<string, object> raw, Dictionary<string, object> terms, Dictionary<string, object> payload, string resolutionText, string command, List<string> errors, List<Dictionary<string, object>> trace)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            if (index.Count == 0)
            {
                TraceResolver(trace, "index", "none", "", "missing_index", "", new List<string>());
                return;
            }

            string actorHero = ResolveIndexedEntity(index, "heroes", "hero",
                FirstNonEmpty(ReadFirstString(raw, "actorHeroStringId", "actorHeroId", "leaderHeroId", "heroId", "FromHero"), ReadFirstString(terms, "actorHeroStringId", "actorHeroId", "leaderHeroId", "heroId", "FromHero")),
                "heroStringId", new[] { "name" }, "actor hero", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(actorHero))
            {
                raw["actorHeroStringId"] = actorHero;
                AddIfMissing(terms, "fromHeroStringId", actorHero);
            }

            bool isPrisonerTransfer = string.Equals(command, "transfer_prisoner", StringComparison.OrdinalIgnoreCase);
            bool isAutonomousRansom = string.Equals(command, "ransom_package",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(raw, "source", ""),
                    "world_diplomacy_director", StringComparison.OrdinalIgnoreCase);
            string targetHeroQuery = isPrisonerTransfer
                ? FirstNonEmpty(ReadFirstString(raw, "targetHeroStringId", "targetHeroId", "TargetHero", "PrisonerHero"), ReadFirstString(terms, "targetHeroStringId", "targetHeroId", "TargetHero", "PrisonerHero", "prisonerHero"))
                : isAutonomousRansom
                    ? FirstNonEmpty(ReadFirstString(raw, "targetHeroStringId",
                            "targetHeroId", "TargetHero", "ToHero"),
                        ReadFirstString(terms, "targetHeroStringId",
                            "targetHeroId", "TargetHero", "ToHero"))
                : FirstNonEmpty(ReadFirstString(raw, "targetHeroStringId", "targetHeroId", "TargetHero", "ToHero", "PrisonerHero"), ReadFirstString(terms, "targetHeroStringId", "targetHeroId", "TargetHero", "ToHero", "PrisonerHero", "prisonerHero"));
            string targetHero = ResolveIndexedEntity(index, "heroes", "hero",
                targetHeroQuery,
                "heroStringId", new[] { "name" }, "target hero", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(targetHero))
            {
                raw["targetHeroStringId"] = targetHero;
                AddIfMissing(terms, "targetHeroStringId", targetHero);
                if (isPrisonerTransfer
                    || (!isAutonomousRansom
                        && (!string.IsNullOrWhiteSpace(ReadFirstString(raw,
                                "PrisonerHero", "prisonerHero"))
                            || !string.IsNullOrWhiteSpace(ReadFirstString(terms,
                                "PrisonerHero", "prisonerHero")))))
                {
                    AddIfMissing(terms, "prisonerHeroStringId", targetHero);
                }
                else
                {
                    AddIfMissing(terms, "toHeroStringId", targetHero);
                }
            }

            if (isAutonomousRansom)
            {
                string prisonerQuery = FirstNonEmpty(
                    ReadFirstString(raw, "PrisonerHero", "prisonerHero",
                        "prisonerHeroStringId"),
                    ReadFirstString(terms, "PrisonerHero", "prisonerHero",
                        "prisonerHeroStringId"));
                string prisonerHero = ResolveIndexedEntity(index, "heroes", "hero",
                    prisonerQuery, "heroStringId", new[] { "name" },
                    "ransom prisoner", errors, false, trace);
                if (!string.IsNullOrWhiteSpace(prisonerHero))
                {
                    raw["PrisonerHero"] = prisonerHero;
                    terms["prisonerHeroStringId"] = prisonerHero;
                }
            }

            if (string.Equals(command, "duel_player", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(ReadFirstString(raw, "actorHeroStringId", "actorHeroId"))
                && !string.IsNullOrWhiteSpace(targetHero))
            {
                raw["actorHeroStringId"] = targetHero;
                AddIfMissing(terms, "fromHeroStringId", targetHero);
                TraceResolver(trace, "duel actor", "hero", "targetHero", "duel_default_actor", targetHero, new List<string> { targetHero });
            }

            string toHero = ResolveIndexedEntity(index, "heroes", "hero",
                FirstNonEmpty(ReadFirstString(raw, "toHeroStringId", "toHeroId", "ToHero"), ReadFirstString(terms, "toHeroStringId", "toHeroId", "ToHero", "toHero")),
                "heroStringId", new[] { "name" }, "receiving hero", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(toHero))
            {
                AddIfMissing(terms, "toHeroStringId", toHero);
            }

            string killerHero = ResolveIndexedEntity(index, "heroes", "hero",
                FirstNonEmpty(ReadFirstString(raw, "killerHeroStringId", "killerHeroId", "KillerHero"), ReadFirstString(terms, "killerHeroStringId", "killerHeroId", "KillerHero", "killerHero")),
                "heroStringId", new[] { "name" }, "killer hero", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(killerHero))
            {
                terms["killerHeroStringId"] = killerHero;
            }

            string actorClan = ResolveIndexedEntity(index, "clans", "clan",
                FirstNonEmpty(ReadFirstString(raw, "actorClanStringId", "actorClanId", "claimantClanId", "FromClan"), ReadFirstString(terms, "actorClanStringId", "actorClanId", "claimantClanId", "FromClan")),
                "clanId", new[] { "name", "leaderName" }, "actor clan", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(actorClan))
            {
                raw["actorClanStringId"] = actorClan;
            }

            string targetClan = ResolveIndexedEntity(index, "clans", "clan",
                FirstNonEmpty(ReadFirstString(raw, "targetClanStringId", "targetClanId", "TargetClan", "ToClan"), ReadFirstString(terms, "targetClanStringId", "targetClanId", "TargetClan", "ToClan")),
                "clanId", new[] { "name", "leaderName" }, "target clan", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(targetClan))
            {
                raw["targetClanStringId"] = targetClan;
            }

            string family = PromptService.ActionFamilyForId(command);
            bool strictKingdomReferences = string.Equals(family, "diplomacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(family, "politics", StringComparison.OrdinalIgnoreCase);
            string actorKingdomQuery = NormalizeKingdomQuery(index, payload, raw, terms,
                FirstNonEmpty(ReadFirstString(raw, "actorKingdomStringId", "actorKingdomId", "kingdomId", "FromKingdom"), ReadFirstString(terms, "actorKingdomStringId", "actorKingdomId", "kingdomId", "FromKingdom")),
                true);
            string actorKingdom = ResolveIndexedEntity(index, "kingdoms", "kingdom",
                actorKingdomQuery,
                "kingdomId", new[] { "name", "formalName", "leaderName", "rulingClanName" }, "actor kingdom", errors, strictKingdomReferences, trace);
            if (!strictKingdomReferences && !string.IsNullOrWhiteSpace(actorKingdom) && !IndexContainsEntityId(index, "kingdoms", "kingdomId", actorKingdom))
            {
                actorKingdom = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(actorKingdom))
            {
                raw["actorKingdomStringId"] = actorKingdom;
            }

            string targetKingdomQuery = NormalizeKingdomQuery(index, payload, raw, terms,
                FirstNonEmpty(ReadFirstString(raw, "targetKingdomStringId", "targetKingdomId", "TargetKingdom", "ToKingdom"), ReadFirstString(terms, "targetKingdomStringId", "targetKingdomId", "TargetKingdom", "ToKingdom")),
                false);
            string targetKingdom = ResolveIndexedEntity(index, "kingdoms", "kingdom",
                targetKingdomQuery,
                "kingdomId", new[] { "name", "formalName", "leaderName", "rulingClanName" }, "target kingdom", errors, strictKingdomReferences, trace);
            if (!strictKingdomReferences && !string.IsNullOrWhiteSpace(targetKingdom) && !IndexContainsEntityId(index, "kingdoms", "kingdomId", targetKingdom))
            {
                targetKingdom = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(targetKingdom))
            {
                raw["targetKingdomStringId"] = targetKingdom;
            }

            string targetSettlement = ResolveIndexedEntity(index, "settlements", "settlement",
                FirstNonEmpty(ReadFirstString(raw, "targetSettlementStringId", "targetSettlementId", "settlementId", "TargetSettlement", "ToSettlement"), ReadFirstString(terms, "targetSettlementStringId", "targetSettlementId", "settlementId", "TargetSettlement", "ToSettlement", "targetSettlement", "settlementName")),
                "settlementId", new[] { "name" }, "target settlement", errors, true, trace);
            List<string> settlementTerms = ReadStringList(terms, "settlementIds");
            List<string> resolvedSettlementTerms = new List<string>();
            foreach (string settlementTerm in settlementTerms)
            {
                string resolved = ResolveIndexedEntity(index, "settlements", "settlement", settlementTerm, "settlementId", new[] { "name" }, "terms.settlementIds", errors, true, trace);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    resolvedSettlementTerms.Add(resolved);
                }
            }

            if (string.IsNullOrWhiteSpace(targetSettlement) && CanInferSettlementFromText(command))
            {
                targetSettlement = ResolveSemanticSettlementFromText(index, resolutionText, command, trace);
                if (string.IsNullOrWhiteSpace(targetSettlement))
                {
                    targetSettlement = ResolveMentionedSettlement(index, resolutionText, ReadFirstString(raw, "targetKingdomStringId", "targetKingdomId"), errors, trace);
                }
            }

            if (!string.IsNullOrWhiteSpace(targetSettlement))
            {
                raw["targetSettlementStringId"] = targetSettlement;
                AddIfMissing(terms, "targetSettlementStringId", targetSettlement);
                if (resolvedSettlementTerms.Count == 0 && (string.Equals(command, "demand_settlement_peace", StringComparison.OrdinalIgnoreCase) || string.Equals(command, "demand_surrender_peace", StringComparison.OrdinalIgnoreCase)))
                {
                    resolvedSettlementTerms.Add(targetSettlement);
                }
            }

            if (resolvedSettlementTerms.Count > 0)
            {
                terms["settlementIds"] = new ArrayList(resolvedSettlementTerms.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }

            if (string.Equals(command, "demand_surrender_peace", StringComparison.OrdinalIgnoreCase)
                && resolvedSettlementTerms.Count == 0
                && string.IsNullOrWhiteSpace(targetSettlement))
            {
                terms["allTargetFortifications"] = true;
            }

            string targetParty = ResolveIndexedEntity(index, "parties", "party", FirstNonEmpty(ReadFirstString(raw, "targetPartyId", "TargetParty"), ReadFirstString(terms, "targetPartyId", "TargetParty", "targetParty")), "partyId", new[] { "name", "leaderName" }, "target party", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(targetParty))
            {
                terms["targetPartyId"] = targetParty;
            }

            string targetArmy = ResolveIndexedEntity(index, "armies", "army", FirstNonEmpty(ReadFirstString(raw, "targetArmyId", "TargetArmy"), ReadFirstString(terms, "targetArmyId", "TargetArmy", "targetArmy")), "armyId", new[] { "name", "leaderName" }, "target army", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(targetArmy))
            {
                terms["targetArmyId"] = targetArmy;
            }

            ResolveItemReference(index, raw, terms, trace);
            ResolveWorkshopReference(index, raw, terms, errors, trace);
        }

        private static void ResolveWorkshopReference(Dictionary<string, object> index, Dictionary<string, object> raw, Dictionary<string, object> terms, List<string> errors, List<Dictionary<string, object>> trace)
        {
            string workshopQuery = FirstNonEmpty(ReadFirstString(raw, "Workshop", "workshop", "workshopId"), ReadFirstString(terms, "Workshop", "workshop", "workshopId"));
            if (string.IsNullOrWhiteSpace(workshopQuery))
            {
                return;
            }

            string workshopId = ResolveIndexedEntity(index, "workshops", "workshop", workshopQuery, "workshopId", new[] { "name", "typeName", "settlementName", "tag" }, "workshop", errors, false, trace);
            if (!string.IsNullOrWhiteSpace(workshopId))
            {
                terms["workshopId"] = workshopId;
            }
        }

        private static bool CanInferSettlementFromText(string command)
        {
            switch (CanonicalCommand(command))
            {
                case "trade_package":
                case "demand_settlement_peace":
                case "return_occupied_settlement":
                case "recruit_and_recover":
                case "form_army":
                case "attack_settlement":
                case "capture_settlement_plan":
                case "go_to_settlement":
                case "patrol_around_settlement":
                case "wait_near_settlement":
                case "raid_village":
                case "besiege_settlement":
                    return true;
                default:
                    return false;
            }
        }

        private static string ResolveSemanticSettlementFromText(Dictionary<string, object> index, string text, string command, List<Dictionary<string, object>> trace)
        {
            string query = (text ?? string.Empty).Trim();
            string normalized = NormalizeLookup(query);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            bool hasSemanticShape = normalized.Contains("nearest")
                || normalized.Contains("closest")
                || normalized.Contains("friendly")
                || normalized.Contains("enemy")
                || normalized.Contains("hostile")
                || normalized.Contains("village")
                || normalized.Contains("town")
                || normalized.Contains("city")
                || normalized.Contains("castle")
                || normalized.Contains("hideout");
            if (!hasSemanticShape)
            {
                return string.Empty;
            }

            List<Dictionary<string, object>> settlements = ReadDictionaryList(index, "settlements");
            return ResolveSemanticEntity(index, settlements, "settlements", "settlement", normalized, "settlementId", trace, "semantic settlement", query);
        }

        private static string NormalizeKingdomQuery(Dictionary<string, object> index, Dictionary<string, object> payload, Dictionary<string, object> raw, Dictionary<string, object> terms, string query, bool actorRole)
        {
            string value = (query ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || !LooksLikeClanIdentifier(value))
            {
                return value;
            }

            string clanKingdom = FindKingdomIdForClanReference(index, value);
            if (!string.IsNullOrWhiteSpace(clanKingdom))
            {
                return clanKingdom;
            }

            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            string playerClanId = FirstNonEmpty(
                ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                ReadFirstString(raw, "actorClanStringId", "actorClanId"),
                ReadFirstString(terms, "actorClanStringId", "actorClanId"));
            string speakerClanId = FirstNonEmpty(
                ReadFirstString(payload, "speakerClanId", "targetClanId", "targetClanStringId"),
                ReadFirstString(raw, "targetClanStringId", "targetClanId"),
                ReadFirstString(terms, "targetClanStringId", "targetClanId"));
            string playerKingdomId = FirstUsableKingdomReference(index,
                ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                ReadString(hero, "playerKingdomId", ""));
            string speakerKingdomId = FirstUsableKingdomReference(index,
                ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                ReadString(hero, "kingdomId", ""));

            if (!string.IsNullOrWhiteSpace(speakerClanId) && string.Equals(value, speakerClanId, StringComparison.OrdinalIgnoreCase))
            {
                return FirstNonEmpty(speakerKingdomId, value);
            }

            if (!string.IsNullOrWhiteSpace(playerClanId) && string.Equals(value, playerClanId, StringComparison.OrdinalIgnoreCase))
            {
                return FirstNonEmpty(playerKingdomId, value);
            }

            return actorRole ? FirstNonEmpty(playerKingdomId, speakerKingdomId, value) : FirstNonEmpty(speakerKingdomId, playerKingdomId, value);
        }

        private static bool LooksLikeClanIdentifier(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string q = value.Trim();
            return q.StartsWith("clan_", StringComparison.OrdinalIgnoreCase)
                || q.EndsWith("_clan", StringComparison.OrdinalIgnoreCase)
                || q.IndexOf("clan.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FindKingdomIdForClanReference(Dictionary<string, object> index, string query)
        {
            string normalized = NormalizeLookup(query);
            if (index == null || string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            foreach (Dictionary<string, object> clan in ReadDictionaryList(index, "clans"))
            {
                string clanId = ReadString(clan, "clanId", "");
                string clanName = ReadString(clan, "name", "");
                string leaderName = ReadString(clan, "leaderName", "");
                if (!string.Equals(clanId, query, StringComparison.OrdinalIgnoreCase)
                    && NormalizeLookup(clanName) != normalized
                    && NormalizeLookup(leaderName) != normalized)
                {
                    continue;
                }

                string kingdom = FirstUsableKingdomReference(index,
                    ReadFirstString(clan, "kingdomId", "kingdomStringId", "factionId", "mapFactionId", "ownerKingdomId"),
                    ReadFirstString(clan, "kingdomName", "factionName"));
                if (!string.IsNullOrWhiteSpace(kingdom))
                {
                    return kingdom;
                }
            }

            return string.Empty;
        }

        private static string FirstUsableKingdomReference(Dictionary<string, object> index, params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            foreach (string value in values)
            {
                string resolved = ResolveKingdomIdLenient(index, value);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            return string.Empty;
        }

        private static string ResolveKingdomIdLenient(Dictionary<string, object> index, string value)
        {
            string query = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query) || LooksLikeClanIdentifier(query))
            {
                return string.Empty;
            }

            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(index, "kingdoms");
            if (kingdoms.Count == 0)
            {
                return query;
            }

            Dictionary<string, object> idMatch = kingdoms.FirstOrDefault(x => string.Equals(ReadString(x, "kingdomId", ""), query, StringComparison.OrdinalIgnoreCase));
            if (idMatch != null)
            {
                return ReadString(idMatch, "kingdomId", "");
            }

            string normalized = NormalizeLookup(query);
            List<Dictionary<string, object>> nameMatches = kingdoms
                .Where(x => new[] { "name", "formalName", "leaderName", "rulingClanName" }.Any(key => NormalizeLookup(ReadString(x, key, "")) == normalized))
                .ToList();
            return nameMatches.Count == 1 ? ReadString(nameMatches[0], "kingdomId", "") : string.Empty;
        }

        private static bool IndexContainsEntityId(Dictionary<string, object> index, string collectionKey, string idKey, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            List<Dictionary<string, object>> entries = ReadDictionaryList(index, collectionKey);
            if (entries.Count == 0)
            {
                return true;
            }

            return entries.Any(x => string.Equals(ReadString(x, idKey, ""), id, StringComparison.OrdinalIgnoreCase));
        }

        internal static string ResolveIndexedEntity(Dictionary<string, object> index, string collectionKey, string entityType, string rawValue, string idKey, string[] nameKeys, string label, List<string> errors, bool strictWhenIndexed, List<Dictionary<string, object>> trace)
        {
            string query = (rawValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
            {
                return string.Empty;
            }

            string alias = ResolveAlias(index, entityType, query);
            if (!string.IsNullOrWhiteSpace(alias))
            {
                TraceResolver(trace, label, entityType, query, "alias", alias, new List<string> { alias });
                return alias;
            }

            List<Dictionary<string, object>> entries = ReadDictionaryList(index, collectionKey);
            if (entries.Count == 0)
            {
                TraceResolver(trace, label, entityType, query, "no_index_entries", query, new List<string>());
                return query;
            }

            Dictionary<string, object> idMatch = entries.FirstOrDefault(x => string.Equals(ReadString(x, idKey, ""), query, StringComparison.OrdinalIgnoreCase));
            if (idMatch != null)
            {
                TraceResolver(trace, label, entityType, query, "id", ReadString(idMatch, idKey, ""), new List<string> { ReadString(idMatch, "name", ReadString(idMatch, idKey, "")) });
                return ReadString(idMatch, idKey, "");
            }

            string normalized = NormalizeLookup(query);
            List<Dictionary<string, object>> exactNameMatches = entries
                .Where(x => nameKeys.Any(key => NormalizeLookup(ReadString(x, key, "")) == normalized))
                .ToList();
            if (exactNameMatches.Count == 1)
            {
                TraceResolver(trace, label, entityType, query, "exact_name", ReadString(exactNameMatches[0], idKey, ""), exactNameMatches.Select(x => ReadString(x, "name", ReadString(x, idKey, ""))).ToList());
                return ReadString(exactNameMatches[0], idKey, "");
            }

            if (exactNameMatches.Count > 1)
            {
                errors.Add("Ambiguous " + label + " '" + query + "': " + string.Join(", ", exactNameMatches.Take(6).Select(x => ReadString(x, "name", ReadString(x, idKey, "")))));
                TraceResolver(trace, label, entityType, query, "ambiguous_exact_name", "", exactNameMatches.Take(12).Select(x => ReadString(x, "name", ReadString(x, idKey, ""))).ToList());
                return string.Empty;
            }

            string semantic = ResolveSemanticEntity(index, entries, collectionKey, entityType, normalized, idKey, trace, label, query);
            if (!string.IsNullOrWhiteSpace(semantic))
            {
                return semantic;
            }

            List<Dictionary<string, object>> partialMatches = entries
                .Where(x => nameKeys.Any(key =>
                {
                    string candidate = NormalizeLookup(ReadString(x, key, ""));
                    return candidate.Length >= 3 && normalized.Length >= 3 && (candidate.Contains(normalized) || normalized.Contains(candidate));
                }))
                .ToList();
            if (partialMatches.Count == 1)
            {
                TraceResolver(trace, label, entityType, query, "partial_name", ReadString(partialMatches[0], idKey, ""), partialMatches.Select(x => ReadString(x, "name", ReadString(x, idKey, ""))).ToList());
                return ReadString(partialMatches[0], idKey, "");
            }

            if (partialMatches.Count > 1)
            {
                errors.Add("Ambiguous " + label + " '" + query + "': " + string.Join(", ", partialMatches.Take(6).Select(x => ReadString(x, "name", ReadString(x, idKey, "")))));
                TraceResolver(trace, label, entityType, query, "ambiguous_partial_name", "", partialMatches.Take(12).Select(x => ReadString(x, "name", ReadString(x, idKey, ""))).ToList());
                return string.Empty;
            }

            if (strictWhenIndexed)
            {
                errors.Add("Could not resolve " + label + " '" + query + "' from the current campaign index.");
                TraceResolver(trace, label, entityType, query, "not_found", "", new List<string>());
                return string.Empty;
            }

            TraceResolver(trace, label, entityType, query, "unresolved_passthrough", query, new List<string>());
            return query;
        }

        internal static string ResolveAlias(Dictionary<string, object> index, string entityType, string query)
        {
            string normalized = NormalizeLookup(query);
            foreach (Dictionary<string, object> alias in ReadDictionaryList(index, "aliases"))
            {
                if (string.Equals(ReadString(alias, "entityType", ""), entityType, StringComparison.OrdinalIgnoreCase)
                    && NormalizeLookup(ReadString(alias, "alias", "")) == normalized)
                {
                    return ReadString(alias, "id", "");
                }
            }

            foreach (Dictionary<string, object> recent in ReadDictionaryList(index, "recentContext"))
            {
                if (string.Equals(ReadString(recent, "entityType", ""), entityType, StringComparison.OrdinalIgnoreCase)
                    && (NormalizeLookup(ReadString(recent, "label", "")) == normalized || NormalizeLookup(ReadString(recent, "name", "")) == normalized))
                {
                    return ReadString(recent, "id", "");
                }
            }

            return string.Empty;
        }

        private static string ResolveSemanticEntity(Dictionary<string, object> index, List<Dictionary<string, object>> entries, string collectionKey, string entityType, string normalizedQuery, string idKey, List<Dictionary<string, object>> trace, string label, string rawQuery)
        {
            if (entries == null || entries.Count == 0 || string.IsNullOrWhiteSpace(normalizedQuery))
            {
                return string.Empty;
            }

            if (collectionKey == "settlements")
            {
                bool wantsNearest = normalizedQuery.Contains("nearest") || normalizedQuery.Contains("closest");
                bool wantsFriendly = normalizedQuery.Contains("friendly") || normalizedQuery.Contains("our ");
                bool wantsEnemy = normalizedQuery.Contains("enemy") || normalizedQuery.Contains("hostile");
                string mentionedKingdomId = FindMentionedKingdomId(index, normalizedQuery);
                string wantedType = normalizedQuery.Contains("village") ? "village"
                    : normalizedQuery.Contains("castle") ? "castle"
                    : (normalizedQuery.Contains("town") || normalizedQuery.Contains("city")) ? "town"
                    : normalizedQuery.Contains("hideout") ? "hideout"
                    : string.Empty;

                if (wantsNearest || !string.IsNullOrWhiteSpace(wantedType) || !string.IsNullOrWhiteSpace(mentionedKingdomId))
                {
                    string playerKingdom = ResolveAlias(index, "kingdom", "player_kingdom");
                    IEnumerable<Dictionary<string, object>> filtered = entries;
                    if (!string.IsNullOrWhiteSpace(wantedType))
                    {
                        filtered = filtered.Where(x => string.Equals(ReadString(x, "type", ""), wantedType, StringComparison.OrdinalIgnoreCase));
                    }

                    if (!string.IsNullOrWhiteSpace(mentionedKingdomId))
                    {
                        filtered = filtered.Where(x => string.Equals(ReadString(x, "kingdomId", ""), mentionedKingdomId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ReadString(x, "ownerKingdomId", ""), mentionedKingdomId, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(ReadString(x, "mapFactionId", ""), mentionedKingdomId, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (wantsFriendly && !string.IsNullOrWhiteSpace(playerKingdom))
                    {
                        filtered = filtered.Where(x => string.Equals(ReadString(x, "kingdomId", ""), playerKingdom, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (wantsEnemy && !string.IsNullOrWhiteSpace(playerKingdom))
                    {
                        filtered = filtered.Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "kingdomId", "")) && !string.Equals(ReadString(x, "kingdomId", ""), playerKingdom, StringComparison.OrdinalIgnoreCase));
                    }

                    Dictionary<string, object> best = filtered.OrderBy(x => ReadDouble(x, "distance", double.MaxValue)).FirstOrDefault();
                    if (best != null)
                    {
                        string resolved = ReadString(best, idKey, "");
                        TraceResolver(trace, label, entityType, rawQuery, "semantic", resolved, new List<string> { ReadString(best, "name", resolved) });
                        return resolved;
                    }
                }
            }

            if (collectionKey == "clans" && normalizedQuery.Contains("strongest"))
            {
                Dictionary<string, object> best = entries.OrderByDescending(x => ReadInt(x, "tier", 0)).ThenByDescending(x => ReadDouble(x, "renown", 0d)).FirstOrDefault();
                if (best != null)
                {
                    string resolved = ReadString(best, idKey, "");
                    TraceResolver(trace, label, entityType, rawQuery, "semantic", resolved, new List<string> { ReadString(best, "name", resolved) });
                    return resolved;
                }
            }

            return string.Empty;
        }

        private static string FindMentionedKingdomId(Dictionary<string, object> index, string normalizedQuery)
        {
            if (index == null || string.IsNullOrWhiteSpace(normalizedQuery))
            {
                return string.Empty;
            }

            List<string> matches = new List<string>();
            foreach (Dictionary<string, object> kingdom in ReadDictionaryList(index, "kingdoms"))
            {
                string id = ReadString(kingdom, "kingdomId", "");
                foreach (string alias in BuildKingdomLookupAliases(kingdom))
                {
                    if (NormalizedTextContainsPhrase(normalizedQuery, alias))
                    {
                        matches.Add(id);
                        break;
                    }
                }
            }

            List<string> unique = matches.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return unique.Count == 1 ? unique[0] : string.Empty;
        }

        private static List<string> BuildKingdomLookupAliases(Dictionary<string, object> kingdom)
        {
            List<string> aliases = new List<string>();
            foreach (string key in new[] { "kingdomId", "name", "formalName", "informalName", "leaderName", "rulingClanName" })
            {
                AddKingdomAlias(aliases, ReadString(kingdom, key, ""));
            }

            string name = NormalizeLookup(ReadString(kingdom, "name", ""));
            if (!string.IsNullOrWhiteSpace(name))
            {
                AddKingdomAlias(aliases, name + "s");
                if (name.EndsWith("ia", StringComparison.OrdinalIgnoreCase))
                {
                    AddKingdomAlias(aliases, name + "n");
                    AddKingdomAlias(aliases, name + "ns");
                }
                else if (name.EndsWith("ait", StringComparison.OrdinalIgnoreCase))
                {
                    AddKingdomAlias(aliases, name + "s");
                }
            }

            return aliases.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddKingdomAlias(List<string> aliases, string value)
        {
            string normalized = NormalizeLookup(value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                aliases.Add(normalized);
            }
        }

        internal static bool NormalizedTextContainsPhrase(string normalizedText, string normalizedPhrase)
        {
            if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedPhrase))
            {
                return false;
            }

            if (string.Equals(normalizedText, normalizedPhrase, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (" " + normalizedText + " ").Contains(" " + normalizedPhrase + " ");
        }

        internal static void ResolveItemReference(Dictionary<string, object> index, Dictionary<string, object> raw, Dictionary<string, object> terms, List<Dictionary<string, object>> trace)
        {
            string itemQuery = FirstNonEmpty(ReadFirstString(raw, "Item", "item", "targetItem"), ReadFirstString(terms, "Item", "item", "targetItem"));
            if (string.IsNullOrWhiteSpace(itemQuery))
            {
                return;
            }

            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            Dictionary<string, object> assets = ReadDictionary(index, "assets") ?? new Dictionary<string, object>();
            foreach (string side in new[] { "player", "speaker" })
            {
                Dictionary<string, object> holder = ReadDictionary(assets, side) ?? new Dictionary<string, object>();
                Dictionary<string, object> inventory = ReadDictionary(holder, "inventory") ?? new Dictionary<string, object>();
                items.AddRange(DialogueService.ActionInventoryItems(inventory));
                Dictionary<string, object> appearance = ReadDictionary(holder, "appearance") ?? new Dictionary<string, object>();
                items.AddRange(ReadDictionaryList(appearance, "civilianEquipment"));
                items.AddRange(ReadDictionaryList(appearance, "battleEquipment"));
            }

            string normalized = NormalizeLookup(itemQuery);
            var exactMatches = items.Where(x => NormalizeLookup(ReadString(x, "itemId", "")) == normalized || NormalizeLookup(ReadString(x, "name", "")) == normalized).ToList();
            Dictionary<string, object> exact = exactMatches.FirstOrDefault();
            if (exactMatches.Select(x => ReadString(x, "itemId", "")).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                TraceResolver(trace, "item", "item", itemQuery, "ambiguous_item", "", exactMatches.Select(x => ReadString(x, "itemId", "")).ToList());
                return;
            }
            if (exact != null)
            {
                // Resolve the item type without silently choosing one of several
                // modifiers or equipped copies. Native ownership validates the copy.
                string id = ApplyResolvedItemTerms(terms, exactMatches.Count == 1 ? exact : TestDict("itemId", ReadString(exact, "itemId", "")));
                TraceResolver(trace, "item", "item", itemQuery, "exact_item", id, new List<string> { ReadString(exact, "name", id) });
                return;
            }

            Dictionary<string, object> semantic = ResolveSemanticItemReference(itemQuery, items);
            if (semantic != null)
            {
                string id = ApplyResolvedItemTerms(terms, semantic);
                TraceResolver(trace, "item", "item", itemQuery, "semantic_item", id, new List<string> { ReadString(semantic, "name", id) });
                return;
            }

            List<Dictionary<string, object>> partial = items.Where(x =>
            {
                string name = NormalizeLookup(ReadString(x, "name", ""));
                string id = NormalizeLookup(ReadString(x, "itemId", ""));
                return normalized.Length > 0
                    && ((name.Length > 0 && (name.Contains(normalized) || normalized.Contains(name)))
                        || (id.Length > 0 && (id.Contains(normalized) || normalized.Contains(id))));
            }).ToList();
            if (partial.Count == 1)
            {
                string id = ApplyResolvedItemTerms(terms, partial[0]);
                TraceResolver(trace, "item", "item", itemQuery, "partial_item", id, new List<string> { ReadString(partial[0], "name", id) });
            }
            else if (partial.Count > 1)
            {
                TraceResolver(trace, "item", "item", itemQuery, "ambiguous_item", "", partial.Take(12).Select(x => ReadString(x, "name", ReadString(x, "itemId", ""))).ToList());
            }
            else
            {
                TraceResolver(trace, "item", "item", itemQuery, "not_found", "", new List<string>());
            }
        }

        internal static string ApplyResolvedItemTerms(Dictionary<string, object> terms, Dictionary<string, object> item)
        {
            if (item.ContainsKey("itemModifierId")) AddIfMissing(terms, "itemModifierId", ReadString(item, "itemModifierId", ""));
            string id = ReadString(item, "itemId", "");
            if (!string.IsNullOrWhiteSpace(id))
            {
                terms["itemId"] = id;
            }

            string slot = ReadString(item, "slot", "");
            if (!string.IsNullOrWhiteSpace(slot))
            {
                AddIfMissing(terms, "sourceEquipmentSlot", slot);
            }

            string equipmentSet = ReadString(item, "sourceEquipmentSet", "");
            if (!string.IsNullOrWhiteSpace(equipmentSet))
            {
                AddIfMissing(terms, "sourceEquipmentSet", equipmentSet);
            }

            return id;
        }

        internal static Dictionary<string, object> ResolveSemanticItemReference(string itemQuery, List<Dictionary<string, object>> items)
        {
            string normalized = NormalizeLookup(itemQuery);
            if (items == null || items.Count == 0 || string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            IEnumerable<Dictionary<string, object>> candidates = Enumerable.Empty<Dictionary<string, object>>();
            if (normalized.Contains("armor") || normalized.Contains("armour") || normalized.Contains("clothing") || normalized.Contains("clothes"))
            {
                candidates = items.Where(x => ReadBool(x, "isArmorOrClothing", false));
            }
            else if (normalized.Contains("helmet") || normalized.Contains("head"))
            {
                candidates = items.Where(x => NormalizeLookup(ReadString(x, "slot", "")).Contains("head") || NormalizeLookup(ReadString(x, "name", "")).Contains("helmet"));
            }
            else if (normalized.Contains("boots") || normalized.Contains("shoe"))
            {
                candidates = items.Where(x => NormalizeLookup(ReadString(x, "slot", "")).Contains("leg") || NormalizeLookup(ReadString(x, "name", "")).Contains("boot"));
            }
            else if (normalized.Contains("gloves"))
            {
                candidates = items.Where(x => NormalizeLookup(ReadString(x, "slot", "")).Contains("glove") || NormalizeLookup(ReadString(x, "name", "")).Contains("glove"));
            }
            else if (normalized.Contains("cloak") || normalized.Contains("cape"))
            {
                candidates = items.Where(x => NormalizeLookup(ReadString(x, "slot", "")).Contains("cape") || NormalizeLookup(ReadString(x, "name", "")).Contains("cloak") || NormalizeLookup(ReadString(x, "name", "")).Contains("cape"));
            }
            else if (normalized.Contains("horse") || normalized.Contains("mount"))
            {
                candidates = items.Where(x => ReadBool(x, "isMount", false) || ReadBool(x, "isMountable", false));
            }
            else if (normalized.Contains("weapon"))
            {
                candidates = items.Where(x => ReadBool(x, "isWeapon", false));
            }
            else if (normalized.Contains("sword") || normalized.Contains("axe") || normalized.Contains("spear") || normalized.Contains("bow") || normalized.Contains("shield"))
            {
                candidates = items.Where(x =>
                {
                    string name = NormalizeLookup(ReadString(x, "name", "") + " " + ReadString(x, "itemId", "") + " " + ReadString(x, "type", ""));
                    return name.Contains(normalized) || normalized.Contains(name);
                });
            }

            var matches = candidates
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "itemId", "")))
                .ToList();
            // A category such as "armor" is not permission to take the most
            // valuable item or select an arbitrary modified/equipped copy.
            return matches.Count == 1 ? matches[0] : null;
        }

        private static string ResolveMentionedSettlement(Dictionary<string, object> index, string text, string targetKingdomId, List<string> errors, List<Dictionary<string, object>> trace)
        {
            string normalizedText = NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalizedText))
            {
                return string.Empty;
            }

            List<Dictionary<string, object>> matches = ReadDictionaryList(index, "settlements")
                .Where(x => string.IsNullOrWhiteSpace(targetKingdomId) || string.Equals(ReadString(x, "kingdomId", ""), targetKingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(x =>
                {
                    string name = NormalizeLookup(ReadString(x, "name", ""));
                    return name.Length >= 3 && ContainsNormalizedPhrase(normalizedText, name);
                })
                .ToList();

            if (matches.Count == 1)
            {
                TraceResolver(trace, "mentioned settlement", "settlement", text, "mentioned_text", ReadString(matches[0], "settlementId", ""), new List<string> { ReadString(matches[0], "name", "") });
                return ReadString(matches[0], "settlementId", "");
            }

            if (matches.Count > 1)
            {
                errors.Add("Ambiguous settlement mention: " + string.Join(", ", matches.Take(6).Select(x => ReadString(x, "name", ReadString(x, "settlementId", "")))));
                TraceResolver(trace, "mentioned settlement", "settlement", text, "ambiguous_mentioned_text", "", matches.Take(12).Select(x => ReadString(x, "name", ReadString(x, "settlementId", ""))).ToList());
            }

            return string.Empty;
        }

        internal static string NormalizeLookup(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string decomposed = value.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new StringBuilder();
            bool previousSpace = false;
            foreach (char c in decomposed)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                char lower = char.ToLowerInvariant(c);
                if (char.IsLetterOrDigit(lower))
                {
                    builder.Append(lower);
                    previousSpace = false;
                }
                else if (!previousSpace)
                {
                    builder.Append(' ');
                    previousSpace = true;
                }
            }

            return builder.ToString().Trim();
        }

        private static string InferCommandFromNaturalAction(Dictionary<string, object> raw, Dictionary<string, object> terms, Dictionary<string, object> payload, string resolutionText, List<Dictionary<string, object>> trace)
        {
            string text = string.Join(" ", new[]
            {
                ReadFirstString(raw, "proposal", "message_type", "messageType", "treaty", "treatyType", "SocialAction", "socialAction", "Policy", "policy", "Claim", "claim", "Promise", "promise"),
                ReadFirstString(terms, "proposal", "message_type", "messageType", "treaty", "treatyType", "SocialAction", "socialAction", "Policy", "policy", "Claim", "claim", "Promise", "promise"),
                resolutionText ?? string.Empty
            }).Trim();

            string normalized = NormalizeLookup(text);
            string command = string.Empty;
            string lowerText = (text ?? string.Empty).ToLowerInvariant();
            bool namedSettlementTrade = DirectiveHasAny(lowerText, "gold", "denar", "denars", "coin", "coins", "money", "silver", "price")
                && DirectiveHasAny(lowerText, " for ", "price", "trade", "sell", "sold", "buy", "purchase", "deal", "bargain", "in exchange", "pay", "payment")
                && !string.IsNullOrWhiteSpace(FindMentionedSettlementName(payload, text, ""));
            if (LooksLikePaidAssetTradeDirective(text) || namedSettlementTrade || ContainsAnyNormalized(normalized, "gold for", "denars for", "grain for", "horses for", "prisoner for", "settlement for", "town for", "castle for"))
            {
                command = "trade_package";
            }
            else if (ContainsAnyNormalized(normalized, "full surrender", "capitulation", "give all towns", "give all castles", "submit kingdom"))
            {
                command = "demand_surrender_peace";
            }
            else if (LooksLikeNavalAssetDirective(text))
            {
                command = "trade_package";
            }
            else if (LooksLikeSettlementTransferDirective(text) || ContainsAnyNormalized(normalized, "surrender town", "surrender city", "surrender castle"))
            {
                command = "demand_settlement_peace";
            }
            else if (ContainsAnyNormalized(normalized, "non aggression", "nonaggression", "do not attack", "no attack pact"))
            {
                command = "sign_non_aggression_pact";
            }
            else if (ContainsAnyNormalized(normalized, "defensive pact", "defend each other"))
            {
                command = "sign_defensive_pact";
            }
            else if (ContainsAnyNormalized(normalized, "alliance", "ally"))
            {
                command = "sign_alliance";
            }
            else if (ContainsAnyNormalized(normalized, "trade agreement", "trade treaty", "commerce pact"))
            {
                command = "sign_trade_agreement";
            }
            else if (ContainsAnyNormalized(normalized, "ransom", "gold for prisoner", "denars for prisoner"))
            {
                command = "ransom_package";
            }
            else if (ContainsAnyNormalized(normalized, "prisoner exchange", "exchange prisoners", "release prisoners"))
            {
                command = "exchange_prisoners";
            }
            else if (ContainsAnyNormalized(normalized, "reparations", "indemnity", "compensation"))
            {
                command = "demand_reparations_peace";
            }
            else if (ContainsAnyNormalized(normalized, "tribute"))
            {
                command = "offer_tribute_peace";
            }
            else if (ContainsAnyNormalized(normalized, "stop following", "break off the escort", "go your own way", "no need to trail", "trail us any longer"))
            {
                command = "stop_following";
            }
            else if (ContainsAnyNormalized(normalized, "follow on map", "follow in scene", "follow me", "follow us", "follow my party", "follow our party", "escort me", "escort us", "escort my party", "come with me", "walk with me", "stay by my side", "stay close", "walk after me", "stay behind our party", "keep to our route"))
            {
                command = "follow";
            }
            else if (ContainsAnyNormalized(normalized, "go to", "travel to", "ride to"))
            {
                command = "go_to_settlement";
            }
            else if (ContainsAnyNormalized(normalized, "patrol around", "patrol near"))
            {
                command = "patrol_around_settlement";
            }
            else if (ContainsAnyNormalized(normalized, "wait near", "wait outside"))
            {
                command = "wait_near_settlement";
            }
            else if (ContainsAnyNormalized(normalized, "raid village", "raid the village", "burn village") || (ContainsAnyNormalized(normalized, "raid") && ContainsAnyNormalized(normalized, "village")))
            {
                command = "raid_village";
            }
            else if (ContainsAnyNormalized(normalized, "besiege", "lay siege"))
            {
                command = "besiege_settlement";
            }
            else if (ContainsAnyNormalized(normalized, "create party", "raise a party", "make a new party"))
            {
                command = "create_party";
            }
            else if (ContainsAnyNormalized(normalized, "show the way", "guide me"))
            {
                command = "show_the_way";
            }
            else if (ContainsAnyNormalized(normalized, "attack player party", "attack my party"))
            {
                command = "attack_player_party";
            }
            else if (ContainsAnyNormalized(normalized, "attack party", "attack that party"))
            {
                command = "attack_party";
            }
            else if (ContainsAnyNormalized(normalized, "surrender to player", "surrender to me"))
            {
                command = "surrender_to_player";
            }
            else if (ContainsAnyNormalized(normalized, "leave player alone", "leave me alone"))
            {
                command = "leave_player_alone";
            }
            else if (ContainsAnyNormalized(normalized, "kill character", "execute"))
            {
                command = "kill_character";
            }
            else if (ContainsAnyNormalized(normalized, "duel player", "challenge me to a duel"))
            {
                command = "duel_player";
            }
            else if (ContainsAnyNormalized(normalized, "give gold", "give denars"))
            {
                command = "give_gold_to_player";
            }
            else if (ContainsAnyNormalized(normalized, "transfer gold", "transfer denars"))
            {
                command = "transfer_gold";
            }
            else if (ContainsAnyNormalized(normalized, "transfer item", "give item"))
            {
                command = "transfer_item";
            }
            else if (ContainsAnyNormalized(normalized, "transfer workshop", "give workshop"))
            {
                command = "transfer_workshop";
            }
            else if (ContainsAnyNormalized(normalized, "transfer prisoner", "release prisoner"))
            {
                command = "transfer_prisoner";
            }
            else if (ContainsAnyNormalized(normalized, "hire player as mercenary", "hire me as mercenary"))
            {
                command = "hire_player_as_mercenary";
            }
            else if (ContainsAnyNormalized(normalized, "dismiss player mercenary", "dismiss me as mercenary"))
            {
                command = "dismiss_player_mercenary";
            }
            else if (ContainsAnyNormalized(normalized, "offer player vassalage", "make me your vassal"))
            {
                command = "offer_player_vassalage";
            }
            else if (ContainsAnyNormalized(normalized, "dismiss player vassal", "release me from vassalage"))
            {
                command = "dismiss_player_vassal";
            }
            else if (ContainsAnyNormalized(normalized, "join clan"))
            {
                command = "join_clan";
            }
            else if (ContainsAnyNormalized(normalized, "leave clan"))
            {
                command = "leave_clan";
            }
            else if (ContainsAnyNormalized(normalized, "join kingdom", "join my kingdom"))
            {
                command = "join_kingdom";
            }
            else if (ContainsAnyNormalized(normalized, "leave kingdom"))
            {
                command = "leave_kingdom";
            }
            else if (ContainsAnyNormalized(normalized, "hire mercenary clan"))
            {
                command = "hire_mercenary_clan";
            }
            else if (ContainsAnyNormalized(normalized, "peace", "end the war", "settle hostilities", "cease fighting"))
            {
                command = "make_peace";
            }

            TraceResolver(trace, "command", "concept", text, string.IsNullOrWhiteSpace(command) ? "not_inferred" : "inferred", command, string.IsNullOrWhiteSpace(command) ? new List<string>() : new List<string> { command });
            return command;
        }

        private static bool HasDiplomaticPackageTerm(Dictionary<string, object> terms, Dictionary<string, object> record)
        {
            if (terms == null)
            {
                terms = new Dictionary<string, object>();
            }

            return ReadInt(terms, "gold", 0) > 0
                || ReadInt(terms, "GoldAmount", 0) > 0
                || ReadInt(terms, "reparationsGold", 0) > 0
                || ReadInt(terms, "dailyTribute", 0) > 0
                || !string.IsNullOrWhiteSpace(ReadFirstString(terms, "item", "itemId", "asset", "assetClass", "assetName", "prisonerHero", "prisonerHeroStringId", "targetHeroStringId", "treatyKind", "promiseText", "packageText", "targetSettlementStringId"))
                || !string.IsNullOrWhiteSpace(ReadFirstString(record, "targetSettlementStringId", "targetHeroStringId"))
                || ReadDictionaryList(terms, "items").Count > 0
                || ReadDictionaryList(terms, "itemTerms").Count > 0
                || ReadStringList(terms, "settlementIds").Count > 0;
        }

        private static bool HasTradePackageTerm(Dictionary<string, object> terms, Dictionary<string, object> record)
        {
            if (terms == null)
            {
                terms = new Dictionary<string, object>();
            }

            return ReadInt(terms, "gold", 0) > 0
                || ReadInt(terms, "GoldAmount", 0) > 0
                || ReadInt(terms, "shipCount", 0) > 0
                || ReadBool(terms, "allMatchingAssets", false)
                || !string.IsNullOrWhiteSpace(ReadFirstString(terms,
                    "item", "itemId", "asset", "assetClass", "assetName",
                    "prisonerHero", "prisonerHeroStringId",
                    "workshop", "workshopId",
                    "targetSettlementStringId", "packageText"))
                || !string.IsNullOrWhiteSpace(ReadFirstString(record, "targetSettlementStringId", "targetHeroStringId"))
                || ReadDictionaryList(terms, "items").Count > 0
                || ReadDictionaryList(terms, "itemTerms").Count > 0
                || ReadStringList(terms, "settlementIds").Count > 0;
        }

        internal static bool ContainsAnyNormalized(string normalized, params string[] phrases)
        {
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return phrases.Any(x => normalized.Contains(NormalizeLookup(x)));
        }

        private static void TraceResolver(List<Dictionary<string, object>> trace, string label, string entityType, string query, string status, string resolvedId, List<string> candidates)
        {
            if (trace == null)
            {
                return;
            }

            trace.Add(new Dictionary<string, object>
            {
                ["label"] = label ?? string.Empty,
                ["entityType"] = entityType ?? string.Empty,
                ["query"] = query ?? string.Empty,
                ["status"] = status ?? string.Empty,
                ["resolvedId"] = resolvedId ?? string.Empty,
                ["candidates"] = candidates ?? new List<string>()
            });
        }

        private static void LogResolverTrace(string campaignId, string command, string mappedType, Dictionary<string, object> raw, Dictionary<string, object> record, List<Dictionary<string, object>> trace, List<string> errors)
        {
            LogResolverTrace(campaignId, command, mappedType, raw, record, trace, errors, new List<string>());
        }

        private static void LogResolverTrace(string campaignId, string command, string mappedType, Dictionary<string, object> raw, Dictionary<string, object> record, List<Dictionary<string, object>> trace, List<string> errors, List<string> schemaWarnings)
        {
            string correlationId = FirstNonEmpty(ReadFirstString(record, "correlationId"), ReadFirstString(raw, "correlationId", "requestId", "clientRequestId"));
            string actionId = FirstNonEmpty(ReadFirstString(record, "actionId", "serverActionId"), ReadFirstString(raw, "actionId", "id", "serverActionId"));
            bool failed = errors != null && errors.Count > 0;
            OperationsService.LogOperational("resolver.action_normalize", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId ?? string.Empty,
                ["correlationId"] = correlationId,
                ["command"] = command ?? string.Empty,
                ["mappedType"] = mappedType ?? string.Empty,
                ["rawAction"] = raw ?? new Dictionary<string, object>(),
                ["normalizedRecord"] = record ?? new Dictionary<string, object>(),
                ["trace"] = trace ?? new List<Dictionary<string, object>>(),
                ["errors"] = errors ?? new List<string>(),
                ["failureClass"] = failed ? ClassifyActionFailure(1, errors) : "",
                ["schemaWarnings"] = schemaWarnings ?? new List<string>()
            });
            OperationsService.WriteAudit(campaignId, correlationId, "server", "action", failed ? "action.validation_failed" : "action.normalized", ReadFirstString(record, "actorHeroStringId", "targetHeroStringId"), actionId, "", failed ? "failed" : "completed", 0, failed ? "Action failed schema or world validation." : "Action normalized and validated.", new Dictionary<string, object>
            {
                ["command"] = command ?? string.Empty,
                ["mappedType"] = mappedType ?? string.Empty,
                ["rawAction"] = raw ?? new Dictionary<string, object>(),
                ["normalizedRecord"] = record ?? new Dictionary<string, object>(),
                ["trace"] = trace ?? new List<Dictionary<string, object>>(),
                ["errors"] = errors ?? new List<string>(),
                ["failureClass"] = failed ? ClassifyActionFailure(1, errors) : "",
                ["schemaWarnings"] = schemaWarnings ?? new List<string>()
            });
        }
    }
}
