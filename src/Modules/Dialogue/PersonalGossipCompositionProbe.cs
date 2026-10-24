using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // This never retries, reserves or publishes the live delivery job. Provider
        // calls run outside the campaign lock, using a detached frozen source.
        private static Dictionary<string, object> PersonalGossipCompositionProbe(string campaign, string timeline,
            Dictionary<string, object> request, Dictionary<string, object> test)
        {
            string message = ReadString(test, "messageId", ""), actor = ReadString(test, "actorHeroId", "");
            Dictionary<string, object> data;
            string root, topic, before;
            using (EnterCampaignWorkLock(GossipGate(campaign)))
            using (var c = OpenCampaignConnection(campaign))
            {
                if (ActiveWorldHistoryTimeline(c) != timeline) throw new InvalidOperationException("The active timeline changed.");
                var row = QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE message_id=$id AND timeline_id=$timeline;",
                    GossipFields("id", message, "timeline", timeline)).SingleOrDefault();
                data = PersonalGossipProbeSource(row, actor, timeline);
                root = ReadString(row, "root_id", ""); topic = ReadString(row, "topic_id", "");
                before = PersonalGossipProbeState(c, timeline, root, topic);
            }
            var proposal = PersonalGossipProbeDraft(data, ChatWithLlm);
            if (!GossipTestAuthorized(request)) throw new InvalidOperationException("The exact probe lease or native scope changed.");
            using (EnterCampaignWorkLock(GossipGate(campaign)))
            using (var c = OpenCampaignConnection(campaign))
            {
                if (ActiveWorldHistoryTimeline(c) != timeline || before != PersonalGossipProbeState(c, timeline, root, topic))
                    throw new InvalidOperationException("The frozen gossip state changed during the probe; no proposal may be credited.");
            }
            return GossipFields("ok", true, "schema", "reign-personal-gossip-native-evidence-v1",
                "origin", "operator_composition_probe", "caseId", ReadString(test, "caseId", ""),
                "phase", "composition_probe", "campaignId", campaign, "timelineId", timeline,
                "worldDay", ReadDouble(request, "worldDay", 0), "activeSaveName", ReadString(request, "activeSaveName", ""),
                "messageId", message, "rootId", root, "recipientId", ReadString(data, "recipientId", ""),
                "compositionProbe", proposal, "gossipStateUnchanged", true, "gossipStateHash", PromptHash(before),
                "coverage", "One shadow proposal from a terminal original job. No scheduling, delivery, history, relationship, draw or live-attempt changes. Not native delivery acceptance.");
        }

        private static Dictionary<string, object> PersonalGossipProbeSource(Dictionary<string, object> row, string actor, string timeline)
        {
            if (row == null || ReadString(row, "message_id", "").Length == 0 || ReadString(row, "topic_id", "").Length == 0
                || ReadString(row, "root_id", "") != ReadString(row, "topic_id", "")
                || ReadString(row, "timeline_id", "") != timeline || ReadString(row, "speaker_id", "") != actor
                || ReadString(row, "status", "") != "generation_failed" || ReadInt(row, "is_reply", 1) != 0 || ReadInt(row, "hop", 0) != 1)
                throw new InvalidOperationException("Composition probes require an exact actor-owned terminal original job on this timeline.");
            var data = TryParseJsonObject(ReadString(row, "payload_json", ""));
            if (data == null || ReadString(data, "id", "") != ReadString(row, "topic_id", "")
                || ReadString(data, "rootId", "") != ReadString(row, "root_id", "")
                || ReadString(data, "speakerId", "") != actor || ReadString(data, "timelineId", "") != timeline
                || ReadString(data, "recipientId", "") != ReadString(row, "recipient_id", "")
                || ReadString(data, "playerId", "").Length == 0 || ReadString(data, "playerId", "") != ReadString(row, "player_id", "")
                || ReadString(data, "type", "") != "negative" || ReadBool(data, "reply", false) || ReadInt(data, "hop", 0) != 1
                || !new[] { "loaded_exaggeration", "false_escalation", "invented_detail" }.Contains(ReadString(data, "distortion", ""))
                || ReadDictionaryList(data, "sourceEncounterTurns").Count != 2 || ReadStringList(data, "sourceTurnIds").Distinct().Count() != 2
                || ReadString(data, "sourceQuote", "").Length == 0
                || ReadString(ReadDictionary(data, "operatorTestControls"), "compositionMode", "production") != "production"
                || PersonalGossipOriginalSourceFrame(data) == null)
                throw new InvalidOperationException("A probe requires the unchanged malicious permission, recipient and two original accepted source turns; replies, retells and fault controls are refused.");
            return data;
        }

        private static string PersonalGossipProbeState(ReignDbConnection c, string timeline, string root, string topic)
        {
            var args = GossipFields("timeline", timeline, "root", root, "topic", topic);
            return CanonicalJson(GossipFields(
                "topics", QuerySql(c, "SELECT * FROM personal_gossip_topics WHERE timeline_id=$timeline AND topic_id=$topic ORDER BY topic_id;", args),
                "messages", QuerySql(c, "SELECT * FROM personal_gossip_messages WHERE timeline_id=$timeline AND root_id=$root ORDER BY message_id;", args),
                "history", QuerySql(c, "SELECT * FROM personal_gossip_history WHERE timeline_id=$timeline AND root_id=$root ORDER BY receipt_id;", args)));
        }

        private static Dictionary<string, object> PersonalGossipProbeDraft(Dictionary<string, object> source,
            Func<Dictionary<string, object>, Dictionary<string, object>> respond)
        {
            string frozen = CanonicalJson(source);
            var detached = TryParseJsonObject(Json.Serialize(source));
            Dictionary<string, object> written = null;
            int calls = 0;
            var result = ComposePersonalGossip(detached, false, request => {
                if (++calls > 2) throw new InvalidOperationException("A composition probe allows one writer and at most one review.");
                var response = respond(request);
                if (ReadString(request, "requestType", "") == "personal_gossip")
                    written = TryParseJsonObject(ReadString(response, "content", ""));
                return response;
            });
            var review = result.TryGetValue(GossipCompositionApprovalKey, out object token) && token is GossipCompositionApproval approval
                ? approval.Evidence : ReadDictionary(result, "compositionReview");
            bool valid = ValidatePersonalGossipComposition(null, detached, result, false);
            bool unchanged = frozen == CanonicalJson(source) && frozen == CanonicalJson(detached);
            return GossipFields("schema", "reign-gossip-composition-probe-v1", "accepted", valid && unchanged,
                "providerCalls", calls, "sourceUnchanged", unchanged, "sourceHash", PromptHash(frozen),
                "proposedBody", ReadString(written, "body", ""), "validatedBody", valid ? ReadString(result, "body", "") : "",
                "sourceQuote", ReadString(source, "sourceQuote", ""), "severity", ReadString(source, "distortion", ""),
                "compositionReview", review, "error", ReadString(result, "error", ""), "liveAttemptsConsumed", 0,
                "recipientId", ReadString(source, "recipientId", ""), "policyVersion", ReadString(source, "policyVersion", ""),
                "route", ReadDictionary(source, "route"));
        }
    }
}
