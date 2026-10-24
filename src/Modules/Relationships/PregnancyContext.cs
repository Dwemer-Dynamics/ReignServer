using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Reign.Core.Contracts.Family;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsurePregnancyContextSchema(ReignDbConnection c)
        {
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS conception_contexts (
timeline_id TEXT NOT NULL,pregnancy_id TEXT NOT NULL,facts_json TEXT NOT NULL,world_day DOUBLE PRECISION NOT NULL,
PRIMARY KEY(timeline_id,pregnancy_id));
CREATE TABLE IF NOT EXISTS conception_knowledge (
timeline_id TEXT NOT NULL,pregnancy_id TEXT NOT NULL,observer_id TEXT NOT NULL,
knowledge_json TEXT NOT NULL,source_id TEXT NOT NULL,source_hash TEXT NOT NULL,world_day DOUBLE PRECISION NOT NULL,
PRIMARY KEY(timeline_id,pregnancy_id,observer_id));");
        }
        private static PregnancyFacts PregnancyFactsFrom(Dictionary<string, object> value) =>
            Json.Deserialize<PregnancyFacts>(Json.Serialize(value)) ?? new PregnancyFacts();
        private static void BindRecoveredPregnancyKnowledge(ReignDbConnection c, string requested, string recovered,
            long sequence, string head)
        {
            // The restore context exists only after restoring the exact Save Sync snapshot.
            // Never inherit discoveries solely because two timelines have related names.
            var restored = RestoredLetterContext(c, recovered);
            if (restored == null || ReadLong(restored, "head_sequence", -1) != sequence
                || ReadString(restored, "head_event_id", "") != head || requested == recovered) return;
            EnsurePregnancyContextSchema(c);
            var args = TestDict("old", requested, "new", recovered, "day", ReadDouble(restored, "world_day", -1));
            ExecuteSql(c, @"INSERT INTO conception_contexts(timeline_id,pregnancy_id,facts_json,world_day)
SELECT $new,pregnancy_id,facts_json,world_day FROM conception_contexts WHERE timeline_id=$old AND world_day<=$day
ON CONFLICT(timeline_id,pregnancy_id) DO NOTHING;
INSERT INTO conception_knowledge(timeline_id,pregnancy_id,observer_id,knowledge_json,source_id,source_hash,world_day)
SELECT $new,pregnancy_id,observer_id,knowledge_json,source_id,source_hash,world_day FROM conception_knowledge
WHERE timeline_id=$old AND world_day<=$day ON CONFLICT(timeline_id,pregnancy_id,observer_id) DO NOTHING;", args);
        }
        private static PregnancyKnowledge ReadPregnancyKnowledge(ReignDbConnection c, string timeline, string id, string observer, double asOfDay = double.MaxValue)
        {
            var row = QuerySql(c, "SELECT knowledge_json FROM conception_knowledge WHERE timeline_id=$timeline AND pregnancy_id=$id AND observer_id=$observer AND world_day<=$day;",
                TestDict("timeline", timeline, "id", id, "observer", observer, "day", asOfDay)).FirstOrDefault();
            return row == null ? new PregnancyKnowledge() : Json.Deserialize<PregnancyKnowledge>(ReadString(row, "knowledge_json", "{}")) ?? new PregnancyKnowledge();
        }
        private static void GrantPregnancyKnowledge(ReignDbConnection c, string timeline, PregnancyFacts facts, string observer,
            bool illicit, bool father, string sourceId, string sourceHash, double day)
        {
            if (string.IsNullOrEmpty(observer) || string.IsNullOrEmpty(sourceId)) return;
            var known = ReadPregnancyKnowledge(c, timeline, facts.PregnancyId, observer);
            known.Pregnancy = true;
            known.Illicit |= illicit && facts.Illicit;
            known.Paternity |= father && facts.BiologicalFatherId.Length > 0;
            known.EvidenceId = sourceId;
            ExecuteSql(c, @"INSERT INTO conception_knowledge(timeline_id,pregnancy_id,observer_id,knowledge_json,source_id,source_hash,world_day)
VALUES($timeline,$id,$observer,$json,$source,$hash,$day) ON CONFLICT(timeline_id,pregnancy_id,observer_id) DO UPDATE SET
knowledge_json=$json,source_id=$source,source_hash=$hash,world_day=$day;",
                TestDict("timeline", timeline, "id", facts.PregnancyId, "observer", observer,
                    "json", Json.Serialize(known), "source", sourceId, "hash", sourceHash, "day", day));
        }
        private static Dictionary<string, object> FamilyPregnancySyncApi(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", ""), timeline = ReadString(payload, "timelineId", "");
            var snapshots = ReadDictionaryList(payload, "pregnancies");
            double day = ReadDouble(payload, "worldDay", -1);
            if (campaign.Length == 0 || timeline.Length == 0 || snapshots.Count > 256 || !double.IsFinite(day) || day < 0)
                return TestDict("ok", false, "error", "A bounded native pregnancy snapshot and campaign timeline are required.");
            lock (CampaignRelationshipWriteLock(campaign))
            using (var c = OpenCampaignConnection(campaign))
            {
                EnsurePregnancyContextSchema(c);
                EnsureWorldHistorySchema(c);
                if (ActiveWorldHistoryTimeline(c) != timeline) return TestDict("ok", false, "error", "Pregnancy snapshot belongs to another timeline.");
                var facts = snapshots.Select(PregnancyFactsFrom).ToList();
                if (facts.Any(f => f.PregnancyId.Length == 0 || f.PregnancyId.Length > 256 || f.MotherId.Length == 0
                    || f.MotherId.Length > 256 || f.WorldDay != day || !f.Active)
                    || facts.Select(f => f.PregnancyId).Distinct().Count() != facts.Count)
                    return TestDict("ok", false, "error", "Pregnancy snapshot identity or native time is invalid.");
                var observers = ReadStringList(payload, "observers").Where(id => id.Length > 0 && id.Length <= 256).Distinct().Take(65).ToList();
                var present = ReadBool(payload, "physicalConversation", false) ? ReadStringList(payload, "presentMotherIds") : new List<string>();
                using var transaction = c.BeginTransaction();
                ExecuteSql(c, @"DELETE FROM world_history_knowledge_rules WHERE acquisition_mode='realm_news'
AND event_id IN (SELECT event_id FROM world_history_events WHERE event_type='child_conceived' AND timeline_id=$timeline);",
                    TestDict("timeline", timeline));
                foreach (var old in QuerySql(c, "SELECT pregnancy_id,facts_json FROM conception_contexts WHERE timeline_id=$timeline;", TestDict("timeline", timeline)))
                {
                    if (facts.Any(f => f.PregnancyId == ReadString(old, "pregnancy_id", ""))) continue;
                    var ended = Json.Deserialize<PregnancyFacts>(ReadString(old, "facts_json", "{}"));
                    if (ended == null || !ended.Active) continue;
                    ended.Active = false;
                    ExecuteSql(c, "UPDATE conception_contexts SET facts_json=$json,world_day=$day WHERE timeline_id=$timeline AND pregnancy_id=$id;",
                        TestDict("json", Json.Serialize(ended), "day", day, "timeline", timeline, "id", ended.PregnancyId));
                }
                var rows = new List<Dictionary<string, object>>();
                var views = new Dictionary<string, object>();
                foreach (var f in facts)
                {
                    ExecuteSql(c, @"INSERT INTO conception_contexts(timeline_id,pregnancy_id,facts_json,world_day)
VALUES($timeline,$id,$json,$day) ON CONFLICT(timeline_id,pregnancy_id) DO UPDATE SET facts_json=$json,world_day=$day;",
                        TestDict("timeline", timeline, "id", f.PregnancyId, "json", Json.Serialize(f), "day", day));
                    GrantPregnancyKnowledge(c, timeline, f, f.MotherId, f.Illicit, f.BiologicalFatherId.Length > 0,
                        "native_pregnancy:" + f.PregnancyId, PromptHash(Json.Serialize(f)), day);
                    foreach (string observer in observers)
                    {
                        if (f.MotherExposed || present.Contains(f.MotherId) && PregnancyContextPolicy.Visible(f))
                            GrantPregnancyKnowledge(c, timeline, f, observer,
                                f.MotherExposed || f.MarriageAtConceptionKnown && f.SpouseAtConceptionId.Length == 0,
                                f.FatherExposed, f.MotherExposed ? "pregnancy_exposure:" + f.PregnancyId : "native_visible_pregnancy:" + f.PregnancyId,
                                PromptHash(Json.Serialize(f)), day);
                    }
                    rows.Add(TestDict("pregnancyId", f.PregnancyId, "knowledge", observers.Concat(new[] { f.MotherId }).Distinct()
                        .ToDictionary(id => id, id => (object)ReadPregnancyKnowledge(c, timeline, f.PregnancyId, id))));
                    ApplyConfirmedPregnancyExposure(c, campaign, timeline, f, day);
                }
                foreach (string observer in observers)
                    views[observer] = string.Join("\n\n", facts.Select(f => observer == f.MotherId ? PregnancyContextPolicy.SelfPrompt(f)
                        : PregnancyContextPolicy.ObserverPrompt(f, ReadPregnancyKnowledge(c, timeline, f.PregnancyId, observer), present.Contains(f.MotherId)))
                        .Where(text => text.Length > 0).Distinct());
                transaction.Commit();
                return TestDict("ok", true, "timelineId", timeline, "knowledge", rows, "observerViews", views);
            }
        }
        private static string BuildPregnancyPrompt(Dictionary<string, object> profile)
        {
            string projected = ReadString(profile, "pregnancyPrompt", "");
            if (projected.Length > 0) return projected + "\n" + PregnancyDisclosureContract;
            string id = ReadFirstString(profile, "heroStringId", "id");
            var self = ReadDictionaryList(profile, "pregnancyContexts").Select(PregnancyFactsFrom).FirstOrDefault(f => f.MotherId == id && f.Active);
            if (self == null && ReadBool(profile, "isPregnant", false)) self = new PregnancyFacts { Active = true,
                PregnancyId = "legacy_flag:" + id, MotherId = id, MotherName = ReadString(profile, "name", id),
                SpouseId = ReadString(profile, "spouseId", ""), SpouseName = ReadString(profile, "spouseName", ""), Source = "native_flag" };
            return self == null ? "" : PregnancyContextPolicy.SelfPrompt(self) + "\n" + PregnancyDisclosureContract;
        }
        private const string PregnancyDisclosureContract = "If you actually disclose a confirmed pregnancy fact in your accepted spoken reply or letter, return pregnancyDisclosures: [{pregnancyId, kind: pregnancy|illicit|paternity, evidenceQuote}]. "
            + "Quote the exact affirmative spoken words. Omit this collection for silence, denial, speculation, concealed thoughts or a false paternity claim. Each fact and its audience are separate. "
            + "Disclosing paternity requires naming the biological father or explicitly acknowledging the listener as father. Private speaker context is never another speaker's knowledge.";

        private static string BuildCurrentPregnancyPrompt(string campaign, string timeline, string observer, double day,
            Dictionary<string, object> profile)
        {
            using var c = OpenCampaignConnection(campaign);
            if (!TableExists(c, "conception_contexts")) return BuildPregnancyPrompt(profile);
            var records = QuerySql(c, "SELECT facts_json FROM conception_contexts WHERE timeline_id=$timeline AND world_day<=$day;", TestDict("timeline", timeline, "day", day));
            if (records.Count == 0) return BuildPregnancyPrompt(profile);
            var parts = new List<string>();
            foreach (var row in records)
            {
                var facts = Json.Deserialize<PregnancyFacts>(ReadString(row, "facts_json", "{}"));
                if (facts == null || !facts.Active) continue;
                facts.WorldDay = day;
                var known = ReadPregnancyKnowledge(c, timeline, facts.PregnancyId, observer, day);
                if (facts.MotherId == observer) parts.Add(PregnancyContextPolicy.SelfPrompt(facts));
                else if (known.Pregnancy || facts.MotherExposed) parts.Add(PregnancyContextPolicy.ObserverPrompt(facts, known, false));
            }
            return parts.Count == 0 ? "" : string.Join("\n\n", parts) + "\n" + PregnancyDisclosureContract;
        }
        private static void ApplyConfirmedPregnancyExposure(ReignDbConnection c, string campaign, string timeline, PregnancyFacts f, double day)
        {
            if (!f.MotherExposed || !f.Illicit) return;
            EnsureSocialReputationSchema(c);
            foreach (var parent in new[] { TestDict("id", f.MotherId, "value", -50), TestDict("id", f.FatherExposed ? f.BiologicalFatherId : "", "value", -5) })
            {
                string subject = ReadString(parent, "id", "");
                if (subject.Length == 0) continue;
                string eventId = f.PregnancyId + ":exposed:" + (subject == f.MotherId ? "mother" : "father");
                if (QuerySql(c, "SELECT lifecycle_id FROM reputation_lifecycle_events WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND source_event_id=$event LIMIT 1;",
                    TestDict("campaign", campaign, "timeline", timeline, "subject", subject, "event", eventId)).Count > 0) continue;
                var evidence = TestDict("pregnancyId", f.PregnancyId, "motherId", f.MotherId, "illicit", true,
                    "fatherNamed", f.FatherExposed, "biologicalFatherId", f.FatherExposed ? f.BiologicalFatherId : "");
                ApplyCourtDynamicSocialOutcome(c, campaign, timeline, subject,
                    TestDict("dynamicReputationTagId", "the_unchaste", "dynamicActive", true, "dynamicValue", ReadInt(parent, "value", -5),
                        "directReputationProducer", "pregnancy_exposure", "dynamicEvidence", evidence,
                        "dynamicDescription", subject == f.MotherId ? "Her publicly exposed illicit pregnancy is a severe court scandal." : "He is the publicly identified biological father of an illicit pregnancy."),
                    eventId, day, out _);
                ReconcileSocialRelationshipsForSubjects(c, campaign, timeline, new[] { subject }, day);
            }
        }

        private static void AcceptPregnancyDisclosures(ReignDbConnection c, string timeline, string speaker,
            string reply, Dictionary<string, object> parsed, IEnumerable<string> audience, string sourceId, double day)
        {
            var disclosures = ReadDictionaryList(parsed, "pregnancyDisclosures");
            if (disclosures.Count == 0) return;
            EnsurePregnancyContextSchema(c);
            foreach (var disclosure in disclosures.Take(6))
            {
                string id = ReadString(disclosure, "pregnancyId", ""), kind = ReadString(disclosure, "kind", ""), quote = ReadString(disclosure, "evidenceQuote", "");
                var row = QuerySql(c, "SELECT facts_json FROM conception_contexts WHERE timeline_id=$timeline AND pregnancy_id=$id;", TestDict("timeline", timeline, "id", id)).FirstOrDefault();
                var f = row == null ? null : Json.Deserialize<PregnancyFacts>(ReadString(row, "facts_json", "{}"));
                if (f == null || !f.Active || quote.Length < 5 || quote.Length > 2000 || !reply.Contains(quote, StringComparison.Ordinal)
                    || quote.Contains('*') || quote.Contains('?') || Regex.IsMatch(quote, @"\b(never|might|maybe|perhaps|if|rumou?r|suspect|guess|suppose|could|would)\b", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(quote, @"\b(?:not|isn't|aren't|no)\s+(?:an?\s+|actually\s+)?(?:pregnant|expecting|with child|illicit|unmarried|unwed|out of wedlock|outside (?:my |the )?marriage)\b", RegexOptions.IgnoreCase)
                    || kind != "illicit" && Regex.IsMatch(quote, @"\bnot\b|isn't|aren't|am not", RegexOptions.IgnoreCase)) continue;
                var knows = ReadPregnancyKnowledge(c, timeline, id, speaker);
                if (!knows.Pregnancy || kind == "illicit" && (!knows.Illicit || !f.Illicit)
                    || kind == "paternity" && !knows.Paternity) continue;
                bool pregnancy = Regex.IsMatch(quote, @"\b(pregnant|expecting|carrying|with child)\b", RegexOptions.IgnoreCase);
                bool illicit = kind == "illicit" && (Regex.IsMatch(quote, @"\b(illicit|out of wedlock|outside (?:my |the )?marriage)\b", RegexOptions.IgnoreCase)
                    || (f.SpouseId.Length == 0 || f.MarriageAtConceptionKnown && f.SpouseAtConceptionId.Length == 0)
                        && Regex.IsMatch(quote, @"\b(unmarried|unwed)\b", RegexOptions.IgnoreCase)
                    || PregnancyContextPolicy.FatherMismatch(f)
                        && Regex.IsMatch(quote, @"\b(not my husband|isn't my husband's|is not my husband's)\b", RegexOptions.IgnoreCase));
                string fatherName = Regex.Escape(f.BiologicalFatherName);
                bool namedFather = f.BiologicalFatherName.Length > 0 && Regex.IsMatch(quote,
                    @"\b" + fatherName + @"\s+(?:is|was)\s+(?:the\s+)?father\b|\bfather\s+(?:is|was)\s+" + fatherName + @"\b|"
                    + fatherName + @"['’]s\s+(?:child|baby)\b|\b(?:pregnant|child|baby)\s+(?:by|with)\s+" + fatherName + @"\b|"
                    + fatherName + @"\s+(?:got|made)\s+me\s+pregnant\b", RegexOptions.IgnoreCase);
                bool father = kind == "paternity" && (namedFather
                        || f.BiologicalFatherId == speaker && Regex.IsMatch(quote, @"\b(?:(?:I am|I'm) (?:the )?father|my child)\b", RegexOptions.IgnoreCase)
                        || audience.Where(id => id != speaker).Distinct().SequenceEqual(new[] { f.BiologicalFatherId })
                            && Regex.IsMatch(quote, @"\b(?:(?:you are|you're) (?:the )?father|your child|yours)\b", RegexOptions.IgnoreCase));
                if (!pregnancy && !illicit && !father) continue;
                foreach (string observer in audience.Distinct())
                    GrantPregnancyKnowledge(c, timeline, f, observer, illicit || father && f.Illicit, father,
                        sourceId, PromptHash(quote), day);
            }
        }
    }
}
