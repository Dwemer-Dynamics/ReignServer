using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        internal static bool IsQualifyingFavorConversationTurn(Dictionary<string, object> turn)
        {
            var payload = TryParseJsonObject(ReadString(turn, "payload_json", "{}"));
            if (ReadString(payload, "mode", "") != "court_life" && ReadString(payload, "conversationMode", "") != "court_life") return true;
            return ReadBool(payload, "actualPlayerTurn", false) && ReadString(payload, "courtLifePhase", "") == "conversation";
        }
        internal static void EnsureRulerFavorContactSchema(ReignDbConnection connection)
        {
            ReputationSchema.CreateCourtRulerFavorContact(connection);
            ReputationSchema.CreateCourtRulerFavorContactReceipts(connection);
        }

        internal static void RecordRulerFavorContact(ReignDbConnection connection, string campaignId, string timelineId,
            string firstId, string secondId, double day, string contactId)
        {
            if (string.IsNullOrWhiteSpace(contactId) || firstId == secondId) return;
            EnsureSocialReputationSchema(connection);
            ExpireRulerFavorContact(connection, campaignId, timelineId, day);
            foreach (string rulerId in new[] { firstId, secondId })
            {
                string favoriteId = rulerId == firstId ? secondId : firstId;
                var ruler = CourtConversationParticipant(connection, rulerId);
                if (ruler == null || !ReadBool(ruler, "isRuler", false)) continue;
                var receipt = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["contact"] = contactId };
                if (ReputationStore.SelectCourtRulerFavorContactReceipts(connection, receipt).Any()) continue;
                ReputationStore.InsertOrIgnoreCourtRulerFavorContactReceipts(connection, receipt);
                ReputationStore.UpsertCourtRulerFavorContact(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["contact"] = contactId });
            }
        }

        private static List<Dictionary<string, object>> ActiveRulerFavorRows(ReignDbConnection connection, string campaignId, string timelineId, double day)
        {
            return ReputationStore.SelectCharacterReputations10(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day });
        }

        private const int RulerFavorContactLookupBatchSize = 256;

        private static Dictionary<(string RulerId, string FavoriteId), Dictionary<string, object>> InitializeRulerFavorContacts(
            ReignDbConnection connection, string campaignId, string timelineId, double day,
            List<Dictionary<string, object>> tags)
        {
            // PostgreSQL's text keys are case sensitive. Dedupe only lookup keys;
            // the original tag order and repeated expiry writes remain authoritative.
            var keys = tags.Select(tag => (RulerId: ReadString(tag, "subject_id", ""),
                    FavoriteId: ReadString(TryParseJsonObject(ReadString(tag, "snapshot_json", "{}")), "linkedHeroId", "")))
                .Where(key => !string.IsNullOrWhiteSpace(key.FavoriteId)).Distinct()
                .OrderBy(key => key.RulerId, StringComparer.Ordinal)
                .ThenBy(key => key.FavoriteId, StringComparer.Ordinal).ToList();
            var contacts = new Dictionary<(string RulerId, string FavoriteId), Dictionary<string, object>>();
            for (int offset = 0; offset < keys.Count; offset += RulerFavorContactLookupBatchSize)
            {
                var batch = keys.Skip(offset).Take(RulerFavorContactLookupBatchSize)
                    .Select(key => new Dictionary<string, object> {
                        ["ruler_id"] = key.RulerId, ["favorite_id"] = key.FavoriteId }).ToList();
                var parameters = new Dictionary<string, object> {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = day, ["keys"] = Json.Serialize(batch) };
                // Expiring one pair never changes another pair's exchange anchor.
                // Initialize all anchors before expiry writes, then retain the
                // existing per-tag effects, including duplicate-pair tags.
                ReputationStore.InsertOrIgnoreCourtRulerFavorContact(connection, parameters);
                foreach (var contact in ReputationStore.SelectCourtRulerFavorContact(connection, parameters))
                    contacts.Add((ReadString(contact, "ruler_id", ""), ReadString(contact, "favorite_id", "")), contact);
            }
            return contacts;
        }

        private static List<string> ExpireRulerFavorContact(ReignDbConnection connection, string campaignId, string timelineId, double day)
        {
            EnsureRulerFavorContactSchema(connection);
            var changed = new List<string>();
            var tags = ActiveRulerFavorRows(connection, campaignId, timelineId, day);
            var contacts = InitializeRulerFavorContacts(connection, campaignId, timelineId, day, tags);
            foreach (var tag in tags)
            {
                var snapshot = TryParseJsonObject(ReadString(tag, "snapshot_json", "{}"));
                string rulerId = ReadString(tag, "subject_id", ""), favoriteId = ReadString(snapshot, "linkedHeroId", "");
                if (string.IsNullOrWhiteSpace(favoriteId)) continue;
                var p = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = rulerId, ["favorite"] = favoriteId, ["day"] = day, ["tag"] = ReadString(tag, "tag_id", ""),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                if (!contacts.TryGetValue((rulerId, favoriteId), out var contact))
                    throw new InvalidOperationException("Ruler-favor contact initialization returned no matching row.");
                object contactDay;
                double anchor = contact.TryGetValue("last_contact_day", out contactDay) && contactDay != null && contactDay != DBNull.Value
                    ? ReadDouble(contact, "last_contact_day", day) : ReadDouble(contact, "grace_day", day);
                if (!ReignFamilyVisitRules.FavorExpired(anchor, day)) continue;
                ReputationStore.UpdateCharacterReputations8(connection, p);
                ReputationStore.UpdateRumorSubjectTags3(connection, p);
                ReputationStore.UpdateRumorOccurrences2(connection, p);
                ReputationStore.UpdateCourtRulerFavorContact(connection, p);
                ReputationStore.UpdateCourtPlayerFavoringExchanges(connection, p);
                changed.Add(rulerId);
            }
            var subjects = changed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (subjects.Count > 0) RecomputePublicStandingForSubjects(connection, campaignId, timelineId, subjects, day);
            return subjects;
        }

        internal static bool RulerFavorRequiresFreshContact(ReignDbConnection connection, string campaignId, string timelineId,
            string rulerId, string favoriteId)
        {
            EnsureRulerFavorContactSchema(connection);
            return ReputationStore.SelectCourtRulerFavorContact2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ruler"] = rulerId, ["favorite"] = favoriteId }).Any();
        }

        internal static List<string> CurrentRulerFavorites(ReignDbConnection connection, string campaignId, string timelineId, string rulerId, double day)
        {
            return ActiveRulerFavorRows(connection, campaignId, timelineId, day)
                .Where(row => ReadString(row, "subject_id", "").Equals(rulerId, StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}")), "linkedHeroId", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static void RecordCompletedFavorCorrespondence(ReignDbConnection connection, string campaignId,
            string timelineId, Dictionary<string, object> reply, double day)
        {
            string parentId = ReadString(reply, "parent_letter_id", "");
            string sender = ReadString(reply, "sender_id", ""), recipient = ReadString(reply, "recipient_id", "");
            if (string.IsNullOrWhiteSpace(parentId) || string.IsNullOrWhiteSpace(ReadString(reply, "body", "")) || !IsPersonalFavorLetter(reply)) return;
            var parent = ReputationStore.SelectLetters(connection, new Dictionary<string, object> { ["id"] = parentId, ["sender"] = sender, ["recipient"] = recipient }).FirstOrDefault();
            if (parent == null || string.IsNullOrWhiteSpace(ReadString(parent, "body", "")) || !IsPersonalFavorLetter(parent)) return;
            RecordRulerFavorContact(connection, campaignId, timelineId, sender, recipient, day,
                "correspondence:" + ReadString(reply, "letter_id", ""));
        }

        internal static bool IsPersonalFavorLetter(Dictionary<string, object> letter)
        {
            string source = ReadString(letter, "source", "");
            return source == "player" || source == "npc_reply" || source == "npc_initiated" || source == "relationship_development";
        }
        private static string SharedFavorJealousyContext(ReignDbConnection connection, string campaignId, string timelineId,
            string observerId, string subjectId, double day)
        {
            var byRuler = ActiveRulerFavorRows(connection, campaignId, timelineId, day).GroupBy(row => ReadString(row, "subject_id", ""));
            foreach (var group in byRuler)
            {
                var favorites = group.Select(row => ReadString(TryParseJsonObject(ReadString(row, "snapshot_json", "{}")), "linkedHeroId", "")).ToList();
                if (favorites.Contains(observerId) && favorites.Contains(subjectId)
                    && CharactersService.IdentityStateVerified(CharactersService.ReadAcquaintance(connection, observerId, group.Key)))
                    return "You and this person both currently receive the same ruler's favor. You have less reason to resent lack of attention. Moderate attention-based jealousy while preserving other motives and your temperament; favor is not evidence of an affair.";
            }
            return "";
        }
    }
}
