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
        private static void ValidateResolverIndexSchema(Dictionary<string, object> payload, List<string> errors, List<string> warnings, List<Dictionary<string, object>> trace)
        {
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex");
            if (index == null || index.Count == 0)
            {
                warnings.Add("actionResolutionIndex is missing; only exact IDs already present in the action can be trusted.");
                TraceResolver(trace, "schema.actionResolutionIndex", "schema", "", "missing", "", new List<string>());
                return;
            }

            string version = ReadString(index, "version", "");
            if (string.IsNullOrWhiteSpace(version))
            {
                warnings.Add("actionResolutionIndex.version is missing.");
            }

            ValidateResolverCollectionSchema(index, "settlements", "settlementId", "name", errors, warnings, trace, required: true);
            ValidateResolverCollectionSchema(index, "kingdoms", "kingdomId", "name", errors, warnings, trace, required: true);
            ValidateResolverCollectionSchema(index, "clans", "clanId", "name", errors, warnings, trace, required: true);
            ValidateResolverCollectionSchema(index, "heroes", "heroStringId", "name", errors, warnings, trace, required: false);
            ValidateResolverCollectionSchema(index, "parties", "partyId", "name", errors, warnings, trace, required: false);
            ValidateResolverCollectionSchema(index, "armies", "armyId", "name", errors, warnings, trace, required: false);
            ValidateResolverCollectionSchema(index, "workshops", "workshopId", "name", errors, warnings, trace, required: false);

            int aliasCount = ReadDictionaryList(index, "aliases").Count;
            if (aliasCount == 0)
            {
                warnings.Add("actionResolutionIndex.aliases is empty; context references like player, speaker, this city, and current settlement may not resolve.");
            }

            Dictionary<string, object> assets = ReadDictionary(index, "assets");
            if (assets == null || assets.Count == 0)
            {
                warnings.Add("actionResolutionIndex.assets is missing; item, prisoner, and gold package validation will be limited.");
            }

            TraceResolver(trace, "schema.actionResolutionIndex", "schema", version, errors.Count == 0 ? "checked" : "schema_errors", version, new List<string>
            {
                "settlements=" + ReadDictionaryList(index, "settlements").Count,
                "kingdoms=" + ReadDictionaryList(index, "kingdoms").Count,
                "clans=" + ReadDictionaryList(index, "clans").Count,
                "heroes=" + ReadDictionaryList(index, "heroes").Count,
                "parties=" + ReadDictionaryList(index, "parties").Count,
                "armies=" + ReadDictionaryList(index, "armies").Count,
                "workshops=" + ReadDictionaryList(index, "workshops").Count
            });
        }

        private static void ValidateResolverCollectionSchema(Dictionary<string, object> index, string collectionKey, string idKey, string nameKey, List<string> errors, List<string> warnings, List<Dictionary<string, object>> trace, bool required)
        {
            List<Dictionary<string, object>> rows = ReadDictionaryList(index, collectionKey);
            if (rows.Count == 0)
            {
                string message = "actionResolutionIndex." + collectionKey + " is empty.";
                if (required)
                {
                    errors.Add(message);
                }
                else
                {
                    warnings.Add(message);
                }

                TraceResolver(trace, "schema." + collectionKey, "schema", "", "empty", "", new List<string>());
                return;
            }

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missingIds = 0;
            int duplicateIds = 0;
            int missingNames = 0;
            foreach (Dictionary<string, object> row in rows)
            {
                string id = ReadString(row, idKey, "");
                if (string.IsNullOrWhiteSpace(id))
                {
                    missingIds++;
                }
                else if (!ids.Add(id))
                {
                    duplicateIds++;
                }

                if (string.IsNullOrWhiteSpace(ReadString(row, nameKey, "")))
                {
                    missingNames++;
                }
            }

            if (missingIds > 0)
            {
                errors.Add("actionResolutionIndex." + collectionKey + " has " + missingIds + " entries missing " + idKey + ".");
            }

            if (duplicateIds > 0)
            {
                warnings.Add("actionResolutionIndex." + collectionKey + " has " + duplicateIds + " duplicate " + idKey + " entries.");
            }

            if (missingNames > 0)
            {
                warnings.Add("actionResolutionIndex." + collectionKey + " has " + missingNames + " entries missing " + nameKey + ".");
            }
        }

        private static void ValidateActionPayloadSchema(Dictionary<string, object> raw, Dictionary<string, object> terms, string mappedType, Dictionary<string, object> record, List<string> errors, List<string> warnings)
        {
            if (record == null)
            {
                errors.Add("Normalized action record is missing.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ReadString(record, "actionId", "")))
            {
                errors.Add("actionId is required after normalization.");
            }

            if (string.IsNullOrWhiteSpace(ReadString(record, "command", "")))
            {
                errors.Add("command is required after normalization.");
            }

            if (string.IsNullOrWhiteSpace(mappedType))
            {
                errors.Add("type mapping is required after normalization.");
            }

            if (ReadString(record, "reason", "").Length > 2000)
            {
                warnings.Add("Action reason is longer than 2000 characters and may be hard to read in logs.");
            }

            if (ReadString(record, "termsJson", "").Length > 12000)
            {
                errors.Add("termsJson is too large for a compact world action.");
            }

            int maxAttempts = ReadInt(record, "maxAttempts", 3);
            if (maxAttempts < 1 || maxAttempts > 72)
            {
                errors.Add("maxAttempts must be between 1 and 72.");
            }

            int desiredStrength = ReadInt(record, "desiredStrength", 350);
            if (desiredStrength < 0 || desiredStrength > 50000)
            {
                errors.Add("desiredStrength is outside the allowed range 0..50000.");
            }

            int minimumTroops = ReadInt(record, "minimumTroops", 40);
            if (minimumTroops < 0 || minimumTroops > 10000)
            {
                errors.Add("minimumTroops is outside the allowed range 0..10000.");
            }

            double executeAfterDays = ReadDouble(record, "executeAfterDays", 0d);
            if (executeAfterDays < 0d || executeAfterDays > 3650d)
            {
                errors.Add("executeAfterDays must be between 0 and 3650.");
            }

            foreach (string key in new[] { "actorHeroStringId", "targetHeroStringId", "actorKingdomStringId", "targetKingdomStringId", "actorClanStringId", "targetClanStringId", "targetSettlementStringId" })
            {
                string value = ReadString(record, key, "");
                if (value.Length > 160)
                {
                    errors.Add(key + " is too long to be a Bannerlord StringId.");
                }
            }

            if (terms != null)
            {
                foreach (string key in terms.Keys.ToList())
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        errors.Add("terms contains an empty key.");
                    }
                    else if (key.Length > 80)
                    {
                        warnings.Add("terms key is unusually long: " + key);
                    }
                }
            }
        }

        internal static Dictionary<string, object> NormalizeActionCommand(Dictionary<string, object> raw, string campaignId, out List<string> errors, Dictionary<string, object> payload = null, string resolutionText = "")
        {
            raw = FlattenActionFields(raw ?? new Dictionary<string, object>());
            errors = new List<string>();
            string correlationId = FirstNonEmpty(ReadFirstString(raw, "correlationId", "requestId", "clientRequestId"), ReadFirstString(payload, "correlationId", "requestId", "clientRequestId"));
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                correlationId = "action-" + Guid.NewGuid().ToString("N");
            }

            raw["correlationId"] = correlationId;
            List<string> schemaWarnings = new List<string>();
            Dictionary<string, object> terms = LlmProviderService.ToCaseInsensitiveDictionary(ReadDictionary(raw, "terms") ?? new Dictionary<string, object>());
            LlmProviderService.NormalizeChatAiTerms(raw, terms);
            WorldSimulationService.CopyCampaignCommandTerms(raw, terms);
            List<Dictionary<string, object>> resolverTrace = new List<Dictionary<string, object>>();
            ValidateResolverIndexSchema(payload, errors, schemaWarnings, resolverTrace);
            string command = CanonicalCommand(ReadFirstString(raw, "command", "action", "commandType", "intent", "type"));
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(MapCommandToActionType(command)))
            {
                command = CanonicalCommand(InferCommandFromNaturalAction(raw, terms, payload, resolutionText, resolverTrace));
            }

            string mappedType = MapCommandToActionType(command);
            if (string.IsNullOrWhiteSpace(mappedType))
            {
                errors.Add("Unsupported action command: " + (string.IsNullOrWhiteSpace(command) ? "<missing>" : command));
                LogResolverTrace(campaignId, command, mappedType, raw, null, resolverTrace, errors, schemaWarnings);
                return null;
            }

            string actionId = ReadFirstString(raw, "actionId", "id", "serverActionId");
            if (string.IsNullOrWhiteSpace(actionId))
            {
                actionId = Guid.NewGuid().ToString("N");
            }

            if (string.Equals(command, "trade_package", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
                ApplyPaidAssetPackageTerms(raw, terms, payload, hero, resolutionText);
            }

            if (string.Equals(command, "ransom_package", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
                ApplyRansomPackageTerms(raw, terms, payload, hero, resolutionText);
            }

            if (string.Equals(command, "diplomatic_package", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
                ApplyCompoundDiplomaticPackageTerms(raw, terms, payload, hero, resolutionText);
            }

            if (string.Equals(command, "transfer_gold", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "transfer_item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, "give_gold_to_player", StringComparison.OrdinalIgnoreCase))
            {
                bool needsDirection = string.Equals(command, "give_gold_to_player", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(FirstNonEmpty(ReadFirstString(raw, "FromHero", "fromHero", "actorHeroStringId", "actorHeroId"), ReadFirstString(terms, "FromHero", "fromHero", "fromHeroStringId", "actorHeroStringId", "actorHeroId")))
                    || string.IsNullOrWhiteSpace(FirstNonEmpty(ReadFirstString(raw, "ToHero", "toHero", "targetHeroStringId", "targetHeroId", "TargetHero"), ReadFirstString(terms, "ToHero", "toHero", "toHeroStringId", "targetHeroStringId", "targetHeroId", "TargetHero")));
                if (needsDirection)
                {
                    Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
                    ApplyTransferDirectionDefaults(raw, terms, payload, hero, resolutionText, command);
                }
            }

            ApplyTemporaryPartyGuestActorBinding(raw, terms, payload,
                ReadDictionary(payload, "hero") ?? new Dictionary<string, object>(), command);
            CharactersService.BindAndValidateResidentAgreement(raw, terms, payload, resolutionText, command, errors);
            DiplomacyService.BindAndValidateClanAccord(raw, terms, payload, resolutionText, command, errors);
            ResolveActionReferences(raw, terms, payload, resolutionText, command, errors, resolverTrace);
            WorldSimulationService.BindDialogueMarriageParticipants(raw, terms, payload, command, errors);
            GovernmentService.ValidateGovernmentPaymentOwnership(raw, terms, command, errors);
            string termsJson = terms == null ? ReadString(raw, "termsJson", "") : Json.Serialize(terms);
            Dictionary<string, object> record = new Dictionary<string, object>
            {
                ["actionId"] = actionId,
                ["serverActionId"] = actionId,
                ["correlationId"] = correlationId,
                ["campaignId"] = campaignId,
                ["command"] = command,
                ["type"] = mappedType,
                ["source"] = ReadString(raw, "source", "server_llm_or_manual"),
                ["actorHeroStringId"] = ReadFirstString(raw, "actorHeroStringId", "actorHeroId", "leaderHeroId", "heroId", "FromHero"),
                ["actorKingdomStringId"] = ReadFirstString(raw, "actorKingdomStringId", "actorKingdomId", "kingdomId"),
                ["targetHeroStringId"] = ReadFirstString(raw, "targetHeroStringId", "targetHeroId", "TargetHero", "ToHero", "PrisonerHero"),
                ["actorClanStringId"] = ReadFirstString(raw, "actorClanStringId", "actorClanId", "claimantClanId"),
                ["targetClanStringId"] = ReadFirstString(raw, "targetClanStringId", "targetClanId", "TargetClan"),
                ["supporterClanIdsCsv"] = ReadSupporterClanIds(raw, terms),
                ["targetKingdomStringId"] = ReadFirstString(raw, "targetKingdomStringId", "targetKingdomId", "TargetKingdom"),
                ["targetSettlementStringId"] = ReadFirstString(raw, "targetSettlementStringId", "targetSettlementId", "settlementId", "TargetSettlement"),
                ["reason"] = ReadString(raw, "reason", "AI world action from Bannerlord Reign server."),
                ["termsJson"] = termsJson,
                ["minimumTroops"] = ReadInt(raw, "minimumTroops", 40),
                ["desiredStrength"] = ReadInt(raw, "desiredStrength", 350),
                ["maxAttempts"] = ReadInt(raw, "maxAttempts", mappedType == "StrategyCaptureSettlement" ? 24 : 3),
                ["executeAfterDays"] = ReadDouble(raw, "executeAfterDays", 0d),
                ["requiresAcceptance"] = ReadBool(raw, "requiresAcceptance", false)
                ,["authorizationMode"] = ReadFirstString(raw, "authorizationMode")
                ,["negotiationId"] = ReadFirstString(raw, "negotiationId")
                ,["termsHash"] = ReadFirstString(raw, "termsHash")
                ,["executionPhase"] = ReadString(raw, "executionPhase", "idle")
                ,["executionSnapshotJson"] = ReadString(raw, "executionSnapshotJson", "{}")
                ,["acceptedByHeroStringId"] = ReadFirstString(raw, "acceptedByHeroStringId")
            };

            if (string.Equals(command, "issue_campaign_order", StringComparison.OrdinalIgnoreCase)
                && !ReadBool(raw, "requiresAcceptance", false)
                && (ReadString(raw, "source", "").IndexOf("dialogue", StringComparison.OrdinalIgnoreCase) >= 0
                    || ReadString(raw, "source", "").IndexOf("correspondence", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                record["authorizationMode"] = "accepted_recommendation";
                record["acceptedByHeroStringId"] = ReadString(record, "actorHeroStringId", "");
            }

            if (mappedType.StartsWith("Diplomacy", StringComparison.OrdinalIgnoreCase))
            {
                Require(record, "actorKingdomStringId", errors);
                Require(record, "targetKingdomStringId", errors);
                if ((mappedType == "DiplomacyDemandSettlementPeace" || mappedType == "DiplomacyDemandSurrenderPeace" || mappedType == "DiplomacyReturnOccupiedSettlement") && string.IsNullOrWhiteSpace(ReadString(record, "targetSettlementStringId", "")))
                {
                    string firstSettlement = FirstStringFromTerms(terms, "settlementIds");
                    if (!string.IsNullOrWhiteSpace(firstSettlement))
                    {
                        record["targetSettlementStringId"] = firstSettlement;
                    }
                }

                if (mappedType == "DiplomacyDemandSettlementPeace" && string.IsNullOrWhiteSpace(ReadString(record, "targetSettlementStringId", "")))
                {
                    errors.Add("targetSettlementStringId or terms.settlementIds is required for settlement surrender.");
                }
                if (mappedType == "DiplomacyDemandSurrenderPeace" && string.IsNullOrWhiteSpace(ReadString(record, "targetSettlementStringId", "")) && ReadStringList(terms, "settlementIds").Count == 0 && !ReadBool(terms, "allTargetFortifications", false))
                {
                    terms["allTargetFortifications"] = true;
                    record["termsJson"] = Json.Serialize(terms);
                }
                if (mappedType == "DiplomacyReturnOccupiedSettlement" && string.IsNullOrWhiteSpace(ReadString(record, "targetSettlementStringId", "")))
                {
                    errors.Add("targetSettlementStringId is required for return occupied settlement.");
                }
                if (mappedType == "DiplomacyPayToJoinWar" && string.IsNullOrWhiteSpace(FirstNonEmpty(ReadString(terms, "enemyKingdomId", ""), ReadString(terms, "thirdKingdomId", ""))))
                {
                    errors.Add("terms.enemyKingdomId is required for pay to join war.");
                }
                if (mappedType == "DiplomacyPackage" && !HasDiplomaticPackageTerm(terms, record))
                {
                    errors.Add("Diplomatic package requires at least one resolved term: gold, item, prisoner, settlement, treaty, or promise.");
                }
            }
            else if (mappedType.StartsWith("Politics", StringComparison.OrdinalIgnoreCase))
            {
                Require(record, "actorKingdomStringId", errors);
                if (mappedType == "PoliticsResolveCivilWar")
                {
                    Require(record, "targetKingdomStringId", errors);
                    if (!string.Equals(ReadString(record, "authorizationMode", ""), "negotiated", StringComparison.OrdinalIgnoreCase)) errors.Add("resolve_civil_war requires authorizationMode=negotiated.");
                    Require(record, "negotiationId", errors);
                    Require(record, "termsHash", errors);
                    string politicalResult = ReadString(terms, "politicalResult", "");
                    if (politicalResult == "recognized_independence")
                    {
                        Require(terms, "movementId", errors);
                        Require(terms, "rebelKingdomId", errors);
                        Require(terms, "parentKingdomId", errors);
                        if (!string.Equals(ReadString(record,
                                "actorKingdomStringId", ""),
                                ReadString(terms, "rebelKingdomId", ""),
                                StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(ReadString(record,
                                "targetKingdomStringId", ""),
                                ReadString(terms, "parentKingdomId", ""),
                                StringComparison.OrdinalIgnoreCase))
                            errors.Add("recognized_independence must name the rebel realm as actor and its exact parent realm as target.");
                    }
                    if (politicalResult != "recognized_independence")
                        errors.Add("resolve_civil_war permits only safeguarded recognized_independence; ordinary civil-war peace is disabled.");
                }
                else if (mappedType == "PoliticsConsentGovernmentReduction")
                {
                    Require(record, "actorHeroStringId", errors);
                    Require(record, "targetHeroStringId", errors);
                    if (!ReadBool(terms, "consentConfirmed", false))
                        errors.Add("terms.consentConfirmed must be true for government-reduction consent.");
                }
                else
                {
                    Require(record, "actorClanStringId", errors);
                    if (mappedType == "PoliticsRecruitLordToRebellion" || mappedType == "PoliticsJoinRebellion")
                    {
                        Require(record, "actorHeroStringId", errors);
                        Require(record, "targetHeroStringId", errors);
                    }
                    else if (mappedType == "PoliticsStartRulingClanRebellion")
                    {
                        Require(record, "actorHeroStringId", errors);
                        Require(record, "targetHeroStringId", errors);
                    }
                    else if (mappedType == "PoliticsSurrenderRebellion")
                    {
                        Require(record, "actorHeroStringId", errors);
                    }
                }
            }
            else if (mappedType.StartsWith("Strategy", StringComparison.OrdinalIgnoreCase))
            {
                Require(record, "actorHeroStringId", errors);
                Require(record, "targetSettlementStringId", errors);
            }
            else if (mappedType.StartsWith("Regular", StringComparison.OrdinalIgnoreCase))
            {
                ValidateRegularRecordRequirements(command, record, terms, errors);
            }

            BindDialogueSettlementAuthorization(record, terms, payload, command, mappedType,
                correlationId, errors);
            DialogueService.BindPaymentRefund(record, terms, payload, command, errors);
            WorldSimulationService.BindDialogueActionAuthority(record, terms, payload, command, mappedType,
                correlationId, errors, raw);

            if (string.IsNullOrWhiteSpace(ReadString(record, "reason", "")))
            {
                errors.Add("Action reason is required.");
            }

            ValidateActionPayloadSchema(raw, terms, mappedType, record, errors, schemaWarnings);
            LogResolverTrace(campaignId, command, mappedType, raw, record, resolverTrace, errors, schemaWarnings);
            return errors.Count == 0 ? record : null;
        }

        private static void BindDialogueSettlementAuthorization(
            Dictionary<string, object> record,
            Dictionary<string, object> terms,
            Dictionary<string, object> payload,
            string command,
            string mappedType,
            string correlationId,
            List<string> errors)
        {
            string source = ReadString(record, "source", "");
            if (!IsDialogueActionSource(source)
                || !IsSettlementTransferAction(mappedType, record, terms))
            {
                return;
            }

            Dictionary<string, object> hero = ReadDictionary(payload, "hero")
                ?? new Dictionary<string, object>();
            string acceptedByHeroId = FirstNonEmpty(
                ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId"),
                ReadString(hero, "heroStringId", ""));
            string playerHeroId = ReadString(payload, "playerHeroStringId", "");
            Dictionary<string, object> index = ReadDictionary(payload, "actionResolutionIndex")
                ?? new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(acceptedByHeroId)
                || string.IsNullOrWhiteSpace(playerHeroId))
            {
                errors.Add("Dialogue settlement transfer requires exact speaker and player hero IDs.");
                return;
            }
            if (index.Count == 0)
            {
                errors.Add("Dialogue settlement transfer requires the live action resolution index.");
                return;
            }

            if (string.Equals(mappedType, "RegularTradePackage", StringComparison.OrdinalIgnoreCase))
            {
                BindDialogueSettlementTradeAuthority(record, terms, index, acceptedByHeroId,
                    playerHeroId, errors);
            }
            else
            {
                BindDialogueKingdomSettlementAuthority(record, terms, index, mappedType,
                    acceptedByHeroId, playerHeroId, errors);
            }

            if (errors.Count > 0)
            {
                return;
            }

            record["acceptedByHeroStringId"] = acceptedByHeroId;
            record["authorizationMode"] = "dialogue_acceptance";
            record["negotiationId"] = correlationId;
            record["termsJson"] = Json.Serialize(terms);
            record["termsHash"] = ComputeActionTermsHash(command, terms);
        }

        private static void BindDialogueSettlementTradeAuthority(
            Dictionary<string, object> record,
            Dictionary<string, object> terms,
            Dictionary<string, object> index,
            string acceptedByHeroId,
            string playerHeroId,
            List<string> errors)
        {
            List<string> settlementIds = ReadStringList(terms, "settlementIds");
            string targetSettlementId = ReadString(record, "targetSettlementStringId", "");
            if (!string.IsNullOrWhiteSpace(targetSettlementId)
                && !settlementIds.Contains(targetSettlementId, StringComparer.OrdinalIgnoreCase))
            {
                settlementIds.Insert(0, targetSettlementId);
            }
            settlementIds = settlementIds.Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (settlementIds.Count == 0)
            {
                errors.Add("Dialogue settlement trade requires at least one resolved town or castle.");
                return;
            }

            string recipientHeroId = FirstNonEmpty(
                ReadFirstString(terms, "settlementToHeroStringId", "toHeroStringId"),
                ReadString(record, "targetHeroStringId", ""));
            string ownerHeroId;
            if (string.Equals(recipientHeroId, playerHeroId, StringComparison.OrdinalIgnoreCase))
            {
                ownerHeroId = acceptedByHeroId;
            }
            else if (string.Equals(recipientHeroId, acceptedByHeroId, StringComparison.OrdinalIgnoreCase))
            {
                ownerHeroId = playerHeroId;
            }
            else
            {
                errors.Add("Dialogue settlement trade recipient must be the player or the agreeing NPC.");
                return;
            }

            string ownerClanId = "";
            foreach (string settlementId in settlementIds)
            {
                Dictionary<string, object> settlement = FindIndexedRow(index, "settlements",
                    "settlementId", settlementId);
                string currentOwnerClanId = ReadString(settlement, "ownerClanId", "");
                Dictionary<string, object> ownerClan = FindIndexedRow(index, "clans", "clanId",
                    currentOwnerClanId);
                string currentOwnerLeaderId = ReadString(ownerClan, "leaderHeroStringId", "");
                if (settlement == null || string.IsNullOrWhiteSpace(currentOwnerClanId)
                    || string.IsNullOrWhiteSpace(currentOwnerLeaderId))
                {
                    errors.Add("Settlement authority could not be resolved for " + settlementId + ".");
                    continue;
                }
                if (!string.Equals(currentOwnerLeaderId, ownerHeroId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("Only the current owner-clan leader may transfer settlement "
                        + settlementId + " in a voluntary trade.");
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(ownerClanId)
                    && !string.Equals(ownerClanId, currentOwnerClanId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("A single settlement trade cannot combine fiefs owned by different clans.");
                    continue;
                }
                ownerClanId = currentOwnerClanId;
            }
            if (errors.Count > 0)
            {
                return;
            }

            record["actorHeroStringId"] = ownerHeroId;
            record["actorClanStringId"] = ownerClanId;
            terms["settlementFromHeroStringId"] = ownerHeroId;
            terms["settlementFromClanStringId"] = ownerClanId;
            terms["settlementToHeroStringId"] = recipientHeroId;
            terms["settlementAuthorizingHeroStringId"] = ownerHeroId;
            terms["settlementAuthorityKind"] = "owner_clan_leader";
        }

        private static void BindDialogueKingdomSettlementAuthority(
            Dictionary<string, object> record,
            Dictionary<string, object> terms,
            Dictionary<string, object> index,
            string mappedType,
            string acceptedByHeroId,
            string playerHeroId,
            List<string> errors)
        {
            string actorKingdomId = ReadString(record, "actorKingdomStringId", "");
            string targetKingdomId = ReadString(record, "targetKingdomStringId", "");
            Dictionary<string, object> actorKingdom = FindIndexedRow(index, "kingdoms",
                "kingdomId", actorKingdomId);
            Dictionary<string, object> targetKingdom = FindIndexedRow(index, "kingdoms",
                "kingdomId", targetKingdomId);
            string actorLeaderId = ReadString(actorKingdom, "leaderHeroStringId", "");
            string targetLeaderId = ReadString(targetKingdom, "leaderHeroStringId", "");
            bool exactRulerPair = (string.Equals(actorLeaderId, playerHeroId,
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(targetLeaderId, acceptedByHeroId,
                        StringComparison.OrdinalIgnoreCase))
                || (string.Equals(actorLeaderId, acceptedByHeroId,
                    StringComparison.OrdinalIgnoreCase)
                    && string.Equals(targetLeaderId, playerHeroId,
                        StringComparison.OrdinalIgnoreCase));
            if (!exactRulerPair)
            {
                errors.Add("Kingdom settlement transfers require the player and agreeing NPC to be the current rulers of the two realms.");
                return;
            }

            string sourceKingdomId;
            if (string.Equals(mappedType, "DiplomacyReturnOccupiedSettlement",
                StringComparison.OrdinalIgnoreCase))
            {
                sourceKingdomId = actorKingdomId;
            }
            else if (string.Equals(mappedType, "DiplomacyPackage",
                StringComparison.OrdinalIgnoreCase))
            {
                sourceKingdomId = ReadString(terms, "settlementFromKingdomId",
                    targetKingdomId);
            }
            else
            {
                sourceKingdomId = targetKingdomId;
            }
            Dictionary<string, object> sourceKingdom = FindIndexedRow(index, "kingdoms",
                "kingdomId", sourceKingdomId);
            string sourceLeaderId = ReadString(sourceKingdom, "leaderHeroStringId", "");
            if (sourceKingdom == null
                || (!string.Equals(sourceLeaderId, acceptedByHeroId,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(sourceLeaderId, playerHeroId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("The settlement source kingdom has no participating ruler authority.");
                return;
            }

            terms["settlementFromKingdomId"] = sourceKingdomId;
            terms["settlementAuthorizingHeroStringId"] = sourceLeaderId;
            terms["settlementAuthorityKind"] = "kingdom_ruler";
        }

        private static bool IsSettlementTransferAction(string mappedType,
            Dictionary<string, object> record, Dictionary<string, object> terms)
        {
            if (string.Equals(mappedType, "DiplomacyDemandSettlementPeace",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(mappedType, "DiplomacyDemandSurrenderPeace",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(mappedType, "DiplomacyReturnOccupiedSettlement",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.Equals(mappedType, "RegularTradePackage",
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mappedType, "DiplomacyPackage",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(ReadString(record,
                    "targetSettlementStringId", ""))
                || ReadStringList(terms, "settlementIds").Count > 0
                || !string.IsNullOrWhiteSpace(ReadString(terms,
                    "targetSettlementStringId", ""));
        }

        internal static bool IsDialogueActionSource(string source)
        {
            source = source ?? "";
            return source.IndexOf("dialogue", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(source, "hidden_action_planner",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "test_lab_action_gate",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> FindIndexedRow(
            Dictionary<string, object> index, string collectionKey, string idKey, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return ReadDictionaryList(index, collectionKey).FirstOrDefault(row =>
                string.Equals(ReadString(row, idKey, ""), id,
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static string ComputeActionTermsHash(string command,
            Dictionary<string, object> terms)
        {
            Dictionary<string, object> hashTerms = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> pair in terms
                ?? new Dictionary<string, object>())
            {
                if (!string.Equals(pair.Key, "authorizationMode",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, "negotiationId",
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, "termsHash",
                        StringComparison.OrdinalIgnoreCase))
                {
                    hashTerms[pair.Key] = pair.Value;
                }
            }
            string input = (command ?? "") + "|" + CanonicalActionTerms(hashTerms);
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(input))
                    .Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        internal static string CanonicalActionTerms(object value)
        {
            Dictionary<string, object> dictionary = value as Dictionary<string, object>;
            if (dictionary != null)
            {
                return "{" + string.Join(",", dictionary.OrderBy(x => x.Key,
                    StringComparer.Ordinal).Select(x => QuoteCanonicalJsonString(x.Key) + ":"
                    + CanonicalActionTerms(x.Value))) + "}";
            }
            IDictionary genericDictionary = value as IDictionary;
            if (genericDictionary != null)
            {
                List<KeyValuePair<string, object>> pairs = new List<KeyValuePair<string, object>>();
                foreach (DictionaryEntry entry in genericDictionary)
                {
                    pairs.Add(new KeyValuePair<string, object>(Convert.ToString(entry.Key,
                        CultureInfo.InvariantCulture), entry.Value));
                }
                return "{" + string.Join(",", pairs.OrderBy(x => x.Key,
                    StringComparer.Ordinal).Select(x => QuoteCanonicalJsonString(x.Key) + ":"
                    + CanonicalActionTerms(x.Value))) + "}";
            }
            if (value is IEnumerable enumerable && !(value is string))
            {
                List<string> items = new List<string>();
                foreach (object item in enumerable)
                {
                    items.Add(CanonicalActionTerms(item));
                }
                return "[" + string.Join(",", items) + "]";
            }
            if (value is string stringValue)
            {
                return QuoteCanonicalJsonString(stringValue);
            }
            return value == null ? "null" : Json.Serialize(value);
        }

        private static string QuoteCanonicalJsonString(string value)
        {
            StringBuilder result = new StringBuilder((value?.Length ?? 0) + 2);
            result.Append('"');
            foreach (char ch in value ?? "")
            {
                switch (ch)
                {
                    case '"': result.Append("\\\""); break;
                    case '\\': result.Append("\\\\"); break;
                    case '\b': result.Append("\\b"); break;
                    case '\f': result.Append("\\f"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\r': result.Append("\\r"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (ch < ' ')
                        {
                            result.Append("\\u");
                            result.Append(((int)ch).ToString("x4",
                                CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            result.Append(ch);
                        }
                        break;
                }
            }
            result.Append('"');
            return result.ToString();
        }

        private static void ValidateRegularRecordRequirements(string command, Dictionary<string, object> record, Dictionary<string, object> terms, List<string> errors)
        {
            switch (CanonicalCommand(command))
            {
                case "create_clan_accord":
                case "cancel_clan_accord":
                    Require(record, "actorHeroStringId", errors); Require(record, "targetHeroStringId", errors);
                    if (!ReadBool(terms, "playerConfirmed", false)) errors.Add("Clan Accords require explicit player acceptance.");
                    if (CanonicalCommand(command) == "create_clan_accord")
                    {
                        if (!ReadBool(terms, "consentConfirmed", false)) errors.Add("Clan Accords require explicit NPC acceptance.");
                        if (!Enum.TryParse(ReadString(terms, "accordType", ""), true, out Reign.Core.Contracts.ClanAccords.ClanAccordType accordType)
                            || !Reign.Core.Contracts.ClanAccords.ClanAccordLedger.IsSupportedType(accordType)) errors.Add("Unsupported clan accord type.");
                    }
                    else if (!DiplomacyService.ValidClanAccordId(ReadString(terms, "accordId", ""))) errors.Add("An active accordId is required.");
                    return;
                case "recruit_encountered_resident":
                    Require(record, "actorHeroStringId", errors);
                    if (!ReadBool(terms, "consentConfirmed", false) || ReadString(terms, "agreementKind", "") != "permanent"
                        || !terms.ContainsKey("agreedGold") || ReadInt(terms, "agreedGold", -1) < 0)
                        errors.Add("Resident recruitment requires permanent agreement, verified consent and exact nonnegative agreedGold.");
                    return;
                case "release_resident_from_duty":
                    Require(record, "actorHeroStringId", errors); Require(record, "targetHeroStringId", errors);
                    if (!ReadBool(terms, "releaseConfirmed", false)) errors.Add("An explicit authorized duty release is required.");
                    return;
                case "issue_campaign_order":
                    Require(record, "actorHeroStringId", errors);
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "objective", ""))
                        && ReadDictionaryList(terms, "steps").Count == 0)
                        errors.Add("terms.steps or terms.objective is required for issue_campaign_order.");
                    return;
                case "revise_campaign_order":
                case "cancel_campaign_order":
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "orderId", "")))
                        errors.Add("terms.orderId is required for " + command + ".");
                    return;
                case "respond_to_order_report":
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "orderId", "")))
                        errors.Add("terms.orderId is required for respond_to_order_report.");
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "response", "")))
                        errors.Add("terms.response is required for respond_to_order_report.");
                    return;
                case "accept_temporary_party_guest":
                case "renew_temporary_party_guest":
                    Require(record, "actorHeroStringId", errors);
                    if (!ReadBool(terms, "consentConfirmed", false))
                        errors.Add("terms.consentConfirmed must be true for a temporary guest agreement or renewal.");
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "purpose", "")))
                        errors.Add("terms.purpose is required for a temporary guest agreement.");
                    NormalizeTemporaryGuestTermKind(terms, errors);
                    DialogueService.ValidateGuestServiceTerms(terms, errors);
                    return;
                case "end_temporary_party_guest":
                    Require(record, "actorHeroStringId", errors);
                    return;
                case "acknowledge_own_faction_combat_risk":
                    Require(record, "actorHeroStringId", errors);
                    if (!ReadBool(terms, "consentConfirmed", false))
                        errors.Add("terms.consentConfirmed must be true to accept own-faction combat banishment risk.");
                    return;
                case "trade_package":
                    if (!HasTradePackageTerm(terms, record))
                    {
                        errors.Add("Trade package requires at least one resolved value term: gold, item, prisoner, settlement, workshop, ship, or package text with asset details.");
                    }
                    return;
                case "follow":
                case "attack_party":
                    Require(record, "actorHeroStringId", errors);
                    if (string.IsNullOrWhiteSpace(ReadString(record, "targetHeroStringId", "")) && string.IsNullOrWhiteSpace(ReadString(terms, "targetPartyId", "")))
                    {
                        errors.Add("TargetHero or terms.targetPartyId is required.");
                    }
                    return;
                case "go_to_settlement":
                case "patrol_around_settlement":
                case "wait_near_settlement":
                case "raid_village":
                case "besiege_settlement":
                    Require(record, "actorHeroStringId", errors);
                    Require(record, "targetSettlementStringId", errors);
                    return;
                case "stop_following":
                case "attack_player_party":
                case "surrender_to_player":
                case "leave_player_alone":
                case "show_the_way":
                case "duel_player":
                case "create_party":
                    Require(record, "actorHeroStringId", errors);
                    return;
                case "prepare_arrest":
                    Require(record, "actorHeroStringId", errors);
                    Require(record, "targetHeroStringId", errors);
                    if (string.IsNullOrWhiteSpace(ReadFirstString(terms, "accusation")))
                        errors.Add("terms.accusation is required for prepare_arrest.");
                    return;
                case "confirm_arrest":
                case "release_arrested_character":
                case "rescind_arrest_accusation":
                case "player_attack_party":
                    Require(record, "actorHeroStringId", errors);
                    Require(record, "targetHeroStringId", errors);
                    if (string.IsNullOrWhiteSpace(ReadFirstString(terms, "caseId")))
                        errors.Add("terms.caseId is required for " + command + ".");
                    return;
                case "kill_character":
                    Require(record, "actorHeroStringId", errors);
                    Require(record, "targetHeroStringId", errors);
                    return;
                case "give_gold_to_player":
                    Require(record, "actorHeroStringId", errors);
                    RequirePositiveTerm(terms, "gold", "GoldAmount", errors);
                    return;
                case "transfer_gold":
                    RequirePositiveTerm(terms, "gold", "GoldAmount", errors);
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "fromHeroStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorHeroStringId", "")))
                    {
                        errors.Add("FromHero or actorHeroStringId is required for transfer_gold.");
                    }
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "toHeroStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "targetHeroStringId", "")))
                    {
                        errors.Add("ToHero or targetHeroStringId is required for transfer_gold.");
                    }
                    return;
                case "transfer_item":
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "fromHeroStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorHeroStringId", "")))
                    {
                        errors.Add("FromHero or actorHeroStringId is required for transfer_item.");
                    }
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "toHeroStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "targetHeroStringId", "")))
                    {
                        errors.Add("ToHero or targetHeroStringId is required for transfer_item.");
                    }
                    if (string.IsNullOrWhiteSpace(ReadFirstString(terms, "itemId", "item")))
                    {
                        errors.Add("Item is required for transfer_item.");
                    }
                    RequirePositiveTerm(terms, "amount", "Amount", errors);
                    return;
                case "transfer_workshop":
                    if (string.IsNullOrWhiteSpace(ReadFirstString(terms, "workshopId", "workshop")))
                    {
                        errors.Add("Workshop is required for transfer_workshop.");
                    }
                    if (string.IsNullOrWhiteSpace(ReadString(terms, "toHeroStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "targetHeroStringId", "")))
                    {
                        errors.Add("ToHero or TargetHero is required for transfer_workshop.");
                    }
                    return;
                case "transfer_prisoner":
                    if (string.IsNullOrWhiteSpace(ReadFirstString(terms, "prisonerHeroStringId", "prisonerHero")) && string.IsNullOrWhiteSpace(ReadString(record, "targetHeroStringId", "")))
                    {
                        errors.Add("PrisonerHero is required for transfer_prisoner.");
                    }
                    return;
                case "hire_player_as_mercenary":
                case "offer_player_vassalage":
                    if (string.IsNullOrWhiteSpace(ReadString(record, "targetKingdomStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorKingdomStringId", "")))
                    {
                        errors.Add("targetKingdomStringId or actorKingdomStringId is required.");
                    }
                    return;
                case "join_kingdom":
                    if (string.IsNullOrWhiteSpace(ReadString(record, "actorClanStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorHeroStringId", "")))
                    {
                        errors.Add("actorClanStringId or actorHeroStringId is required.");
                    }
                    Require(record, "targetKingdomStringId", errors);
                    return;
                case "leave_kingdom":
                    if (string.IsNullOrWhiteSpace(ReadString(record, "actorClanStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorHeroStringId", "")))
                    {
                        errors.Add("actorClanStringId or actorHeroStringId is required.");
                    }
                    return;
                case "hire_mercenary_clan":
                    if (string.IsNullOrWhiteSpace(ReadString(record, "targetClanStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorClanStringId", "")))
                    {
                        errors.Add("targetClanStringId or actorClanStringId is required.");
                    }
                    if (string.IsNullOrWhiteSpace(ReadString(record, "targetKingdomStringId", "")) && string.IsNullOrWhiteSpace(ReadString(record, "actorKingdomStringId", "")))
                    {
                        errors.Add("targetKingdomStringId or actorKingdomStringId is required.");
                    }
                    return;
                case "dismiss_player_mercenary":
                case "dismiss_player_vassal":
                case "join_clan":
                case "leave_clan":
                    return;
            }
        }

        private static void NormalizeTemporaryGuestTermKind(
            Dictionary<string, object> terms, List<string> errors)
        {
            string raw = ReadString(terms, "termKind", string.Empty)
                .Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            double duration = ReadDouble(terms, "durationDays", 0d);
            if (raw == "fixed" || raw == "fixed_term")
            {
                terms["termKind"] = "fixed";
                if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 1d || duration > DialogueService.MaximumGuestTermDays)
                    errors.Add("A fixed temporary guest term must be 1 through 126 campaign days, preserving fractional seasons.");
                return;
            }
            if (raw == "open_ended" || raw == "openended" || raw == "open")
            {
                terms["termKind"] = "open_ended";
                terms["reviewIntervalDays"] = 30;
                terms.Remove("durationDays");
                return;
            }

            // The dialogue model can confuse the noble's social role (for example,
            // "independent ally") with the schedule enum. A bounded explicit day
            // count is authoritative evidence of a fixed term, so canonicalize that
            // narrow mistake without weakening the consent gate.
            bool socialRoleLabel = raw == "independent_ally" || raw == "ally" ||
                raw == "companion" || raw == "retainer" || raw == "partner" || raw == "guest";
            if (socialRoleLabel && duration >= 1d && duration <= DialogueService.MaximumGuestTermDays)
            {
                terms["termKind"] = "fixed";
                return;
            }
            errors.Add("terms.termKind must be fixed or open_ended for a temporary guest agreement.");
        }

        private static void RequirePositiveTerm(Dictionary<string, object> terms, string primaryKey, string aliasKey, List<string> errors)
        {
            int value = Math.Max(ReadInt(terms, primaryKey, 0), ReadInt(terms, aliasKey, 0));
            if (value <= 0)
            {
                errors.Add(primaryKey + " must be positive.");
            }
        }
    }
}
