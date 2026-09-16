using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static bool MatchingCourtIdentityId(string left, string right)
            => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        // A formal audience identifies its presiding ruler. Merely standing in a
        // ruler's settlement, mentioning court in prose, or claiming a title does not.
        private static bool HasQualifiedCourtAudienceIdentity(Dictionary<string, object> payload,
            Dictionary<string, object> observer, Dictionary<string, object> subject)
        {
            var court = ReadDictionary(payload, "courtLifeContext");
            var native = ReadDictionary(payload, "nativePoliticalContext");
            if (court == null || native == null || observer == null || subject == null
                || !ReadBool(native, "authoritative", false)
                || ReadString(payload, "conversationMode", "") != "court_life"
                || ReadString(court, "conversationMode", "") != "court_life"
                || ReadString(court, "courtScope", "") != "capital"
                || string.IsNullOrWhiteSpace(ReadString(court, "sessionId", ""))
                || string.IsNullOrWhiteSpace(ReadString(court, "matterId", ""))
                || !ReadBool(court, "playerIsKingdomRuler", false)
                || !ReadBool(subject, "isRuler", false)) return false;
            string source = ReadString(court, "source", "");
            if (source != "International" && source != "Visitor" && source != "Family" && source != "Patronage") return false;
            var nativeSubject = ReadDictionary(native, "subject");
            var nativeObserver = ReadDictionary(native, "observer");
            var settlement = ReadDictionary(native, "settlement");
            if (nativeSubject == null || nativeObserver == null || settlement == null
                || !ReadBool(nativeSubject, "isRuler", false)) return false;
            string ruler = IdentityHeroId(subject), speaker = IdentityHeroId(observer);
            string kingdom = ReadString(subject, "kingdomId", "");
            string capital = ReadString(court, "capitalSettlementStringId", "");
            double day = ReadDouble(payload, "worldDay", double.NaN);
            double observed = ReadDouble(native, "observedWorldDay", double.NaN);
            if (double.IsNaN(day) || double.IsInfinity(day) || double.IsNaN(observed) || double.IsInfinity(observed)
                || Math.Abs(day - observed) > 0.001d) return false;
            return MatchingCourtIdentityId(ReadString(court, "campaignId", ""), ReadString(payload, "campaignId", ""))
                && MatchingCourtIdentityId(ReadString(court, "timelineId", ""), ReadString(payload, "timelineId", ""))
                && MatchingCourtIdentityId(ReadString(court, "activeSpeakerHeroId", ""), speaker)
                && MatchingCourtIdentityId(IdentityHeroId(nativeObserver), speaker)
                && MatchingCourtIdentityId(ReadString(court, "playerHeroStringId", ""), ruler)
                && MatchingCourtIdentityId(IdentityHeroId(nativeSubject), ruler)
                && MatchingCourtIdentityId(ReadString(nativeSubject, "kingdomId", ""), kingdom)
                && MatchingCourtIdentityId(ReadString(court, "playerKingdomStringId", ""), kingdom)
                && MatchingCourtIdentityId(ReadFirstString(settlement, "kingdomId", "factionId"), kingdom)
                && MatchingCourtIdentityId(ReadFirstString(settlement, "settlementId", "id"), capital)
                && MatchingCourtIdentityId(ReadString(court, "hostSettlementStringId", ""), capital)
                && MatchingCourtIdentityId(ReadString(observer, "currentSettlementId", ""), capital);
        }

        private static void AddCourtAudienceIdentityTests(string campaignId, Action<string, bool, string, object> add)
        {
            var observer = IdentityTestHero("court_envoy", "Envoy", "foreign_clan", "foreign_realm", true, false);
            observer["currentSettlementId"] = "court_capital";
            var subject = IdentityTestHero("court_ruler", "Court Ruler", "ruler_clan", "court_realm", true, true);
            var court = new Dictionary<string, object> { ["campaignId"] = campaignId, ["timelineId"] = "main",
                ["conversationMode"] = "court_life", ["courtScope"] = "capital", ["sessionId"] = "court_session",
                ["matterId"] = "court_matter", ["source"] = "International", ["activeSpeakerHeroId"] = "court_envoy",
                ["playerHeroStringId"] = "court_ruler", ["playerKingdomStringId"] = "court_realm", ["playerIsKingdomRuler"] = true,
                ["capitalSettlementStringId"] = "court_capital", ["hostSettlementStringId"] = "court_capital" };
            var native = new Dictionary<string, object> { ["authoritative"] = true, ["observedWorldDay"] = 50d,
                ["subject"] = subject, ["observer"] = observer,
                ["settlement"] = new Dictionary<string, object> { ["settlementId"] = "court_capital", ["kingdomId"] = "court_realm" } };
            var payload = new Dictionary<string, object> { ["campaignId"] = campaignId, ["timelineId"] = "main",
                ["conversationMode"] = "court_life", ["worldDay"] = 50d, ["courtLifeContext"] = court,
                ["nativePoliticalContext"] = native, ["hero"] = observer, ["playerIdentity"] = subject,
                ["playerHeroStringId"] = "court_ruler", ["conversationSessionId"] = "formal_court_test" };
            add("court_audience_exact_native_context", HasQualifiedCourtAudienceIdentity(payload, observer, subject),
                "An actual foreign envoy at the ruler's formal capital audience knows whom the audience presents.", null);
            foreach (var invalid in new[] { "speaker", "capital", "realm", "timeline", "office", "native_office", "native_authority", "mode", "freshness", "missing_context" })
            {
                var changed = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(payload));
                var changedCourt = ReadDictionary(changed, "courtLifeContext");
                if (invalid == "speaker") changedCourt["activeSpeakerHeroId"] = "another_guest";
                if (invalid == "capital") changedCourt["hostSettlementStringId"] = "another_town";
                if (invalid == "realm") changedCourt["playerKingdomStringId"] = "conqueror";
                if (invalid == "timeline") changedCourt["timelineId"] = "other_branch";
                if (invalid == "office") changedCourt["playerIsKingdomRuler"] = false;
                if (invalid == "native_office")
                {
                    var changedNative = ReadDictionary(changed, "nativePoliticalContext");
                    var changedSubject = ReadDictionary(changedNative, "subject");
                    changedSubject["isRuler"] = false; changedNative["subject"] = changedSubject;
                    changed["nativePoliticalContext"] = changedNative;
                }
                if (invalid == "native_authority")
                {
                    var changedNative = ReadDictionary(changed, "nativePoliticalContext");
                    changedNative["authoritative"] = false; changed["nativePoliticalContext"] = changedNative;
                }
                if (invalid == "mode") changed["conversationMode"] = "in_person";
                if (invalid == "freshness") changed["worldDay"] = 51d;
                changed["courtLifeContext"] = changedCourt;
                if (invalid == "missing_context") changed.Remove("courtLifeContext");
                add("court_audience_rejects_" + invalid, !HasQualifiedCourtAudienceIdentity(changed, observer, subject),
                    "Unqualified court claims preserve ordinary unknown-identity rules.", null);
            }
            // Exercise the real prompt adapter and persisted transition from an
            // unknown encounter, not just the eligibility predicate.
            using (var connection = OpenCampaignConnection(campaignId))
            {
                EnsureIdentitySchema(connection);
                InsertUnknownAcquaintance(connection, "court_envoy", "court_ruler", "Court Ruler", "ordinary_visit", 49d,
                    new Dictionary<string, object> { ["mode"] = "dialogue", ["subject"] = subject });
            }
            var recognized = ResolvePromptIdentity(payload, "court_envoy", "dialogue", "formal_court_test");
            using (var connection = OpenCampaignConnection(campaignId))
            {
                var saved = ReadAcquaintance(connection, "court_envoy", "court_ruler");
                add("court_audience_identity_persists_through_prompt_adapter", ReadBool(recognized, "canonicalNameAllowed", false)
                    && ReadString(recognized, "usableName", "") == "Court Ruler" && IdentityStateVerified(saved)
                    && ReadString(saved, "verification_source", "") == "formal_court_audience",
                    "Formal presentation upgrades the exact encountered pair and survives a fresh database connection.", recognized);
            }
        }
    }
}
