using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        internal static List<Dictionary<string, object>> ApplyIdentityIntroductionWrites(string campaignId,
            Dictionary<string, object> payload, Dictionary<string, object> parsed, string encounterId,
            string speakerId, string playerId, string playerText, string acceptedReply,
            IEnumerable<string> hearingAudience, ReignDbConnection connection)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            if (parsed == null) return results;
            List<Dictionary<string, object>> writes = ReadDictionaryList(parsed, "identityIntroductions").Concat(ReadDictionaryList(parsed, "identity_introductions")).Take(4).ToList();
            if (writes.Count == 0) return results;
            HashSet<string> present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ReadStringList(payload, "activeHeroIds")) present.Add(id);
            foreach (Dictionary<string, object> attendee in ReadDictionaryList(payload, "attendees")) present.Add(CampaignFileService.CharacterIdFrom(attendee));
            present.Add(ReadFirstString(payload, "speakerHeroStringId", "heroStringId"));
            present.Add(ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId"));
            present.RemoveWhere(string.IsNullOrWhiteSpace);
            var heard = new HashSet<string>(hearingAudience ?? present, StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> write in writes)
            {
                string observer = ReadFirstString(write, "observerHeroStringId", "observerId");
                string subject = ReadFirstString(write, "subjectHeroStringId", "subjectId");
                string introducer = ReadFirstString(write, "introducerHeroStringId", "introducerId");
                string name = ReadFirstString(write, "claimedName", "name");
                string quote = ReadFirstString(write, "supportingQuote", "evidenceQuote");
                string spoken = introducer == speakerId ? acceptedReply : introducer == playerId ? playerText : "";
                bool audibleQuote = ReadDictionaryList(DialogueService.ProjectConversationNarration(spoken, introducer, observer), "spans")
                    .Any(span => ReadString(span, "kind", "") == "spoken_statement"
                        && ReadString(span, "text", "").Contains(quote, StringComparison.Ordinal));
                if (!heard.Contains(observer) || quote.Length < 8 || quote.Length > 600 || name.Length > 80
                    || string.IsNullOrWhiteSpace(spoken) || !audibleQuote
                    || string.IsNullOrWhiteSpace(name) || !quote.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(TestDict("ok", false, "error", "Introduction lacks an accepted audible quote from its actual introducer.", "write", write));
                    continue;
                }
                if (!present.Contains(observer) || !present.Contains(subject) || !present.Contains(introducer) || string.IsNullOrWhiteSpace(name))
                {
                    results.Add(new Dictionary<string, object> { ["ok"] = false, ["error"] = "Introduction rejected because observer, subject, introducer, or name was not present in the scene.", ["write"] = write });
                    continue;
                }
                Dictionary<string, object> request = new Dictionary<string, object>(write, StringComparer.OrdinalIgnoreCase)
                {
                    ["campaignId"] = campaignId,
                    ["observerHeroStringId"] = observer,
                    ["subjectHeroStringId"] = subject,
                    ["introducerHeroStringId"] = introducer,
                    ["claimedName"] = name,
                    ["kind"] = string.Equals(introducer, subject, StringComparison.OrdinalIgnoreCase) ? "self" : "third_party",
                    ["encounterId"] = encounterId,
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                    ["correlationId"] = OperationsService.EnsureCorrelationId(payload)
                };
                results.Add(IdentityIntroductionApi(request, connection));
            }
            return results;
        }

        internal static string UpsertVerifiedIdentity(ReignDbConnection connection, Dictionary<string, object> observer, Dictionary<string, object> subject, string source, string sourceEntityId, double worldDay, string correlationId)
        {
            string observerId = IdentityHeroId(observer), subjectId = IdentityHeroId(subject), canonicalName = ReadString(subject, "name", "");
            if (string.IsNullOrWhiteSpace(observerId) || string.IsNullOrWhiteSpace(subjectId) || string.Equals(observerId, subjectId, StringComparison.OrdinalIgnoreCase)) return "unchanged";
            Dictionary<string, object> existing = ReadStoredAcquaintance(connection, observerId, subjectId);
            string result = existing == null ? "created" : IdentityStateVerified(existing) ? "unchanged" : "upgraded";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<string> aliases = existing == null ? new List<string>() : DialogueService.TextListFromJson(ReadString(existing, "aliases_json", "[]"));
            string claimed = existing == null ? "" : ReadString(existing, "claimed_name", "");
            if (!string.IsNullOrWhiteSpace(claimed) && !aliases.Contains(claimed, StringComparer.OrdinalIgnoreCase)) aliases.Add(claimed);
            CharactersStore.UpsertAcquaintances2(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["canonical"] = canonicalName, ["claimed"] = claimed, ["aliases"] = Json.Serialize(aliases), ["source"] = source ?? "", ["sourceEntity"] = sourceEntityId ?? "", ["day"] = worldDay, ["ts"] = ts });
            if (result != "unchanged") AddIdentityEvidence(connection, observerId, subjectId, "verified", claimed, canonicalName, source, sourceEntityId, "", 1d, worldDay, new Dictionary<string, object> { ["correlationId"] = correlationId ?? "" });
            return result;
        }

        private static void UpsertClaimedIdentity(ReignDbConnection connection, string observerId, string subjectId, string canonicalName, string claimedName, string source, string sourceEntityId, string encounterId, double worldDay, Dictionary<string, object> payload)
        {
            Dictionary<string, object> existing = ReadAcquaintance(connection, observerId, subjectId);
            if (IdentityStateVerified(existing))
            {
                string knownCanonical = ReadString(existing, "canonical_name", canonicalName);
                if (!string.Equals(NormalizeIdentityName(knownCanonical), NormalizeIdentityName(claimedName), StringComparison.OrdinalIgnoreCase))
                {
                    AddIdentityEvidence(connection, observerId, subjectId, "conflicting_claim", claimedName, knownCanonical, source, sourceEntityId, encounterId, 0.25d, worldDay, payload);
                }
                return;
            }
            List<string> aliases = existing == null ? new List<string>() : DialogueService.TextListFromJson(ReadString(existing, "aliases_json", "[]"));
            string prior = existing == null ? "" : ReadString(existing, "claimed_name", "");
            if (!string.IsNullOrWhiteSpace(prior) && !aliases.Contains(prior, StringComparer.OrdinalIgnoreCase)) aliases.Add(prior);
            if (!aliases.Contains(claimedName, StringComparer.OrdinalIgnoreCase)) aliases.Add(claimedName);
            aliases = PruneAmbiguousAddressAliases(connection, observerId, subjectId, aliases, claimedName);
            string state = aliases.Any(alias => !NormalizeIdentityName(alias).Equals(NormalizeIdentityName(claimedName), StringComparison.OrdinalIgnoreCase)) ? "disputed" : "claimed";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            CharactersStore.UpsertAcquaintances3(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["state"] = state, ["canonical"] = canonicalName ?? "", ["claimed"] = claimedName, ["aliases"] = Json.Serialize(aliases), ["source"] = source ?? "", ["sourceEntity"] = sourceEntityId ?? "", ["day"] = worldDay, ["encounter"] = encounterId ?? "", ["payload"] = "{}", ["ts"] = ts });
            AddIdentityEvidence(connection, observerId, subjectId, state == "disputed" ? "disputed_claim" : "claimed_identity", claimedName, canonicalName, source, sourceEntityId, encounterId, 0.6d, worldDay, payload);
        }

        private static List<string> PruneAmbiguousAddressAliases(ReignDbConnection connection, string observerId, string subjectId,
            List<string> aliases, string currentClaim)
        {
            List<string> result = new List<string>();
            foreach (string alias in (aliases ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (NormalizeIdentityName(alias).Equals(NormalizeIdentityName(currentClaim), StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(alias);
                    continue;
                }
                List<Dictionary<string, object>> evidence = CharactersStore.SelectIdentityEvidence2(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["alias"] = alias });
                bool supported = evidence.Any(row =>
                {
                    string type = ReadString(row, "evidence_type", "");
                    if (type.Equals("verified", StringComparison.OrdinalIgnoreCase) || type.Equals("trusted_introduction", StringComparison.OrdinalIgnoreCase)) return true;
                    Dictionary<string, object> sourcePayload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                    string sourceText = ReadFirstString(sourcePayload, "playerText", "text", "message");
                    string explicitName = ExtractSelfIntroducedName(sourceText, false);
                    if (NormalizeIdentityName(explicitName).Equals(NormalizeIdentityName(alias), StringComparison.OrdinalIgnoreCase)) return true;
                    string permissiveName = ExtractSelfIntroducedName(sourceText, true);
                    return IdentityIntroductionAnswerExpected(sourcePayload)
                        && NormalizeIdentityName(permissiveName).Equals(NormalizeIdentityName(alias), StringComparison.OrdinalIgnoreCase);
                });
                bool ambiguousAddressOnly = evidence.Count > 0 && !supported && evidence.All(row =>
                {
                    Dictionary<string, object> sourcePayload = TryParseJsonObject(ReadString(row, "payload_json", "")) ?? new Dictionary<string, object>();
                    string sourceText = ReadFirstString(sourcePayload, "playerText", "text", "message");
                    return NormalizeIdentityName(ExtractSelfIntroducedName(sourceText, true)).Equals(NormalizeIdentityName(alias), StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(ExtractSelfIntroducedName(sourceText, false))
                        && !IdentityIntroductionAnswerExpected(sourcePayload);
                });
                if (!ambiguousAddressOnly) result.Add(alias);
            }
            if (!result.Contains(currentClaim, StringComparer.OrdinalIgnoreCase)) result.Add(currentClaim);
            return result;
        }

        internal static Dictionary<string, object> BuildIdentityView(Dictionary<string, object> row, Dictionary<string, object> context)
        {
            string state = row == null ? "encountered_unknown" : ReadString(row, "identity_state", "encountered_unknown");
            string claimed = row == null ? "" : ReadString(row, "claimed_name", "");
            string canonical = row == null ? ReadString(context, "canonicalName", "") : ReadString(row, "canonical_name", ReadString(context, "canonicalName", ""));
            bool verified = string.Equals(state, "verified", StringComparison.OrdinalIgnoreCase);
            string safeLabel = DeriveSceneIdentityLabel(context);
            string usable = verified ? FirstNonEmpty(canonical, claimed, safeLabel)
                : string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase) ? FirstNonEmpty(claimed, safeLabel)
                : string.Equals(state, "disputed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claimed) ? "the stranger claiming to be " + claimed
                : safeLabel;
            bool subjectSexKnown = TryResolveIdentitySubjectSex(
                context, out string subjectSex);
            bool verifiedQualifiedIntroduction = verified && ReadString(row, "verification_source", "")
                .StartsWith("authoritative_qualified_self_introduction", StringComparison.Ordinal);
            Dictionary<string, object> result =
                new Dictionary<string, object>
            {
                ["identityState"] = state,
                ["usableName"] = usable,
                ["safeLabel"] = safeLabel,
                ["claimedName"] = verifiedQualifiedIntroduction ? usable : claimed,
                ["authoritativeIntroducedName"] = verifiedQualifiedIntroduction ? claimed : "",
                ["canonicalNameAllowed"] = verified,
                ["knowsIdentity"] = verified || string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase),
                ["knowledgeSource"] = row == null ? "none" : ReadString(row, "verification_source", ""),
                ["confidence"] = row == null ? 0d : ReadDouble(row, "confidence", 0d),
                ["encounterCount"] = row == null ? 0 : ReadInt(row, "encounter_count", 0),
                ["recognitionAttempts"] = row == null ? 0 : ReadInt(row, "recognition_attempts", 0),
                ["subjectSexKnown"] = subjectSexKnown,
                ["subjectSex"] = subjectSexKnown
                    ? subjectSex
                    : "unknown",
                ["instruction"] = verified
                    ? "The observer knows this person's canonical identity and may use their name and established titles."
                    : string.Equals(state, "claimed", StringComparison.OrdinalIgnoreCase)
                        ? "The observer knows only the name this person claimed. Use that claimed name without treating it as independently verified."
                        : string.Equals(state, "disputed", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(claimed)
                            ? "The observer has conflicting or otherwise disputed identity claims. They may repeat the exact latest claimed name as an unverified claim, use second person, or use the scene label, but must not present the canonical identity or titles as verified."
                            : "The observer does not know this person's name. Use only the scene-based label or second-person address; never reveal the canonical name or titles."
            };
            Dictionary<string, object> authorityView =
                BuildCurrentPoliticalAuthorityView(result, context);
            result["authorityView"] = authorityView;
            if (!verified && ReadBool(
                    authorityView, "publicOfficeKnown", false))
            {
                result["instruction"] =
                    "The observer does not know or recognize this person's canonical personal identity, so the canonical name remains forbidden. The observer does know the person's current public office from authoritative institutional context and may use only the supplied valid formal office addresses.";
            }
            return result;
        }

        private static bool TryResolveIdentitySubjectSex(
            Dictionary<string, object> context,
            out string sex)
        {
            sex = "unknown";
            context = context
                ?? new Dictionary<string, object>();
            Dictionary<string, object> native =
                ReadDictionary(context, "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> subject =
                ReadDictionary(native, "subject")
                ?? ReadDictionary(context, "subject")
                ?? new Dictionary<string, object>();
            string suppliedSex = ReadString(subject, "sex", "")
                .Trim().ToLowerInvariant();
            if (suppliedSex == "female" || suppliedSex == "male")
            {
                sex = suppliedSex;
                return true;
            }
            if (!subject.ContainsKey("isFemale")) return false;
            sex = ReadBool(subject, "isFemale", false)
                ? "female"
                : "male";
            return true;
        }

        internal static Dictionary<string, object>
            BuildCurrentPoliticalAuthorityView(
                Dictionary<string, object> identityView,
                Dictionary<string, object> context)
        {
            identityView = identityView
                ?? new Dictionary<string, object>();
            context = context
                ?? new Dictionary<string, object>();
            Dictionary<string, object> native =
                ReadDictionary(context, "nativePoliticalContext")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> observer =
                ReadDictionary(native, "observer")
                ?? ReadDictionary(context, "observer")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> subject =
                ReadDictionary(native, "subject")
                ?? ReadDictionary(context, "subject")
                ?? new Dictionary<string, object>();
            bool identityVerified = ReadBool(
                identityView, "canonicalNameAllowed", false);
            string observerKingdomId = ReadString(
                observer, "kingdomId", "");
            string subjectKingdomId = ReadString(
                subject, "kingdomId", "");
            string observerKingdomName = ReadString(
                observer, "kingdomName", "");
            string subjectKingdomName = ReadString(
                subject, "kingdomName", "");
            string observerClanId = ReadString(
                observer, "clanId", "");
            string subjectClanId = ReadString(
                subject, "clanId", "");
            bool sameClan =
                !string.IsNullOrWhiteSpace(observerClanId)
                && observerClanId.Equals(
                    subjectClanId,
                    StringComparison.OrdinalIgnoreCase);
            bool sameKingdom =
                !string.IsNullOrWhiteSpace(observerKingdomId)
                && observerKingdomId.Equals(
                    subjectKingdomId,
                    StringComparison.OrdinalIgnoreCase);
            bool subjectIsRuler = ReadBool(
                subject, "isRuler", false);
            bool observerIsRuler = ReadBool(
                observer, "isRuler", false);

            Dictionary<string, object> settlement =
                ReadDictionary(native, "settlement")
                ?? new Dictionary<string, object>();
            if (settlement.Count == 0)
            {
                foreach (Dictionary<string, object> bundle in
                    ReadDictionaryList(context, "contextBundles"))
                {
                    if (!ReadString(bundle, "id", "").Equals(
                            "current_settlement_facts",
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    settlement = ReadDictionary(
                        ReadDictionary(bundle, "data")
                            ?? new Dictionary<string, object>(),
                        "settlement")
                        ?? new Dictionary<string, object>();
                    break;
                }
            }
            string settlementId = ReadFirstString(
                settlement, "settlementId", "id");
            string settlementName = ReadString(
                settlement, "name", "");
            string ownerClanId = ReadString(
                settlement, "ownerClanId", "");
            string ownerClanName = ReadString(
                settlement, "ownerClanName", "");
            string settlementKingdomId = ReadFirstString(
                settlement, "kingdomId", "factionId");
            string settlementKingdomName = ReadFirstString(
                settlement, "kingdomName", "factionName");
            string governorHeroId = ReadFirstString(
                settlement, "governorHeroId", "governorId");
            Dictionary<string, object> diplomacy =
                ReadDictionary(native, "diplomacy")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> subjectKingdomState =
                ReadDictionary(diplomacy, "playerKingdom")
                ?? new Dictionary<string, object>();
            subjectKingdomName = FirstNonEmpty(
                subjectKingdomName,
                ReadFirstString(
                    subjectKingdomState,
                    "kingdomName",
                    "name"),
                !string.IsNullOrWhiteSpace(subjectKingdomId)
                    && subjectKingdomId.Equals(
                        settlementKingdomId,
                        StringComparison.OrdinalIgnoreCase)
                    ? settlementKingdomName
                    : "");
            List<Dictionary<string, object>> enemyKingdoms =
                ReadDictionaryList(
                    diplomacy, "playerEnemyKingdoms");
            string subjectId = IdentityHeroId(subject);
            string observerId = IdentityHeroId(observer);
            string encounterMode = ActionPipelineService.NormalizeLookup(
                ReadString(context, "mode", ""));
            bool institutionalEncounter =
                ReadBool(context, "officialAudience", false)
                || ReadBool(context, "officialEvent", false)
                || encounterMode.Contains("social_event")
                || encounterMode.Contains("party_chat")
                || encounterMode.Contains("group")
                || encounterMode.Contains("court")
                || encounterMode.Contains("castle");
            bool subjectRealmOwnsCurrentSettlement =
                !string.IsNullOrWhiteSpace(subjectKingdomId)
                && subjectKingdomId.Equals(
                    settlementKingdomId,
                    StringComparison.OrdinalIgnoreCase);
            bool subjectClanOwnsCurrentSettlement =
                !string.IsNullOrWhiteSpace(subjectClanId)
                && subjectClanId.Equals(
                    ownerClanId,
                    StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> civic = ReadDictionary(native, "observerCivicAffiliation");
            bool localCivicSovereign = ReadBool(native, "authoritative", false)
                && ReadBool(civic, "authoritative", false) && ReadBool(civic, "available", false)
                && ReadBool(civic, "localEncounter", false)
                && !string.IsNullOrWhiteSpace(observerId) && !string.IsNullOrWhiteSpace(subjectId)
                && ReadString(civic, "heroStringId", "").Equals(observerId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(settlementId)
                && ReadString(civic, "settlementId", "").Equals(settlementId, StringComparison.OrdinalIgnoreCase)
                && subjectRealmOwnsCurrentSettlement
                && ReadString(civic, "kingdomId", "").Equals(subjectKingdomId, StringComparison.OrdinalIgnoreCase)
                && ReadString(civic, "sovereignHeroStringId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase)
                && (ReadString(civic, "source", "") == "native_home_settlement"
                    || ReadString(civic, "source", "") == "native_notable_roster");
            // Native political summaries omit resident metadata. Use the matching
            // full observer profile, while current native ownership remains authoritative.
            var residentObserver = IsEncounteredResidentProfile(observer)
                ? observer : ReadDictionary(context, "observer");
            var residentHome = ReadDictionary(residentObserver, "encounteredResident");
            bool residentCivicSovereign = identityVerified && subjectIsRuler
                && ReadBool(native, "authoritative", false)
                && IsEncounteredResidentProfile(residentObserver)
                && !string.IsNullOrWhiteSpace(observerId)
                && observerId.Equals(IdentityHeroId(residentObserver), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(subjectId) && !string.IsNullOrWhiteSpace(settlementId)
                && ReadString(residentHome, "homeSettlementId", "").Equals(settlementId, StringComparison.OrdinalIgnoreCase)
                && ReadString(residentHome, "homeRulerId", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase)
                && ReadString(residentHome, "homeKingdomId", "").Equals(subjectKingdomId, StringComparison.OrdinalIgnoreCase)
                && subjectRealmOwnsCurrentSettlement;
            localCivicSovereign = localCivicSovereign || residentCivicSovereign;
            bool publicOfficeKnown = subjectIsRuler
                && !string.IsNullOrWhiteSpace(subjectKingdomId)
                && (identityVerified || localCivicSovereign
                    || sameKingdom && institutionalEncounter
                    || sameKingdom && subjectRealmOwnsCurrentSettlement
                        && subjectClanOwnsCurrentSettlement);
            bool realmSovereignKnown = publicOfficeKnown;
            string memberOffice = ReadString(subject, "governmentOfficeTitle", "");
            bool memberOfficeKnown = !subjectIsRuler && identityVerified && ReadBool(native, "authoritative", false)
                && ReadBool(subject, "isAlive", true) && !ReadBool(subject, "isChild", false)
                && ReadString(subject, "governmentOfficeKingdomId", "").Length > 0
                && new[] { "Senator", "Peer", "Councillor", "Assembly member" }.Any(title => memberOffice == title || memberOffice == title + " and party speaker");
            publicOfficeKnown |= memberOfficeKnown;
            bool subjectIsObserverSovereign =
                realmSovereignKnown && (sameKingdom || localCivicSovereign);
            bool observerIsSubjectSovereign =
                identityVerified && observerIsRuler
                && sameKingdom;
            bool currentSettlementOwnerKnown =
                identityVerified
                && !string.IsNullOrWhiteSpace(subjectClanId)
                && subjectClanId.Equals(
                    ownerClanId,
                    StringComparison.OrdinalIgnoreCase);
            bool observerClanOwnsCurrentSettlement =
                !string.IsNullOrWhiteSpace(observerClanId)
                && observerClanId.Equals(
                    ownerClanId,
                    StringComparison.OrdinalIgnoreCase);
            bool subjectIsCurrentGovernor =
                identityVerified
                && !string.IsNullOrWhiteSpace(subjectId)
                && subjectId.Equals(
                    governorHeroId,
                    StringComparison.OrdinalIgnoreCase);
            bool observerIsCurrentGovernor =
                !string.IsNullOrWhiteSpace(observerId)
                && observerId.Equals(
                    governorHeroId,
                    StringComparison.OrdinalIgnoreCase);
            List<string> recognizedRoles = identityVerified
                ? BuildAuthoritativePublicRoles(subject)
                : new List<string>();
            List<string> observerRoles =
                BuildAuthoritativePublicRoles(observer);
            if (realmSovereignKnown)
                recognizedRoles.Add("realm_sovereign");
            if (memberOfficeKnown) recognizedRoles.Add(memberOffice);
            if (subjectIsObserverSovereign)
                recognizedRoles.Add(
                    "subject_is_observer_sovereign");
            if (observerIsSubjectSovereign)
                recognizedRoles.Add(
                    "observer_is_subject_sovereign");
            if (sameClan && identityVerified)
                recognizedRoles.Add("same_clan");
            if (currentSettlementOwnerKnown)
                recognizedRoles.Add(
                    "current_settlement_owner_clan_member");
            if (subjectIsCurrentGovernor)
                recognizedRoles.Add("current_settlement_governor");
            recognizedRoles = recognizedRoles
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string authorityRelationship =
                subjectIsObserverSovereign
                    ? "subject_is_observer_sovereign"
                    : observerIsSubjectSovereign
                        ? "observer_is_subject_sovereign"
                        : sameClan && identityVerified
                            ? "same_clan"
                            : sameKingdom && identityVerified
                                ? "same_kingdom_peer_or_subject"
                                : identityVerified
                                    ? "known_foreign_or_independent_person"
                                    : "identity_unverified";
            string source = subjectIsObserverSovereign
                ? identityVerified
                    ? "verified_identity_live_allegiance"
                    : institutionalEncounter
                        ? "institutional_encounter_live_allegiance"
                        : "sovereign_seat_live_allegiance"
                : currentSettlementOwnerKnown
                    ? "live_current_settlement_ownership"
                    : identityVerified
                        ? "verified_identity_current_native_office"
                        : "identity_not_verified";
            if (localCivicSovereign) source = "local_notable_live_civic_authority";
            if (residentCivicSovereign) source = "recognized_resident_live_civic_authority";
            return new Dictionary<string, object>
            {
                ["localCivicSovereign"] = localCivicSovereign,
                ["identityVerified"] = identityVerified,
                ["personalIdentityKnown"] = identityVerified,
                ["publicOfficeKnown"] = publicOfficeKnown,
                ["officeKnowledgeSource"] = publicOfficeKnown
                    ? source
                    : "office_not_established",
                ["source"] = source,
                ["observerKingdomId"] = observerKingdomId,
                ["subjectKingdomId"] = subjectKingdomId,
                ["observerKingdomName"] = observerKingdomName,
                ["subjectKingdomName"] = subjectKingdomName,
                ["observerClanId"] = observerClanId,
                ["subjectClanId"] = subjectClanId,
                ["sameClan"] = sameClan,
                ["sameKingdom"] = sameKingdom,
                ["observerIsRuler"] = observerIsRuler,
                ["subjectIsRuler"] = subjectIsRuler,
                ["realmSovereignKnown"] =
                    realmSovereignKnown,
                ["subjectIsObserverSovereign"] =
                    subjectIsObserverSovereign,
                ["observerIsSubjectSovereign"] =
                    observerIsSubjectSovereign,
                ["authorityRelationship"] =
                    authorityRelationship,
                ["currentSettlementId"] = settlementId,
                ["currentSettlementName"] = settlementName,
                ["currentSettlementKingdomId"] =
                    settlementKingdomId,
                ["currentSettlementKingdomName"] =
                    settlementKingdomName,
                ["currentSettlementOwnerClanId"] =
                    ownerClanId,
                ["currentSettlementOwnerClanName"] =
                    ownerClanName,
                ["currentSettlementGovernorHeroId"] =
                    governorHeroId,
                ["currentSettlementOwnerKnown"] =
                    currentSettlementOwnerKnown,
                ["observerClanOwnsCurrentSettlement"] =
                    observerClanOwnsCurrentSettlement,
                ["subjectIsCurrentGovernor"] =
                    subjectIsCurrentGovernor,
                ["observerIsCurrentGovernor"] =
                    observerIsCurrentGovernor,
                ["subjectPrimaryRole"] =
                    identityVerified
                        ? PrimaryAuthoritativePublicRole(subject)
                        : realmSovereignKnown
                            ? "realm_sovereign"
                            : memberOfficeKnown ? "government_member"
                            : "identity_unverified",
                ["observerPrimaryRole"] =
                    PrimaryAuthoritativePublicRole(observer),
                ["observerRoles"] = observerRoles,
                ["currentDiplomacyKnown"] =
                    ReadBool(native, "authoritative", false)
                    && ReadBool(
                        subjectKingdomState,
                        "available", false),
                ["currentEnemyKingdomCount"] =
                    enemyKingdoms.Count > 0
                        ? enemyKingdoms.Count
                        : ReadInt(
                            subjectKingdomState,
                            "warCount", 0),
                ["currentEnemyKingdomIds"] =
                    enemyKingdoms.Select(x =>
                            ReadFirstString(
                                x, "kingdomId", "factionId"))
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x))
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                ["currentEnemyKingdomNames"] =
                    enemyKingdoms.Select(x =>
                            ReadString(x, "name", ""))
                        .Where(x =>
                            !string.IsNullOrWhiteSpace(x))
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                ["nativePoliticalObservedWorldDay"] =
                    ReadDouble(
                        native, "observedWorldDay", -1d),
                ["recognizedRoles"] = recognizedRoles,
                ["validFormalAddresses"] = realmSovereignKnown
                    ? SovereignFormalAddresses(subject)
                    : memberOfficeKnown ? new List<string> { memberOffice.Split(new[] { " and " }, StringSplitOptions.None)[0] } : new List<string>(),
                ["governmentMemberOfficeKnown"] = memberOfficeKnown,
                ["governmentMemberRealmId"] = memberOfficeKnown ? ReadString(subject, "governmentOfficeKingdomId", "") : "",
                ["instruction"] = subjectIsObserverSovereign
                    ? identityVerified
                        ? "The observer knows this person is their own current sovereign and knows their personal identity. Personality may produce loyalty, strategic flattery, manipulation, resentment, candid opposition, or deliberate defiance, but the observer must understand the unequal political relationship and its risks."
                        : "The observer knows from the institutional encounter and current allegiance that this person is their sovereign, but does not know or recognize the sovereign's personal identity. Use a valid sovereign title without revealing the unknown name."
                    : realmSovereignKnown
                        ? "The observer recognizes this person as a current foreign or independent sovereign. Clothing, lack of retinue, or an understated presentation cannot make that verified public office uncertain."
                    : currentSettlementOwnerKnown
                        ? "The observer recognizes this person's clan as the current owner of the settlement. Appearance cannot override current native ownership."
                    : memberOfficeKnown
                        ? "The observer recognizes this person's current government office. Address them naturally by the supplied title when appropriate. This is membership in the named realm; it grants no sovereign or royal-council powers."
                        : "Do not infer a political office that is not established by current native facts and verified identity."
            };
        }

        internal static List<string> SovereignFormalAddresses(
            Dictionary<string, object> subject)
        {
            bool female = ReadBool(subject, "isFemale", false)
                || ReadString(subject, "sex", "").Equals(
                    "female", StringComparison.OrdinalIgnoreCase);
            return female
                ? new List<string>
                {
                    "Your Grace", "Your Majesty", "my queen"
                }
                : new List<string>
                {
                    "Your Grace", "Your Majesty", "Sire", "my king"
                };
        }

        private static List<string> BuildAuthoritativePublicRoles(
            Dictionary<string, object> hero)
        {
            hero = hero ?? new Dictionary<string, object>();
            List<string> roles = new List<string>();
            bool female = ReadBool(
                hero, "isFemale",
                ReadString(hero, "sex", "")
                    .Equals("female",
                        StringComparison.OrdinalIgnoreCase));
            if (ReadBool(hero, "isRuler", false))
                roles.Add(female ? "queen" : "king");
            if (ReadBool(hero, "isLord", false))
                roles.Add(female ? "lady" : "lord");
            if (!string.IsNullOrWhiteSpace(
                    ReadFirstString(
                        hero, "governorOfSettlementId",
                        "governor_settlement_id")))
                roles.Add("governor");
            if (ReadBool(hero, "isNotable",
                    ReadInt(hero, "is_notable", 0) != 0))
                roles.Add("notable");
            if (ReadBool(hero, "isWanderer",
                    ReadInt(hero, "is_wanderer", 0) != 0))
                roles.Add("wanderer");
            string occupation = ActionPipelineService.NormalizeLookup(
                ReadString(hero, "occupation", ""));
            if (roles.Count == 0)
            {
                if (occupation.Contains("merchant"))
                    roles.Add("merchant");
                else if (occupation.Contains("artisan"))
                    roles.Add("artisan");
                else if (occupation.Contains("gang"))
                    roles.Add("gang_leader");
                else if (occupation.Contains("rural"))
                    roles.Add("rural_notable");
                else
                    roles.Add("commoner_or_unaffiliated");
            }
            return roles.Distinct(
                StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string PrimaryAuthoritativePublicRole(
            Dictionary<string, object> hero)
        {
            List<string> roles =
                BuildAuthoritativePublicRoles(hero);
            return roles.FirstOrDefault() ?? "unknown";
        }

        private static string DeriveSceneIdentityLabel(Dictionary<string, object> context)
        {
            string supplied = ReadFirstString(context, "visibleLabel", "sceneLabel", "unknownLabel");
            if (!string.IsNullOrWhiteSpace(supplied)) return DialogueService.LimitText(supplied, 80);
            Dictionary<string, object> appearance = ReadDictionary(context, "appearance") ?? ReadDictionary(ReadDictionary(context, "subject"), "appearance") ?? new Dictionary<string, object>();
            string status = ReadString(appearance, "visibleStatusLabel", "").ToLowerInvariant();
            string mode = ReadString(context, "mode", "").ToLowerInvariant();
            if (ReadBool(context, "isPrisoner", false)) return "the prisoner";
            if (mode.Contains("court")) return status.Contains("noble") ? "the unknown noble" : "the petitioner";
            if (status.Contains("noble") || status.Contains("wealth")) return "the well-dressed stranger";
            if (status.Contains("poor") || status.Contains("destitute")) return "the poorly dressed traveler";
            List<Dictionary<string, object>> equipment = ReadDictionaryList(appearance, "civilianEquipment").Concat(ReadDictionaryList(appearance, "battleEquipment")).ToList();
            if (equipment.Any(x => ReadBool(x, "isWeapon", false))) return "the armed stranger";
            return "the stranger";
        }

        internal static bool IdentityIntroductionAnswerExpected(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            List<string> candidateNpcLines = ReadDictionaryList(payload, "groupTranscript")
                .Concat(ReadDictionaryList(payload, "transcript"))
                .Where(row => ReadString(row, "role", "").Equals("npc", StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadFirstString(row, "text", "content", "message"))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            string playerName = FirstNonEmpty(ReadFirstString(payload, "playerName", "mainHeroName"),
                ReadString(ReadDictionary(payload, "subject"), "name", ""));
            foreach (string line in ReadStringList(payload, "transcript").AsEnumerable().Reverse())
            {
                string trimmed = (line ?? "").Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                if (!string.IsNullOrWhiteSpace(playerName)
                    && trimmed.StartsWith(playerName + ":", StringComparison.OrdinalIgnoreCase)) continue;
                candidateNpcLines.Add(trimmed);
                break;
            }

            string latestNpcLine = candidateNpcLines.LastOrDefault() ?? "";
            return DialogueService.ContainsAny(latestNpcLine.ToLowerInvariant(),
                "what is your name", "what's your name", "who are you", "what are you called",
                "tell me your name", "give me your name", "state your name", "introduce yourself",
                "not introduced yourself", "not yet introduced yourself", "not yet introduced themselves",
                "have not introduced yourself", "haven't introduced yourself");
        }

        internal static string ExtractSelfIntroducedName(
            string text,
            bool allowNameFirstAnswer = false,
            string canonicalName = "")
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string clean = Regex.Replace(text, @"\*[^*]*\*", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            const string namePattern = @"([A-Z][\p{L}'\-]*(?:\s+(?:of|the|de|von|fen|Banu|[A-Z][\p{L}'\-]*)){0,3})";
            Match match = Regex.Match(clean, @"(?i:\b(?:my\s+(?:real\s+)?name\s+is|call\s+me|you\s+may\s+call\s+me|people\s+call\s+me))\s+" + namePattern + @"(?=\s*[,.;!?]|\s*$)", RegexOptions.CultureInvariant);
            if (!match.Success)
                match = Regex.Match(clean, @"(?i:\bwhen\s+i\s+said)\s+" + namePattern + @"\s*,?\s*(?i:i\s+mean(?:t)?\s+(?:that\s+)?(?:it\s+)?is\s+my\s+(?:real\s+)?name)\b", RegexOptions.CultureInvariant);
            if (!match.Success)
                match = Regex.Match(clean, @"\b" + namePattern + @"\s+(?i:is\s+my\s+(?:real\s+)?name)\b", RegexOptions.CultureInvariant);
            // A direct answer to "Who are you?" commonly begins with a name
            // followed by the speaker continuing with "I...".  Keeping this
            // shape anchored to the start avoids treating an arbitrary proper
            // noun later in the sentence as a self-introduction. This ambiguous
            // form is valid only when the preceding NPC line actually requested
            // an introduction; otherwise "Menor, I am giving you..." is address.
            if (!match.Success && allowNameFirstAnswer)
                match = Regex.Match(clean, @"^\s*" + namePattern + @"\s*,\s*(?i:i(?:\s+am|'m|’m|m)\b)", RegexOptions.CultureInvariant);
            if (!match.Success)
                match = Regex.Match(clean, @"(?i:\b(?:i\s+am|i'm|i’m|im))\s+" + namePattern + @"(?=\s*[,.;!?]|\s*$)", RegexOptions.CultureInvariant);
            bool nameAndTitleAnswer = false;
            if (!match.Success
                && (allowNameFirstAnswer
                    || !string.IsNullOrWhiteSpace(canonicalName)))
            {
                match = Regex.Match(
                    clean,
                    @"^\s*" + namePattern
                        + @"\s*[,;:\-\u2014]\s*"
                        + @"(?i:(?:the|a|an)\s+)?"
                        + @"(?:lord|lady|king|queen|ruler|sovereign|"
                        + @"owner|governor|vassal|mercenary|wanderer|"
                        + @"merchant|soldier|captain|commander)\b",
                    RegexOptions.CultureInvariant);
                nameAndTitleAnswer = match.Success;
            }
            if (!match.Success) return string.Empty;
            string candidate = match.Groups[1].Value.Trim();
            if (nameAndTitleAnswer
                && !string.IsNullOrWhiteSpace(canonicalName)
                && !NormalizeIdentityName(candidate).Equals(
                    NormalizeIdentityName(canonicalName),
                    StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            string first = candidate.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string[] rejected = { "a", "an", "the", "yes", "no", "well", "actually", "honestly", "hello", "hi", "greetings", "thanks", "thank", "here", "ready", "sorry", "asking", "looking", "going", "trying", "standing", "kneeling", "traveling", "travelling", "pleased", "glad", "afraid", "lord", "lady", "king", "queen", "ruler", "merchant", "soldier", "prisoner", "traveler", "traveller" };
            if (rejected.Contains(first, StringComparer.OrdinalIgnoreCase)) return string.Empty;
            return candidate.Length <= 80 ? candidate : string.Empty;
        }

        private static bool QuarantineAmbiguousStoredIdentityClaim(ReignDbConnection connection,
            Dictionary<string, object> row, string observerId, string subjectId, double worldDay)
        {
            if (row == null || IdentityStateVerified(row)) return false;
            string state = ReadString(row, "identity_state", "");
            string claimed = ReadString(row, "claimed_name", "");
            if (string.IsNullOrWhiteSpace(claimed)
                || !(state.Equals("claimed", StringComparison.OrdinalIgnoreCase)
                    || state.Equals("disputed", StringComparison.OrdinalIgnoreCase))) return false;

            Dictionary<string, object> sourcePayload = TryParseJsonObject(ReadString(row, "payload_json", ""))
                ?? new Dictionary<string, object>();
            if (sourcePayload.Count == 0)
            {
                Dictionary<string, object> evidence = CharactersStore.SelectIdentityEvidence3(connection, new Dictionary<string, object>
                    {
                        ["observer"] = observerId,
                        ["subject"] = subjectId
                    }).FirstOrDefault();
                sourcePayload = TryParseJsonObject(ReadString(evidence, "payload_json", ""))
                    ?? new Dictionary<string, object>();
            }
            string sourceText = ReadFirstString(sourcePayload, "playerText", "text", "message");
            string explicitName = ExtractSelfIntroducedName(
                sourceText,
                false,
                ReadString(row, "canonical_name", ""));
            string permissiveName = ExtractSelfIntroducedName(sourceText, true);
            bool supported = NormalizeIdentityName(explicitName).Equals(NormalizeIdentityName(claimed), StringComparison.OrdinalIgnoreCase)
                || IdentityIntroductionAnswerExpected(sourcePayload)
                    && NormalizeIdentityName(permissiveName).Equals(NormalizeIdentityName(claimed), StringComparison.OrdinalIgnoreCase);
            if (supported || !NormalizeIdentityName(permissiveName).Equals(NormalizeIdentityName(claimed), StringComparison.OrdinalIgnoreCase))
                return false;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            CharactersStore.UpdateAcquaintances(connection, new Dictionary<string, object>
                {
                    ["observer"] = observerId, ["subject"] = subjectId, ["payload"] = Json.Serialize(sourcePayload), ["ts"] = ts
                });
            AddIdentityEvidence(connection, observerId, subjectId, "invalid_claim_quarantined", claimed,
                ReadString(row, "canonical_name", ""), "ambiguous_name_first_address", subjectId,
                ReadString(row, "last_encounter_id", ""), 0d, worldDay, sourcePayload);
            return true;
        }

        internal static bool IdentityStateVerified(Dictionary<string, object> row)
        {
            return row != null && string.Equals(ReadString(row, "identity_state", ""), "verified", StringComparison.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> ReadAcquaintance(ReignDbConnection connection, string observerId, string subjectId)
        {
            Dictionary<string, object> stored = ReadStoredAcquaintance(connection, observerId, subjectId);
            List<Dictionary<string, object>> roster = CharactersStore.SelectIdentityRoster(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId });
            Dictionary<string, object> observer = roster.FirstOrDefault(x =>
                ReadString(x, "hero_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase));
            Dictionary<string, object> subject = roster.FirstOrDefault(x =>
                ReadString(x, "hero_id", "").Equals(subjectId, StringComparison.OrdinalIgnoreCase));
            string source = ImplicitIdentitySource(observer, subject);
            if (string.IsNullOrWhiteSpace(source)) return stored;
            if (IdentityStateVerified(stored)) return stored;
            return new Dictionary<string, object>
            {
                ["observer_id"] = observerId, ["subject_id"] = subjectId, ["identity_state"] = "verified",
                ["canonical_name"] = ReadString(subject, "canonical_name",
                    ReadString(stored, "canonical_name", "")),
                ["claimed_name"] = ReadString(stored, "claimed_name", ""),
                ["aliases_json"] = ReadString(stored, "aliases_json", "[]"),
                ["verification_source"] = source, ["source_entity_id"] = "",
                ["confidence"] = 1d, ["first_met_day"] = 0d, ["last_met_day"] = 0d,
                ["encounter_count"] = ReadInt(stored, "encounter_count", 0),
                ["last_encounter_id"] = ReadString(stored, "last_encounter_id", ""),
                ["recognition_attempts"] = ReadInt(stored, "recognition_attempts", 0),
                ["last_recognition_result"] = "", ["last_recognition_encounter_id"] = "",
                ["payload_json"] = "{}", ["updated_ts"] = 0L, ["implicit"] = true
            };
        }

        internal static Dictionary<string, object> ReadStoredAcquaintance(
            ReignDbConnection connection,
            string observerId,
            string subjectId)
        {
            return CharactersStore.SelectAcquaintances2(connection, new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId }).FirstOrDefault();
        }

        private static void InsertUnknownAcquaintance(ReignDbConnection connection, string observerId, string subjectId, string canonicalName, string encounterId, double worldDay, Dictionary<string, object> payload)
        {
            CharactersStore.InsertOrIgnoreAcquaintances(connection, new Dictionary<string, object>
            {
                ["observer"] = observerId, ["subject"] = subjectId, ["canonical"] = canonicalName ?? "", ["day"] = worldDay,
                ["encounter"] = encounterId ?? "", ["payload"] = "{}", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        private static void AddIdentityEvidence(ReignDbConnection connection, string observerId, string subjectId, string evidenceType, string claimedName, string canonicalName, string source, string sourceEntityId, string encounterId, double confidence, double worldDay, Dictionary<string, object> payload)
        {
            CharactersStore.InsertIdentityEvidence(connection, new Dictionary<string, object>
            {
                ["id"] = "identity_" + Guid.NewGuid().ToString("N"), ["observer"] = observerId, ["subject"] = subjectId, ["type"] = evidenceType,
                ["claimed"] = claimedName ?? "", ["canonical"] = canonicalName ?? "", ["source"] = source ?? "", ["sourceEntity"] = sourceEntityId ?? "",
                ["encounter"] = encounterId ?? "", ["confidence"] = confidence, ["day"] = worldDay,
                ["payload"] = Json.Serialize(CompactIdentityEvidencePayload(payload)), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        }

        private static int MigrateLegacyIdentityKnowledge(ReignDbConnection connection, Dictionary<string, Dictionary<string, object>> heroes, double worldDay, string correlationId)
        {
            if (string.Equals(ReadString(CharactersStore.SelectSchemaMeta(connection).FirstOrDefault(), "value", ""), "1", StringComparison.Ordinal)) return 0;
            int count = 0;
            foreach (Dictionary<string, object> session in CharactersStore.SelectConversationSessions2(connection))
            {
                string observerId = ReadString(session, "npc_id", ""), subjectId = ReadString(session, "player_id", "");
                if (!heroes.TryGetValue(subjectId, out Dictionary<string, object> subject)) continue;
                Dictionary<string, object> existing = ReadAcquaintance(connection, observerId, subjectId);
                if (existing != null) continue;
                UpsertClaimedIdentity(connection, observerId, subjectId, ReadString(subject, "name", ""), ReadString(subject, "name", ""), "legacy_dialogue_migration", subjectId, "", worldDay, new Dictionary<string, object> { ["correlationId"] = correlationId });
                count++;
            }
            CharactersStore.UpsertSchemaMeta(connection);
            return count;
        }

        private static Dictionary<string, object> IdentitySqlArgs(string observerId, string subjectId, double worldDay, string encounterId)
        {
            return new Dictionary<string, object> { ["observer"] = observerId, ["subject"] = subjectId, ["day"] = worldDay, ["encounter"] = encounterId ?? "", ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        }

        internal static string IdentityHeroId(Dictionary<string, object> hero)
        {
            return ReadFirstString(hero, "heroStringId", "heroId", "id");
        }

        private static bool IdentityIsLord(Dictionary<string, object> hero)
        {
            return ReadBool(hero, "isLord", false) || string.Equals(ReadString(hero, "occupation", ""), "Lord", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractAuthoritativeSelfIntroduction(string text, Dictionary<string, object> subject)
        {
            string clean = Regex.Replace(text ?? "", @"\*[^*]*\*", " ");
            Match match = Regex.Match(clean,
                @"\b(?:my\s+(?:real\s+)?name\s+is|i\s+am|i['’]m|call\s+me|you\s+may\s+call\s+me)\s+([\p{L}\p{M}'’\-]+(?:\s+[\p{L}\p{M}'’\-]+){0,6})(?=\s*[,.;!?]|\s*$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success && AuthoritativeIntroductionSource(match.Groups[1].Value, subject).Length > 0
                ? match.Groups[1].Value : "";
        }

        private static string ExtractNameFirstSelfIntroduction(
            string text, Dictionary<string, object> subject,
            Dictionary<string, object> payload)
        {
            string subjectId = IdentityHeroId(subject);
            string playerId = ReadFirstString(payload,
                "playerHeroStringId", "mainHeroStringId", "playerId");
            if (!ReadBool(subject, "isPlayer", false)
                && !ReadString(subject, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase)
                && !subjectId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                && !subjectId.Equals("main_hero", StringComparison.OrdinalIgnoreCase))
                return "";

            string canonicalName = ReadString(subject, "name", "").Trim();
            if (canonicalName.Length == 0) return "";
            string clean = Regex.Replace(text ?? "", @"\*[^*]*\*", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            if (clean.Length == 0) return "";

            List<string> candidates = new List<string> { canonicalName };
            foreach (string key in new[] { "clanName", "kingdomName" })
            {
                string qualifier = ReadString(subject, key, "").Trim();
                if (qualifier.Length == 0) continue;
                candidates.Add(canonicalName + " of " + qualifier);
                candidates.Add(canonicalName + " " + qualifier);
            }
            List<Dictionary<string, object>> others =
                DialogueService.MergedInteractionParticipantProfiles(payload)
                .Concat(new[] { ReadDictionary(payload, "observer") })
                .Where(profile => profile != null
                    && !IdentityHeroId(profile).Equals(subjectId,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (string candidate in candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(value => value.Length))
            {
                string pattern = string.Join(@"\s+", candidate.Split(
                    new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(Regex.Escape));
                if (!Regex.IsMatch(clean, @"^" + pattern + @"(?=\s*[,.;:!?]|\s*$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    continue;
                string normalized = NormalizeIdentityName(candidate);
                if (others.Any(profile =>
                    NormalizeIdentityName(ReadString(profile, "name", "")) == normalized))
                    continue;
                if (AuthoritativeIntroductionSource(candidate, subject).Length > 0)
                    return candidate;
            }
            return "";
        }

        private static string AuthoritativeIntroductionSource(string claimed, Dictionary<string, object> subject)
        {
            string name = NormalizeIdentityName(ReadString(subject, "name", ""));
            string normalized = NormalizeIdentityName(claimed);
            if (name.Length == 0 || normalized.Length == 0) return "";
            if (normalized == name) return "exact_self_introduction";
            foreach (string key in new[] { "clanName", "kingdomName" })
            {
                string qualifier = NormalizeIdentityName(ReadString(subject, key, ""));
                if (qualifier.Length != 0 && (normalized == name + " " + qualifier
                    || normalized == name + " of " + qualifier))
                    return "authoritative_qualified_self_introduction";
            }
            return "";
        }

        internal static string NormalizeIdentityName(string value)
        {
            return Regex.Replace((value ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD), @"[^\p{L}\p{N}]+", " ").Trim();
        }

        private static string ComputeIdentityRosterFingerprint(List<Dictionary<string, object>> heroes)
        {
            string canonical = string.Join("\n", (heroes ?? new List<Dictionary<string, object>>()).OrderBy(IdentityHeroId, StringComparer.OrdinalIgnoreCase).Select(x => string.Join("|", new[]
            {
                IdentityHeroId(x), ReadString(x,"clanId",""), ReadString(x,"kingdomId",""), IdentityIsLord(x)?"1":"0", ReadBool(x,"isRuler",false)?"1":"0", ReadBool(x,"isAlive",true)?"1":"0",
                ReadBool(x,"isAdult",!ReadBool(x,"isChild",false))?"1":"0", ReadBool(x,"isPlayer",false)?"1":"0",
                ReadString(x,"sex",ReadBool(x,"isFemale",false)?"female":"male"), ReadInt(x,"clanTier",0).ToString(CultureInfo.InvariantCulture),
                ReadInt(x,"currentCharm",ReadInt(x,"charm",0)).ToString(CultureInfo.InvariantCulture),
                ReadString(x,"occupation",""), ReadBool(x,"isNotable",false)?"1":"0", ReadBool(x,"isWanderer",false)?"1":"0",
                ReadBool(x,"isClanLeader",false)?"1":"0", ReadBool(x,"isMercenaryClan",false)?"1":"0",
                ReadString(x,"governorOfSettlementId",""), ReadString(x,"governorOfSettlementName",""),
                ReadString(x,"spouseId",""), ReadString(x,"fatherId",""), ReadString(x,"motherId",""), string.Join(",",ReadStringList(x,"childrenIds").OrderBy(v=>v,StringComparer.OrdinalIgnoreCase))
            })));
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        }
    }
}
