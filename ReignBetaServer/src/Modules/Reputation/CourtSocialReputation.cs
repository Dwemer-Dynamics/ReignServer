using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] PositiveCourtPopularityTags =
        {
            "well_regarded", "court_favorite", "beloved_of_the_court"
        };

        private static readonly string[] NegativeCourtPopularityTags =
        {
            "ill_regarded", "shunned_at_court", "court_pariah"
        };

        private static readonly string[] NobleJudgmentDirectTagIds =
        {
            "incompetent", "cruel", "dishonorable", "divorcee", "disloyal",
            "promiscuous", "the_unchaste", "corrupt", "coward", "traitor",
            "murderous", "murderer", "convicted_murderer"
        };

        private static void EnsureCourtSocialReputationSchema(ReignDbConnection connection)
        {
            EnsureRulerFavorContactSchema(connection);
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_dynamic_reputation_metrics (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,domain TEXT NOT NULL,
positive_count INTEGER NOT NULL DEFAULT 0,negative_count INTEGER NOT NULL DEFAULT 0,
dynamic_value INTEGER NOT NULL DEFAULT 0,evidence_json TEXT NOT NULL DEFAULT '{}',
calculated_day REAL NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,domain));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_conversation_signal_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,session_id TEXT NOT NULL,signal_key TEXT NOT NULL,
signal_type TEXT NOT NULL,speaker_id TEXT NOT NULL,target_id TEXT NOT NULL,
result_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,session_id,signal_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_social_signal_evidence (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,exchange_id TEXT NOT NULL,signal_id TEXT NOT NULL,
session_id TEXT NOT NULL DEFAULT '',signal_type TEXT NOT NULL,speaker_id TEXT NOT NULL DEFAULT '',
target_id TEXT NOT NULL DEFAULT '',supporting_quote TEXT NOT NULL DEFAULT '',accepted INTEGER NOT NULL DEFAULT 0,
rejection_reason TEXT NOT NULL DEFAULT '',evidence_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,exchange_id,signal_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_player_favoring_exchanges (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
exchange_count INTEGER NOT NULL DEFAULT 0,last_exchange_id TEXT NOT NULL DEFAULT '',last_world_day REAL NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_player_favoring_exchange_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
exchange_id TEXT NOT NULL,world_day REAL NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id,exchange_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_intimacy_exposure_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,signal_id TEXT NOT NULL,player_id TEXT NOT NULL,
partner_id TEXT NOT NULL,eligible INTEGER NOT NULL DEFAULT 0,exposed INTEGER NOT NULL DEFAULT 0,
probability REAL NOT NULL DEFAULT 0,roll REAL NOT NULL DEFAULT 0,result_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,signal_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_flirt_targets (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,target_id TEXT NOT NULL,
first_session_id TEXT NOT NULL DEFAULT '',first_world_day REAL NOT NULL DEFAULT 0,
last_session_id TEXT NOT NULL DEFAULT '',last_world_day REAL NOT NULL DEFAULT 0,
incident_count INTEGER NOT NULL DEFAULT 1,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,target_id));");
            ExecuteSql(connection,
                "CREATE INDEX IF NOT EXISTS idx_court_flirt_subject ON court_flirt_targets(campaign_id,timeline_id,subject_id,last_world_day DESC);");
            if (!QuerySql(connection, "SELECT 1 FROM schema_meta WHERE key='ruler_favoring_supersession_v1' LIMIT 1;").Any())
            {
                ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='superseded'
WHERE status='active' AND (tag_id='favored_by' OR tag_id LIKE 'favored_by:%');");
                ExecuteSql(connection, @"UPDATE character_reputations SET status='superseded_by_ruler_favoring',updated_ts=$ts
WHERE status='active' AND (tag_id='favored_by' OR tag_id LIKE 'favored_by:%');",
                    new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                ExecuteSql(connection, @"INSERT INTO schema_meta(key,value) VALUES('ruler_favoring_supersession_v1','1')
ON CONFLICT(key) DO UPDATE SET value=excluded.value;");
            }
        }

        private static void AddCourtSocialCatalogEntries(
            List<object> tags,
            List<object> archetypes,
            Action<string, string, string, int, int, string, string, string, string[], bool> addTag,
            Action<string, string, string, double, string, string, string, bool> addArchetype)
        {
            Dictionary<string, object> legacyFavored = CourtCatalogTag(tags, "favored_by");
            if (legacyFavored != null)
            {
                legacyFavored["enabled"] = false;
                legacyFavored["manualEligible"] = false;
                legacyFavored["supersededBy"] = "ruler_favoring";
            }
            Dictionary<string, object> legacyFavoredProducer = archetypes.OfType<Dictionary<string, object>>()
                .FirstOrDefault(x => string.Equals(ReadString(x, "id", ""), "favored_by", StringComparison.OrdinalIgnoreCase));
            if (legacyFavoredProducer != null)
            {
                legacyFavoredProducer["enabled"] = false;
                legacyFavoredProducer["supersededBy"] = "ruler_favoring_presence";
            }
            Action<string, string, string, int, int, string, string, bool> courtTag =
                (id, label, description, rumor, reputation, discipline, polarity, derived) =>
                {
                    int before = tags.Count;
                    addTag(id, label, description, rumor, reputation, "court_personality",
                        discipline, polarity, Array.Empty<string>(), derived);
                    Dictionary<string, object> item = tags.OfType<Dictionary<string, object>>()
                        .FirstOrDefault(x => string.Equals(ReadString(x, "id", ""), id, StringComparison.OrdinalIgnoreCase));
                    if (item == null) return;
                    item["family"] = "court_personality";
                    item["discipline"] = discipline;
                    item["polarity"] = polarity;
                    item["derived"] = derived;
                    if (derived)
                    {
                        item["removable"] = false;
                        item["manualEligible"] = false;
                    }
                    if (tags.Count == before && !item.ContainsKey("manualEligible"))
                        item["manualEligible"] = !derived;
                };

            courtTag("well_regarded", "Well Regarded",
                "A substantial circle of nobles in the same kingdom holds this character in genuine regard.",
                0, 5, "popularity_positive", "positive", true);
            courtTag("court_favorite", "Court Favorite",
                "A broad circle of nobles in the same kingdom holds this character in genuine regard.",
                0, 10, "popularity_positive", "positive", true);
            courtTag("beloved_of_the_court", "Beloved of the Court",
                "An exceptional number of nobles in the same kingdom holds this character in genuine regard.",
                0, 15, "popularity_positive", "positive", true);
            courtTag("ill_regarded", "Ill Regarded",
                "A substantial circle of nobles in the same kingdom privately dislikes this character.",
                0, -5, "popularity_negative", "negative", true);
            courtTag("shunned_at_court", "Shunned at Court",
                "A broad circle of nobles in the same kingdom privately dislikes this character.",
                0, -10, "popularity_negative", "negative", true);
            courtTag("court_pariah", "Court Pariah",
                "An exceptional number of nobles in the same kingdom privately dislikes this character.",
                0, -15, "popularity_negative", "negative", true);

            courtTag("ruler_favoring", "Favors {linkedHeroName}",
                "It is said that this ruler grants {linkedHeroName} unusual attention and favor.",
                -5, -10, "ruler_favoring", "negative", false);
            Dictionary<string, object> favored = CourtCatalogTag(tags, "ruler_favoring");
            if (favored != null)
            {
                favored["parameterizedBy"] = "linkedHeroId";
                favored["manualEligible"] = false;
            }

            courtTag("attentive_lord", "Attentive Lord",
                "They are said to remain close to the towns beneath their authority, hearing local concerns and attending to their lands.",
                5, 15, "governing_presence", "positive", false);
            courtTag("absent_lord", "The Absent Lord",
                "They are said to spend so long away from their lands that stewards govern in their place and petitioners no longer expect to see them.",
                -10, -25, "governing_presence", "negative", false);

            courtTag("infertile", "Infertile",
                "Whispers claim that despite marriage and ample opportunity, they have failed to produce a child and may be incapable of securing their line.",
                -10, -20, "fertility", "negative", false);
            courtTag("dynasty_secure", "Dynasty Secure",
                "Their dynasty is supported by living and publicly acknowledged children.",
                0, 1, "dynasty", "positive", true);
            Dictionary<string, object> dynasty = CourtCatalogTag(tags, "dynasty_secure");
            if (dynasty != null)
            {
                dynasty["dynamicValue"] = true;
                dynasty["maximumDynamicValue"] = 50;
            }

            courtTag("flirt", "Flirt",
                "They have become known for offering romantic attention readily and with less restraint than courtly convention expects.",
                0, -5, "flirtation", "negative", false);
            Dictionary<string, object> flirt = CourtCatalogTag(tags, "flirt");
            if (flirt != null) flirt["producerGate"] = "validated_conversation_signal";

            courtTag("the_unchaste", "The Unchaste",
                "They are publicly associated with bringing a child into the world outside the bonds of marriage.",
                0, -5, "illegitimate_child", "negative", false);
            courtTag("dishonorable", "Dishonorable",
                "A public judgment found that they acted against the obligations of honor.",
                -5, -15, "noble_judgment", "negative", false);
            courtTag("corrupt", "Corrupt",
                "A public judgment tied them to abuse of office, bribery, or stolen public wealth.",
                -10, -25, "noble_judgment", "negative", false);
            courtTag("cruel", "Cruel",
                "A public judgment found that they inflicted needless suffering or abused those beneath their power.",
                -10, -25, "noble_judgment", "negative", false);
            courtTag("incompetent", "Incompetent",
                "A public judgment found their failures too serious to excuse as ordinary misfortune.",
                -5, -15, "noble_judgment", "negative", false);
            courtTag("traitor", "Traitor",
                "A public judgment found that they betrayed their sworn political or military allegiance.",
                -15, -40, "noble_judgment", "negative", false);
            courtTag("murderous", "Murderous",
                "A public judgment found them willing to use lethal violence outside lawful battle.",
                -15, -35, "noble_judgment", "negative", false);
            courtTag("murderer", "Murderer",
                "The court publicly holds them responsible for an unlawful killing.",
                -20, -45, "noble_judgment", "negative", false);
            courtTag("convicted_murderer", "Convicted Murderer",
                "They stand convicted of murder by a ruler's formal judgment.",
                0, -50, "noble_judgment", "negative", false);
            Dictionary<string, object> unchaste = CourtCatalogTag(tags, "the_unchaste");
            if (unchaste != null)
            {
                unchaste["genderVariants"] = new Dictionary<string, object>
                {
                    ["male"] = new Dictionary<string, object>
                    {
                        ["reputationValue"] = -5,
                        ["description"] = "Their fathering of a publicly known illegitimate child has become an enduring part of their reputation."
                    },
                    ["female"] = new Dictionary<string, object>
                    {
                        ["reputationValue"] = -50,
                        ["description"] = "Her bearing of a publicly known illegitimate child has become an enduring and severe court scandal."
                    }
                };
            }

            AddCourtArchetype(archetypes, addArchetype, "ruler_favoring_presence", "Ruler Favoring",
                "A ruler's prolonged daily proximity to one person gave rise to reports of unusual favor.",
                0.05d, "ruler_favoring", "ruler");
            AddCourtArchetype(archetypes, addArchetype, "ruler_favoring_dialogue", "Ruler Favoring",
                "A player ruler's repeated substantive private conversations with one person gave rise to reports of unusual favor.",
                0.10d, "ruler_favoring", "ruler");
            AddCourtArchetype(archetypes, addArchetype, "attentive_lord", "Attentive Lord",
                "Five consecutive days in eligible governed towns gave rise to praise for the character's attention.",
                0.10d, "attentive_lord", "governor");
            AddCourtArchetype(archetypes, addArchetype, "absent_lord", "The Absent Lord",
                "Fifteen consecutive days away from governed settlements gave rise to criticism of the character's absence.",
                0.10d, "absent_lord", "governor");
            AddCourtArchetype(archetypes, addArchetype, "infertile", "Infertile",
                "A childless marriage completed a season with meaningful opportunity but without conception or birth.",
                0.10d, "infertile", "spouse");
            AddCourtArchetype(archetypes, addArchetype, "flirt", "Flirt",
                "A validated explicit flirtatious exchange became known at court.",
                0.05d, "flirt", "speaker");
            AddCourtArchetype(archetypes, addArchetype, "promiscuous_flirtation", "Promiscuous",
                "A character already known as a flirt directed romantic attention toward another person.",
                0.05d, "promiscuous", "speaker");
            AddCourtArchetype(archetypes, addArchetype, "the_unchaste", "The Unchaste",
                "A publicly known illegitimate child continued to draw court attention during the season.",
                0.05d, "the_unchaste", "parent");

            Dictionary<string, object> promiscuous = CourtCatalogTag(tags, "promiscuous");
            if (promiscuous != null)
            {
                promiscuous["sharedPromotionStreak"] = true;
                List<object> producers = CatalogObjectList(promiscuous, "additionalProducers");
                if (!producers.Any(x => string.Equals(Convert.ToString(x, CultureInfo.InvariantCulture),
                    "promiscuous_flirtation", StringComparison.OrdinalIgnoreCase)))
                    producers.Add("promiscuous_flirtation");
            }

            foreach (string tagId in NobleJudgmentDirectTagIds)
            {
                Dictionary<string, object> judgmentTag = CourtCatalogTag(tags, tagId);
                if (judgmentTag == null) continue;
                List<object> directProducers = CatalogObjectList(
                    judgmentTag, "directReputationProducers");
                if (!directProducers.Any(x => string.Equals(
                        Convert.ToString(x, CultureInfo.InvariantCulture),
                        "noble_judgment", StringComparison.OrdinalIgnoreCase)))
                    directProducers.Add("noble_judgment");
            }
        }

        private static Dictionary<string, object> CourtCatalogTag(List<object> tags, string id)
        {
            return tags.OfType<Dictionary<string, object>>().FirstOrDefault(x =>
                string.Equals(ReadString(x, "id", ""), id, StringComparison.OrdinalIgnoreCase));
        }

        private static void SetCourtCounterparts(List<object> tags, string id, params string[] counterparts)
        {
            Dictionary<string, object> item = CourtCatalogTag(tags, id);
            if (item != null) item["counterpartTagIds"] = counterparts.Cast<object>().ToList();
        }

        private static void AddCourtArchetype(
            List<object> archetypes,
            Action<string, string, string, double, string, string, string, bool> addArchetype,
            string id, string label, string description, double chance, string tagId, string role)
        {
            addArchetype(id, label, description, chance, tagId, "court_personality", role, false);
            Dictionary<string, object> item = archetypes.OfType<Dictionary<string, object>>().FirstOrDefault(x =>
                string.Equals(ReadString(x, "id", ""), id, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            item["family"] = "court_personality";
            item["durationDays"] = 45d;
            item["promotionWindowDays"] = 45d;
            item["allowPlayerSubject"] = true;
            if (id.StartsWith("ruler_favoring", StringComparison.OrdinalIgnoreCase)) item["parameterized"] = true;
            if (id == "flirt" || id == "promiscuous_flirtation")
                item["producerGate"] = "validated_conversation_signal";
        }

        private static bool ApplyCourtDynamicSocialOutcome(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            string subjectId,
            Dictionary<string, object> payload,
            string eventId,
            double worldDay,
            out Dictionary<string, object> result,
            Dictionary<string, object> socialCatalog = null)
        {
            string tagId = ReadString(payload, "dynamicReputationTagId", "");
            if (string.IsNullOrWhiteSpace(tagId))
            {
                result = null;
                return false;
            }
            bool active = ReadBool(payload, "dynamicActive", ReadInt(payload, "dynamicValue", 0) != 0);
            int value = ReadInt(payload, "dynamicValue", 0);
            string description = ReadString(payload, "dynamicDescription", "");
            string directProducer = ReadString(payload,
                "directReputationProducer", "");
            string incident = FirstNonEmpty(ReadString(payload, "provenanceSummary", ""),
                "The character's current court standing changed through recorded campaign facts.");
            bool changed = SetCourtDynamicReputation(connection, campaignId, timelineId, subjectId,
                tagId, active, value, worldDay, eventId, description, incident,
                DictionaryOrDefault(payload, "dynamicEvidence", payload),
                socialCatalog, directProducer);
            result = new Dictionary<string, object>
            {
                ["ok"] = true, ["dynamic"] = true, ["tagId"] = tagId,
                ["active"] = active, ["value"] = value, ["changed"] = changed
            };
            return true;
        }

        private static bool SetCourtDynamicReputation(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            string subjectId,
            string tagId,
            bool active,
            int value,
            double worldDay,
            string sourceEventId,
            string descriptionOverride,
            string incident,
            Dictionary<string, object> evidence,
            Dictionary<string, object> catalogOverride = null,
            string directProducer = "")
        {
            Dictionary<string, object> catalog = catalogOverride
                ?? ReadActiveSocialCatalog(connection);
            Dictionary<string, object> definition = ReadDictionaryList(catalog, "tags").FirstOrDefault(x =>
                string.Equals(ReadString(x, "id", ""), tagId, StringComparison.OrdinalIgnoreCase));
            bool directProducerAllowed = definition != null
                && !string.IsNullOrWhiteSpace(directProducer)
                && ReadStringList(definition, "directReputationProducers").Contains(
                    directProducer, StringComparer.OrdinalIgnoreCase);
            if (definition == null
                || (!ReadBool(definition, "derived", false) && !directProducerAllowed))
                return false;
            Dictionary<string, object> existing = QuerySql(connection, @"SELECT * FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["tag"] = tagId
                }).FirstOrDefault();
            bool wasActive = existing != null && ReadString(existing, "status", "") == "active";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!active)
            {
                if (!wasActive) return false;
                ExecuteSql(connection, @"UPDATE character_reputations
SET status='derived_requirement_lost',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = subjectId, ["tag"] = tagId, ["ts"] = ts
                    });
                ExecuteSql(connection, @"INSERT OR IGNORE INTO reputation_lifecycle_events(
lifecycle_id,campaign_id,timeline_id,subject_id,tag_id,event_type,source_event_id,world_day,details_json,created_ts)
VALUES($id,$campaign,$timeline,$subject,$tag,'derived_requirement_lost',$event,$day,$details,$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = "reputation_lifecycle_" + DeterministicSocialId(
                            string.Join("|", campaignId, timelineId, subjectId, tagId, sourceEventId, "lost")),
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = subjectId, ["tag"] = tagId, ["event"] = sourceEventId ?? "",
                        ["day"] = worldDay, ["details"] = Json.Serialize(evidence ?? new Dictionary<string, object>()),
                        ["ts"] = ts
                    });
                return true;
            }

            int boundedValue = Math.Max(-50, Math.Min(50,
                ReadBool(definition, "dynamicValue", false) ? value : ReadInt(definition, "reputationValue", value)));
            string description = FirstNonEmpty(descriptionOverride, ReadString(definition, "description", ""));
            Dictionary<string, object> snapshot = new Dictionary<string, object>(definition, StringComparer.OrdinalIgnoreCase)
            {
                ["reputationValue"] = boundedValue,
                ["description"] = description
            };
            bool changed = !wasActive
                || ReadInt(existing, "reputation_value", int.MinValue) != boundedValue
                || !string.Equals(ReadString(existing, "description", ""), description, StringComparison.Ordinal);
            if (changed)
                ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'','court_dynamic','derived',$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
source_occurrence_id='',archetype_id='court_dynamic',subject_role='derived',description=$description,
reputation_value=$value,acquired_day=CASE WHEN character_reputations.status='active' THEN character_reputations.acquired_day ELSE $day END,
catalog_revision=$revision,snapshot_json=$snapshot,status='active',updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId,
                    ["tag"] = tagId, ["description"] = description, ["value"] = boundedValue,
                    ["day"] = worldDay, ["revision"] = ReadInt(catalog, "revision", BuiltInSocialCatalogRevision),
                    ["snapshot"] = Json.Serialize(snapshot), ["ts"] = ts
                });
            // Every formal judgment owns fresh provenance even when it reaffirms an
            // already-active reputation with the same value and description.
            if (!wasActive || directProducerAllowed)
            {
                RecordReputationActivation(connection, campaignId, timelineId, subjectId, tagId, "",
                    sourceEventId ?? "", worldDay, description, incident,
                    new Dictionary<string, object>
                    {
                        ["source"] = "court_dynamic",
                        ["incidentDescription"] = incident,
                        ["dynamicEvidence"] = evidence ?? new Dictionary<string, object>()
                    });
            }
            return changed || directProducerAllowed;
        }

        private static bool RecomputeCourtPopularityReputations(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            string subjectId,
            double worldDay,
            string sourceEventId)
        {
            EnsureCourtSocialReputationSchema(connection);
            Dictionary<string, object> subject = QuerySql(connection,
                "SELECT * FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = subjectId }).FirstOrDefault();
            bool eligible = subject != null
                && ReadInt(subject, "is_alive", 0) != 0
                && ReadInt(subject, "is_adult", 0) != 0
                && ReadInt(subject, "is_lord", 0) != 0
                && !string.IsNullOrWhiteSpace(ReadString(subject, "kingdom_id", ""));
            int positiveCount = 0;
            int negativeCount = 0;
            if (eligible)
            {
                Dictionary<string, object> counts = QuerySql(connection, @"SELECT
SUM(CASE WHEN incoming_affinity>30 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN incoming_affinity<-30 THEN 1 ELSE 0 END) AS negative_count
FROM (
 SELECT observer.hero_id,
 CASE WHEN pair.hero_a_id=observer.hero_id THEN pair.affinity_a_to_b ELSE pair.affinity_b_to_a END AS incoming_affinity
 FROM identity_roster observer
 JOIN relationship_pair_chemistry pair
   ON (pair.hero_a_id=observer.hero_id AND pair.hero_b_id=$subject)
   OR (pair.hero_b_id=observer.hero_id AND pair.hero_a_id=$subject)
 WHERE observer.hero_id<>$subject AND observer.is_alive=1 AND observer.is_adult=1
 AND observer.is_lord=1 AND observer.is_player=0 AND observer.is_notable=0 AND observer.is_wanderer=0
 AND observer.kingdom_id=$kingdom AND observer.is_mercenary_clan=0
) qualified;",
                    new Dictionary<string, object>
                    {
                        ["subject"] = subjectId,
                        ["kingdom"] = ReadString(subject, "kingdom_id", "")
                    }).FirstOrDefault();
                positiveCount = ReadInt(counts, "positive_count", 0);
                negativeCount = ReadInt(counts, "negative_count", 0);
            }

            string positiveTag = positiveCount >= 30 ? "beloved_of_the_court"
                : positiveCount >= 20 ? "court_favorite"
                : positiveCount >= 10 ? "well_regarded" : "";
            string negativeTag = negativeCount >= 30 ? "court_pariah"
                : negativeCount >= 20 ? "shunned_at_court"
                : negativeCount >= 10 ? "ill_regarded" : "";
            bool changed = false;
            foreach (string tagId in PositiveCourtPopularityTags)
                changed |= SetCourtDynamicReputation(connection, campaignId, timelineId, subjectId, tagId,
                    tagId == positiveTag, 0, worldDay, sourceEventId, "",
                    "The character's current underlying standing among same-kingdom nobles crossed a recorded popularity threshold.",
                    new Dictionary<string, object>
                    {
                        ["positiveQualifyingNobles"] = positiveCount,
                        ["negativeQualifyingNobles"] = negativeCount,
                        ["threshold"] = tagId == positiveTag ? positiveCount : 0
                    });
            foreach (string tagId in NegativeCourtPopularityTags)
                changed |= SetCourtDynamicReputation(connection, campaignId, timelineId, subjectId, tagId,
                    tagId == negativeTag, 0, worldDay, sourceEventId, "",
                    "The character's current underlying standing among same-kingdom nobles crossed a recorded unpopularity threshold.",
                    new Dictionary<string, object>
                    {
                        ["positiveQualifyingNobles"] = positiveCount,
                        ["negativeQualifyingNobles"] = negativeCount,
                        ["threshold"] = tagId == negativeTag ? negativeCount : 0
                    });
            ExecuteSql(connection, @"INSERT INTO court_dynamic_reputation_metrics(
campaign_id,timeline_id,subject_id,domain,positive_count,negative_count,dynamic_value,evidence_json,calculated_day,updated_ts)
VALUES($campaign,$timeline,$subject,'popularity',$positive,$negative,0,$evidence,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,domain) DO UPDATE SET
positive_count=$positive,negative_count=$negative,evidence_json=$evidence,calculated_day=$day,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = subjectId,
                    ["positive"] = positiveCount, ["negative"] = negativeCount,
                    ["evidence"] = Json.Serialize(new Dictionary<string, object>
                    {
                        ["positiveTag"] = positiveTag, ["negativeTag"] = negativeTag,
                        ["usesUnderlyingAffinityOnly"] = true
                    }),
                    ["day"] = worldDay, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            return changed;
        }

        private static int RecomputeAllCourtPopularityReputations(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            double worldDay,
            string sourceEventId)
        {
            List<string> subjects = QuerySql(connection, @"SELECT hero_id FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_lord=1;")
                .Select(x => ReadString(x, "hero_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            List<string> changedSubjects =
                RecomputeCourtPopularityReputationsBatch(connection, campaignId,
                    timelineId, subjects, worldDay, sourceEventId);
            if (changedSubjects.Count > 0)
                ReconcileSocialRelationshipsForSubjects(connection, campaignId,
                    timelineId, changedSubjects, worldDay);
            return changedSubjects.Count;
        }

        private static List<string> RecomputeCourtPopularityReputationsBatch(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            IEnumerable<string> subjectIds,
            double worldDay,
            string sourceEventId)
        {
            List<string> subjects = (subjectIds ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (subjects.Count == 0)
                return new List<string>();
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                return subjects.Where(subjectId =>
                    RecomputeCourtPopularityReputations(connection, campaignId,
                        timelineId, subjectId, worldDay,
                        sourceEventId + "|" + subjectId)).ToList();
            }

            EnsureCourtSocialReputationSchema(connection);
            Dictionary<string, object> socialCatalog =
                ReadActiveSocialCatalog(connection);
            string subjectsJson = Json.Serialize(subjects);
            List<Dictionary<string, object>> counts = QuerySql(connection, @"
WITH requested AS (
    SELECT value AS subject_id
    FROM jsonb_array_elements_text(CAST($subjects AS jsonb))
),
directed AS (
    SELECT pair.hero_b_id AS subject_id,
           pair.hero_a_id AS observer_id,
           pair.affinity_a_to_b AS incoming_affinity
    FROM relationship_pair_chemistry pair
    JOIN requested requested_subject
      ON requested_subject.subject_id=pair.hero_b_id
    UNION ALL
    SELECT pair.hero_a_id AS subject_id,
           pair.hero_b_id AS observer_id,
           pair.affinity_b_to_a AS incoming_affinity
    FROM relationship_pair_chemistry pair
    JOIN requested requested_subject
      ON requested_subject.subject_id=pair.hero_a_id
)
SELECT requested.subject_id,
CASE WHEN subject.is_alive=1 AND subject.is_adult=1
          AND subject.is_lord=1 AND subject.kingdom_id<>''
     THEN 1 ELSE 0 END AS eligible,
COALESCE(SUM(CASE
    WHEN subject.is_alive=1 AND subject.is_adult=1
     AND subject.is_lord=1 AND subject.kingdom_id<>''
     AND observer.hero_id IS NOT NULL
     AND observer.is_alive=1 AND observer.is_adult=1
     AND observer.is_lord=1 AND observer.is_player=0
     AND observer.is_notable=0 AND observer.is_wanderer=0
     AND observer.kingdom_id=subject.kingdom_id
     AND observer.is_mercenary_clan=0
     AND directed.incoming_affinity>30 THEN 1 ELSE 0 END),0)
     AS positive_count,
COALESCE(SUM(CASE
    WHEN subject.is_alive=1 AND subject.is_adult=1
     AND subject.is_lord=1 AND subject.kingdom_id<>''
     AND observer.hero_id IS NOT NULL
     AND observer.is_alive=1 AND observer.is_adult=1
     AND observer.is_lord=1 AND observer.is_player=0
     AND observer.is_notable=0 AND observer.is_wanderer=0
     AND observer.kingdom_id=subject.kingdom_id
     AND observer.is_mercenary_clan=0
     AND directed.incoming_affinity<-30 THEN 1 ELSE 0 END),0)
     AS negative_count
FROM requested
LEFT JOIN identity_roster subject
  ON subject.hero_id=requested.subject_id
LEFT JOIN directed
  ON directed.subject_id=requested.subject_id
LEFT JOIN identity_roster observer
  ON observer.hero_id=directed.observer_id
GROUP BY requested.subject_id,subject.is_alive,subject.is_adult,
         subject.is_lord,subject.kingdom_id
ORDER BY requested.subject_id;",
                new Dictionary<string, object>
                {
                    ["subjects"] = subjectsJson
                });

            List<string> popularityTags = PositiveCourtPopularityTags
                .Concat(NegativeCourtPopularityTags)
                .ToList();
            Dictionary<string, HashSet<string>> activeTags =
                subjects.ToDictionary(value => value,
                    value => new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in QuerySql(connection, @"
SELECT subject_id,tag_id
FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='active'
AND subject_id IN (
    SELECT value FROM jsonb_array_elements_text(CAST($subjects AS jsonb)))
AND tag_id IN (
    'well_regarded','court_favorite','beloved_of_the_court',
    'ill_regarded','shunned_at_court','court_pariah');",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subjects"] = subjectsJson
                }))
            {
                string subjectId = ReadString(row, "subject_id", "");
                if (activeTags.TryGetValue(subjectId,
                    out HashSet<string> tags))
                    tags.Add(ReadString(row, "tag_id", ""));
            }

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> metricRows =
                new List<Dictionary<string, object>>(counts.Count);
            List<string> changedSubjects = new List<string>();
            foreach (Dictionary<string, object> count in counts)
            {
                string subjectId = ReadString(count, "subject_id", "");
                bool eligible = ReadInt(count, "eligible", 0) != 0;
                int positiveCount = eligible
                    ? ReadInt(count, "positive_count", 0) : 0;
                int negativeCount = eligible
                    ? ReadInt(count, "negative_count", 0) : 0;
                string positiveTag = positiveCount >= 30
                    ? "beloved_of_the_court"
                    : positiveCount >= 20 ? "court_favorite"
                    : positiveCount >= 10 ? "well_regarded" : "";
                string negativeTag = negativeCount >= 30
                    ? "court_pariah"
                    : negativeCount >= 20 ? "shunned_at_court"
                    : negativeCount >= 10 ? "ill_regarded" : "";
                HashSet<string> desiredTags = new HashSet<string>(
                    new[] { positiveTag, negativeTag }
                        .Where(value => !string.IsNullOrWhiteSpace(value)),
                    StringComparer.OrdinalIgnoreCase);
                HashSet<string> currentTags =
                    activeTags.TryGetValue(subjectId,
                        out HashSet<string> existingTags)
                        ? existingTags
                        : new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);
                if (!currentTags.SetEquals(desiredTags))
                {
                    bool changed = false;
                    foreach (string tagId in popularityTags.Where(tagId =>
                        currentTags.Contains(tagId)
                        != desiredTags.Contains(tagId)))
                    {
                        bool active = desiredTags.Contains(tagId);
                        changed |= SetCourtDynamicReputation(connection,
                            campaignId, timelineId, subjectId, tagId,
                            active, 0, worldDay,
                            sourceEventId + "|" + subjectId, "",
                            "The character's current underlying standing among same-kingdom nobles crossed a recorded popularity threshold.",
                            new Dictionary<string, object>
                            {
                                ["positiveQualifyingNobles"] = positiveCount,
                                ["negativeQualifyingNobles"] = negativeCount,
                                ["threshold"] = active
                                    ? (PositiveCourtPopularityTags.Contains(
                                        tagId, StringComparer.OrdinalIgnoreCase)
                                        ? positiveCount : negativeCount)
                                    : 0
                            }, socialCatalog);
                    }
                    if (changed)
                        changedSubjects.Add(subjectId);
                }
                metricRows.Add(new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId,
                    ["positive"] = positiveCount,
                    ["negative"] = negativeCount,
                    ["evidence"] = Json.Serialize(
                        new Dictionary<string, object>
                        {
                            ["positiveTag"] = positiveTag,
                            ["negativeTag"] = negativeTag,
                            ["usesUnderlyingAffinityOnly"] = true
                        }),
                    ["day"] = worldDay,
                    ["ts"] = ts
                });
            }
            if (metricRows.Count > 0)
            {
                ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        campaign text,timeline text,subject text,positive integer,
        negative integer,evidence text,day double precision,ts bigint)
)
INSERT INTO court_dynamic_reputation_metrics(
campaign_id,timeline_id,subject_id,domain,positive_count,negative_count,
dynamic_value,evidence_json,calculated_day,updated_ts)
SELECT campaign,timeline,subject,'popularity',positive,negative,0,
       evidence,day,ts
FROM x
ON CONFLICT(campaign_id,timeline_id,subject_id,domain) DO UPDATE SET
positive_count=excluded.positive_count,
negative_count=excluded.negative_count,
evidence_json=excluded.evidence_json,
calculated_day=excluded.calculated_day,
updated_ts=excluded.updated_ts;",
                    new Dictionary<string, object>
                    {
                        ["rows"] = Json.Serialize(metricRows)
                    });
            }
            return changedSubjects;
        }

        private static Dictionary<string, object> ProcessCompletedConversationCourtStanding(
            string campaignId,
            Dictionary<string, object> session,
            List<Dictionary<string, object>> turns,
            double worldDay,
            bool interrupted)
        {
            string sessionId = ReadString(session, "session_id", "");
            string channel = ReadString(session, "channel", "");
            if (string.IsNullOrWhiteSpace(sessionId)
                || !IsCourtStandingConversationChannel(channel))
                return new Dictionary<string, object> { ["processed"] = false, ["reason"] = "ineligible_session" };
            int meaningfulExchanges = (turns ?? new List<Dictionary<string, object>>())
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "exchange_id", "")))
                .GroupBy(x => ReadString(x, "exchange_id", ""), StringComparer.OrdinalIgnoreCase)
                .Count(group => group.Any(x => ReadString(x, "role", "") == "player")
                    && group.Any(x => ReadString(x, "role", "") == "npc"));
            if (meaningfulExchanges == 0)
                return new Dictionary<string, object> { ["processed"] = false, ["reason"] = "no_meaningful_exchange" };

            string timelineId = "main";
            string npcId = ReadString(session, "npc_id", "");
            string playerId = ReadString(session, "player_id", "");
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> groupIntroductions = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                timelineId = ResolveConversationSocialTimeline(connection, session);
                List<string> exchangeIds = (turns ?? new List<Dictionary<string, object>>())
                    .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "text", "")) && IsQualifyingFavorConversationTurn(x))
                    .GroupBy(x => ReadString(x, "exchange_id", ""), StringComparer.OrdinalIgnoreCase)
                    .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                        && group.Any(x => ReadString(x, "role", "") == "player")
                        && group.Any(x => ReadString(x, "role", "") == "npc"))
                    .Select(group => group.Key).ToList();
                string favoriteId = SingleQualifyingFavorNpcId(turns, exchangeIds);
                double contactDay = turns.Where(x => exchangeIds.Contains(ReadString(x, "exchange_id", "")))
                    .Select(x => ReadDouble(x, "world_day", worldDay)).DefaultIfEmpty(worldDay).Max();
                if (exchangeIds.Count > 0)
                {
                    RecordRulerFavorContact(connection, campaignId, timelineId, playerId, npcId, contactDay, exchangeIds.Last());
                    // Group sessions contain attributed NPC turns; only actual speakers qualify.
                    foreach (string speakerId in turns.Where(x => ReadString(x, "role", "") == "npc"
                        && exchangeIds.Contains(ReadString(x, "exchange_id", "")))
                        .Select(x => ReadString(x, "speaker_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
                        RecordRulerFavorContact(connection, campaignId, timelineId, playerId, speakerId, contactDay, exchangeIds.Last());
                    foreach (var exchange in turns.Where(IsQualifyingFavorConversationTurn)
                        .Where(x => ReadString(x, "role", "") == "npc" && !string.IsNullOrWhiteSpace(ReadString(x, "text", "")))
                        .GroupBy(x => ReadString(x, "exchange_id", "")))
                    {
                        var speakers = exchange.Select(x => ReadString(x, "speaker_id", "")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
                        for (int a = 0; a < speakers.Count; a++)
                            for (int b = a + 1; b < speakers.Count; b++)
                                RecordRulerFavorContact(connection, campaignId, timelineId, speakers[a], speakers[b],
                                    exchange.Select(x => ReadDouble(x, "world_day", worldDay)).DefaultIfEmpty(worldDay).Max(), "group:" + exchange.Key);
                    }
                }
                groupIntroductions = ProcessExplicitGroupConversationIntroductions(
                    connection, channel, sessionId, turns, worldDay);
                if (interrupted)
                    return new Dictionary<string, object> { ["processed"] = true, ["favorContactRecorded"] = exchangeIds.Count > 0,
                        ["groupIntroductions"] = groupIntroductions,
                        ["reason"] = "completed_speech_before_interruption" };
                if (!string.IsNullOrWhiteSpace(favoriteId))
                    ProcessPlayerRulerFavoringExchanges(connection, campaignId, timelineId,
                        sessionId, playerId, favoriteId, exchangeIds, contactDay, results);
                ProcessValidatedConversationSocialSignals(connection, campaignId, timelineId, session, turns, worldDay, results);
            }
            if (!campaignId.StartsWith("__", StringComparison.Ordinal))
                SchedulePendingReputationReasonJobs(campaignId);
            return new Dictionary<string, object>
            {
                ["processed"] = true, ["meaningfulExchanges"] = meaningfulExchanges,
                ["timelineId"] = timelineId, ["results"] = results,
                ["groupIntroductions"] = groupIntroductions
            };
        }

        private static bool IsCourtStandingConversationChannel(string channel)
        {
            return string.Equals(channel, "in_person", StringComparison.OrdinalIgnoreCase)
                || string.Equals(channel, "party_chat", StringComparison.OrdinalIgnoreCase);
        }

        private static string SingleQualifyingFavorNpcId(
            IEnumerable<Dictionary<string, object>> turns,
            ICollection<string> exchangeIds)
        {
            if (exchangeIds == null || exchangeIds.Count == 0) return string.Empty;
            List<string> speakers = (turns ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(IsQualifyingFavorConversationTurn)
                .Where(turn => exchangeIds.Contains(ReadString(turn, "exchange_id", ""))
                    && string.Equals(ReadString(turn, "role", ""), "npc", StringComparison.OrdinalIgnoreCase))
                .Select(turn => ReadString(turn, "speaker_id", ""))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return speakers.Count == 1 ? speakers[0] : string.Empty;
        }

        private static List<Dictionary<string, object>> ProcessExplicitGroupConversationIntroductions(
            ReignDbConnection connection,
            string channel,
            string sessionId,
            IEnumerable<Dictionary<string, object>> turns,
            double worldDay)
        {
            var results = new List<Dictionary<string, object>>();
            if (!string.Equals(channel, "party_chat", StringComparison.OrdinalIgnoreCase)) return results;
            foreach (IGrouping<string, Dictionary<string, object>> exchange in
                (turns ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Where(turn => !string.IsNullOrWhiteSpace(ReadString(turn, "exchange_id", "")))
                    .GroupBy(turn => ReadString(turn, "exchange_id", ""), StringComparer.OrdinalIgnoreCase))
            {
                string playerText = string.Join(" ", exchange
                    .Where(turn => string.Equals(ReadString(turn, "role", ""), "player", StringComparison.OrdinalIgnoreCase))
                    .Select(turn => ReadString(turn, "text", "")));
                if (!Regex.IsMatch(playerText, @"\b(?:meet|introduc(?:e|ed|ing|tion)|present(?:ed|ing)?)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                List<Dictionary<string, object>> speakers = exchange
                    .Where(turn => string.Equals(ReadString(turn, "role", ""), "npc", StringComparison.OrdinalIgnoreCase))
                    .Select(turn => CourtConversationParticipant(connection, ReadString(turn, "speaker_id", "")))
                    .Where(participant => participant != null
                        && ExplicitIntroductionNamesParticipant(playerText, ReadString(participant, "name", "")))
                    .GroupBy(participant => ReadString(participant, "heroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToList();
                for (int a = 0; a < speakers.Count; a++)
                {
                    for (int b = a + 1; b < speakers.Count; b++)
                    {
                        foreach (var pair in new[] { new[] { speakers[a], speakers[b] }, new[] { speakers[b], speakers[a] } })
                        {
                            string observerId = ReadString(pair[0], "heroStringId", "");
                            string subjectId = ReadString(pair[1], "heroStringId", "");
                            string status = UpsertVerifiedIdentity(connection, pair[0], pair[1],
                                "explicit_group_introduction", sessionId, worldDay, sessionId + ":" + exchange.Key);
                            results.Add(new Dictionary<string, object>
                            {
                                ["exchangeId"] = exchange.Key,
                                ["observerId"] = observerId,
                                ["subjectId"] = subjectId,
                                ["status"] = status
                            });
                        }
                    }
                }
            }
            return results;
        }

        private static bool ExplicitIntroductionNamesParticipant(string text, string canonicalName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(canonicalName)) return false;
            return Regex.IsMatch(text,
                @"(?<![\p{L}\p{M}])" + Regex.Escape(canonicalName.Trim()) + @"(?![\p{L}\p{M}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static void ProcessPlayerRulerFavoringExchanges(ReignDbConnection connection,
            string campaignId, string timelineId, string sessionId, string playerId,
            string favoriteId, List<string> exchangeIds, double worldDay,
            List<Dictionary<string, object>> results)
        {
            Dictionary<string, object> ruler = CourtConversationParticipant(connection, playerId);
            Dictionary<string, object> favorite = CourtConversationParticipant(connection, favoriteId);
            if (ruler == null || favorite == null || !ReadBool(ruler, "isRuler", false)
                || exchangeIds == null || exchangeIds.Count == 0) return;
            Dictionary<string, object> existing = QuerySql(connection, @"SELECT * FROM court_player_favoring_exchanges
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = playerId, ["favorite"] = favoriteId }).FirstOrDefault();
            int prior = ReadInt(existing, "exchange_count", 0);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int next = prior;
            foreach (string exchangeId in exchangeIds)
            {
                if (QuerySql(connection, @"SELECT 1 FROM court_player_favoring_exchange_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite
AND exchange_id=$exchange LIMIT 1;", new Dictionary<string, object>
                    { ["campaign"] = campaignId, ["timeline"] = timelineId, ["ruler"] = playerId,
                        ["favorite"] = favoriteId, ["exchange"] = exchangeId }).Any()) continue;
                next++;
                ExecuteSql(connection, @"INSERT INTO court_player_favoring_exchange_receipts(
campaign_id,timeline_id,ruler_id,favorite_id,exchange_id,world_day,created_ts)
VALUES($campaign,$timeline,$ruler,$favorite,$exchange,$day,$ts);",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ruler"] = playerId, ["favorite"] = favoriteId, ["exchange"] = exchangeId,
                        ["day"] = worldDay, ["ts"] = ts });
                if (next >= 6)
                {
                    ruler["role"] = "ruler";
                    ruler["linkedHeroId"] = favoriteId;
                    ruler["linkedHeroName"] = ReadString(favorite, "canonicalName", favoriteId);
                    results.Add(RegisterSocialOccurrence(connection, campaignId,
                        new Dictionary<string, object>
                        {
                            ["timelineId"] = timelineId, ["archetypeId"] = "ruler_favoring_dialogue",
                            ["threadKey"] = playerId + "|ruler_favoring|" + favoriteId,
                            ["sourceEventId"] = exchangeId + "|favoring|" + next.ToString(CultureInfo.InvariantCulture),
                            ["worldDay"] = worldDay,
                            ["provenanceSummary"] = ReadString(ruler, "canonicalName", playerId)
                                + " repeatedly sought substantive private conversation with "
                                + ReadString(favorite, "canonicalName", favoriteId) + ".",
                            ["participants"] = new List<object> { ruler }
                        }));
                }
            }
            if (next == prior) return;
            ExecuteSql(connection, @"INSERT INTO court_player_favoring_exchanges(
campaign_id,timeline_id,ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day,updated_ts)
VALUES($campaign,$timeline,$ruler,$favorite,$count,$exchange,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO UPDATE SET
exchange_count=$count,last_exchange_id=$exchange,last_world_day=$day,updated_ts=$ts;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = playerId, ["favorite"] = favoriteId, ["count"] = next,
                    ["exchange"] = exchangeIds.Last(), ["day"] = worldDay, ["ts"] = ts });
        }

        private static string ResolveConversationSocialTimeline(
            ReignDbConnection connection,
            Dictionary<string, object> session)
        {
            Dictionary<string, object> payload =
                TryParseJsonObject(ReadString(session, "payload_json", "{}")) ?? new Dictionary<string, object>();
            return FirstNonEmpty(ReadString(payload, "timelineId", ""), ResolveCharacterSocialTimeline(connection));
        }

        private static Dictionary<string, object> CourtConversationParticipant(
            ReignDbConnection connection,
            string heroId,
            string campaignId = "")
        {
            if (string.IsNullOrWhiteSpace(heroId)) return null;
            Dictionary<string, object> roster = QuerySql(connection,
                "SELECT * FROM identity_roster WHERE hero_id=$id LIMIT 1;",
                new Dictionary<string, object> { ["id"] = heroId }).FirstOrDefault();
            if (roster == null || ReadInt(roster, "is_alive", 0) == 0 || ReadInt(roster, "is_adult", 0) == 0)
                return null;
            return new Dictionary<string, object>
            {
                ["heroStringId"] = heroId,
                ["subjectId"] = heroId,
                ["name"] = ReadString(roster, "canonical_name", heroId),
                ["canonicalName"] = ReadString(roster, "canonical_name", heroId),
                ["clanTier"] = ReadInt(roster, "clan_tier", 0),
                ["isAlive"] = true, ["isAdult"] = true,
                ["isPlayer"] = ReadInt(roster, "is_player", 0) != 0,
                ["isRuler"] = ReadInt(roster, "is_ruler", 0) != 0,
                ["sex"] = ReadString(roster, "sex", ""),
                ["spouseId"] = string.IsNullOrWhiteSpace(campaignId) ? "" : ReadString(
                    ReadJsonObject(CharacterFile(campaignId, heroId, "profile.json")), "spouseId", "")
            };
        }

        private static Dictionary<string, object> NormalizeAndPersistConversationSocialSignals(
            string campaignId, Dictionary<string, object> parsed, Dictionary<string, object> request,
            string npcId, string playerId, string playerText, string npcReply,
            Dictionary<string, object> conceptionGate, string exchangeId, double worldDay)
        {
            string timelineId = "main";
            string sessionId = ReadFirstString(request, "conversationSessionId", "sessionId", "conversationId");
            List<Dictionary<string, object>> proposed = parsed == null
                ? new List<Dictionary<string, object>>() : ReadDictionaryList(parsed, "socialSignals");
            string explicitPlayerFlirtation = ExplicitCurrentPlayerFlirtationQuote(playerText);
            if (!string.IsNullOrWhiteSpace(explicitPlayerFlirtation)
                && !proposed.Any(signal =>
                    ReadString(signal, "type", "").Equals("flirtation", StringComparison.OrdinalIgnoreCase)
                    && ReadString(signal, "supportingQuote", "").Equals(
                        explicitPlayerFlirtation, StringComparison.Ordinal)))
            {
                proposed.Add(new Dictionary<string, object>
                {
                    ["type"] = "flirtation",
                    ["supportingQuote"] = explicitPlayerFlirtation,
                    ["speakerHeroId"] = playerId,
                    ["targetHeroId"] = npcId,
                    ["serverDerivedExplicitCurrentSignal"] = true
                });
            }
            if (ReadBool(conceptionGate, "completed", false))
            {
                proposed.Add(new Dictionary<string, object>
                {
                    ["type"] = "sexual_intimacy_completed",
                    ["supportingQuote"] = ShortSocialEvidenceQuote(npcReply),
                    ["speakerHeroId"] = npcId,
                    ["targetHeroId"] = playerId,
                    ["conceptionGate"] = true
                });
            }
            List<Dictionary<string, object>> accepted = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> rejected = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                timelineId = FirstNonEmpty(ReadString(request, "timelineId", ""),
                    ResolveCharacterSocialTimeline(connection));
                for (int index = 0; index < proposed.Count; index++)
                {
                    Dictionary<string, object> raw = proposed[index];
                    string type = ReadString(raw, "type", "").Trim().ToLowerInvariant();
                    string quote = FirstNonEmpty(ReadString(raw, "supportingQuote", ""),
                        ReadString(raw, "evidenceQuote", "")).Trim();
                    bool playerQuote = ContainsExactSocialQuote(playerText, quote);
                    bool npcQuote = ContainsExactSocialQuote(npcReply, quote);
                    string speakerId = playerQuote && !npcQuote ? playerId : npcQuote && !playerQuote ? npcId : "";
                    string targetId = speakerId == playerId ? npcId : speakerId == npcId ? playerId : "";
                    string reason = "";
                    if (type != "flirtation" && type != "sexual_intimacy_completed")
                        reason = "unsupported_signal_type";
                    else if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(speakerId))
                        reason = "supporting_quote_not_exact_or_ambiguous";
                    else if (!string.IsNullOrWhiteSpace(ReadString(raw, "speakerHeroId", ""))
                        && !ReadString(raw, "speakerHeroId", "").Equals(speakerId, StringComparison.OrdinalIgnoreCase))
                        reason = "speaker_mismatch";
                    else if (!string.IsNullOrWhiteSpace(ReadString(raw, "targetHeroId", ""))
                        && !ReadString(raw, "targetHeroId", "").Equals(targetId, StringComparison.OrdinalIgnoreCase))
                        reason = "target_mismatch";
                    Dictionary<string, object> speaker = CourtConversationParticipant(connection, speakerId, campaignId);
                    Dictionary<string, object> target = CourtConversationParticipant(connection, targetId, campaignId);
                    if (string.IsNullOrWhiteSpace(reason) && (speaker == null || target == null || speakerId == targetId))
                        reason = "ineligible_participant";
                    if (string.IsNullOrWhiteSpace(reason) && !SocialSignalSemanticsAreCompleted(type, quote,
                        ReadBool(raw, "conceptionGate", false)))
                        reason = "unsupported_or_incomplete_act";
                    string signalId = DeterministicSocialId(string.Join("|", campaignId, timelineId,
                        exchangeId, type, speakerId, targetId, quote));
                    Dictionary<string, object> opportunity = ReadDictionary(request, "opportunitySnapshot")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunityObserver = ReadDictionary(opportunity, "observer")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> opportunityTarget = ReadDictionary(opportunity, "target")
                        ?? new Dictionary<string, object>();
                    int speakerClanTier = Math.Max(0, Math.Min(6, ReadInt(
                        speakerId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                            ? opportunityTarget : opportunityObserver,
                        "clanTier", ReadInt(speaker, "clanTier", 0))));
                    int targetClanTier = Math.Max(0, Math.Min(6, ReadInt(
                        targetId.Equals(playerId, StringComparison.OrdinalIgnoreCase)
                            ? opportunityTarget : opportunityObserver,
                        "clanTier", ReadInt(target, "clanTier", 0))));
                    Dictionary<string, object> normalized = new Dictionary<string, object>
                    {
                        ["signalId"] = signalId, ["type"] = type,
                        ["speakerHeroId"] = speakerId, ["targetHeroId"] = targetId,
                        ["supportingQuote"] = quote, ["validated"] = string.IsNullOrWhiteSpace(reason),
                        ["validationSource"] = "reign_conversation_engine",
                        ["confidence"] = "explicit", ["rejectionReason"] = reason,
                        ["exchangeId"] = exchangeId, ["worldDay"] = worldDay,
                        ["speakerClanTier"] = speakerClanTier, ["targetClanTier"] = targetClanTier
                    };
                    ExecuteSql(connection, @"INSERT INTO court_social_signal_evidence(
campaign_id,timeline_id,exchange_id,signal_id,session_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts)
VALUES($campaign,$timeline,$exchange,$signal,$session,$type,$speaker,$target,$quote,$accepted,$reason,$evidence,$ts)
ON CONFLICT(campaign_id,timeline_id,exchange_id,signal_id) DO NOTHING;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["exchange"] = exchangeId,
                            ["signal"] = signalId, ["session"] = sessionId, ["type"] = type,
                            ["speaker"] = speakerId, ["target"] = targetId, ["quote"] = quote,
                            ["accepted"] = string.IsNullOrWhiteSpace(reason) ? 1 : 0, ["reason"] = reason,
                            ["evidence"] = Json.Serialize(normalized), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    (string.IsNullOrWhiteSpace(reason) ? accepted : rejected).Add(normalized);
                }
            }
            return new Dictionary<string, object>
            {
                ["timelineId"] = timelineId, ["accepted"] = accepted,
                ["rejected"] = rejected, ["signals"] = accepted
            };
        }

        private static bool ContainsExactSocialQuote(string source, string quote)
        {
            return !string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(quote)
                && quote.Length >= 3 && source.IndexOf(quote, StringComparison.Ordinal) >= 0;
        }

        private static string ExplicitCurrentPlayerFlirtationQuote(string playerText)
        {
            string value = (playerText ?? "").Trim();
            if (value.Length == 0) return "";
            string normalized = value.ToLowerInvariant();
            string[] nonCurrentMarkers =
            {
                "hypothetically", "suppose ", "imagine ", "if i said", "if i were",
                "would i ", "could i ", "might i ", "should i ", "i used to ",
                "i was flirting", "i am not flirting", "i'm not flirting"
            };
            if (nonCurrentMarkers.Any(marker => normalized.Contains(marker))) return "";

            string[] explicitCurrentPhrases =
            {
                "I am flirting with you right now",
                "I'm flirting with you right now",
                "I am flirting with you",
                "I'm flirting with you"
            };
            foreach (string phrase in explicitCurrentPhrases)
            {
                int index = value.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
                if (index >= 0) return value.Substring(index, phrase.Length);
            }
            return "";
        }

        private static string ShortSocialEvidenceQuote(string text)
        {
            string value = (text ?? "").Trim();
            return value.Length <= 240 ? value : value.Substring(0, 240).Trim();
        }

        private static bool SocialSignalSemanticsAreCompleted(string type, string quote, bool conceptionCompleted)
        {
            string value = (quote ?? "").ToLowerInvariant();
            if (type == "sexual_intimacy_completed" && conceptionCompleted) return true;
            string[] rejected = { "would ", "could ", "might ", "if ", "want to", "refuse", "won't", "will not", "no,", "kiss", "embrace" };
            if (rejected.Any(term => value.Contains(term))) return false;
            if (type == "flirtation")
                return new[] { "desire", "beautiful", "handsome", "court you", "flirt", "bed", "attracted", "want you", "love" }
                    .Any(term => value.Contains(term));
            return new[] { "made love", "slept together", "lay together", "had sex", "inseminat", "consummat" }
                .Any(term => value.Contains(term));
        }

        private static Dictionary<string, object> ProcessImmediatePlayerIntimacySignals(
            string campaignId, string timelineId, string sessionId,
            List<Dictionary<string, object>> acceptedSignals, double worldDay)
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureSocialReputationSchema(connection);
                foreach (Dictionary<string, object> signal in acceptedSignals ?? new List<Dictionary<string, object>>())
                {
                    if (!ReadString(signal, "type", "").Equals("sexual_intimacy_completed", StringComparison.OrdinalIgnoreCase)) continue;
                    string signalId = ReadString(signal, "signalId", "");
                    string speakerId = ReadString(signal, "speakerHeroId", "");
                    string targetId = ReadString(signal, "targetHeroId", "");
                    Dictionary<string, object> speaker = CourtConversationParticipant(connection, speakerId, campaignId);
                    Dictionary<string, object> target = CourtConversationParticipant(connection, targetId, campaignId);
                    if (speaker == null || target == null) continue;
                    Dictionary<string, object> player = ReadBool(speaker, "isPlayer", false) ? speaker
                        : ReadBool(target, "isPlayer", false) ? target : null;
                    Dictionary<string, object> partner = ReferenceEquals(player, speaker) ? target : speaker;
                    if (player == null || partner == null) continue;
                    if (QuerySql(connection, @"SELECT 1 FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["signal"] = signalId }).Any()) continue;
                    string playerSpouse = ReadString(player, "spouseId", "");
                    string partnerSpouse = ReadString(partner, "spouseId", "");
                    bool eligible = (!string.IsNullOrWhiteSpace(playerSpouse) && !playerSpouse.Equals(ReadString(partner, "subjectId", ""), StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(partnerSpouse) && !partnerSpouse.Equals(ReadString(player, "subjectId", ""), StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, object> result = new Dictionary<string, object> { ["ok"] = true, ["eligible"] = eligible, ["exposed"] = false };
                    if (eligible)
                    {
                        player["role"] = string.IsNullOrWhiteSpace(playerSpouse) ? "unmarried" : "married";
                        partner["role"] = string.IsNullOrWhiteSpace(partnerSpouse) ? "unmarried" : "married";
                        result = RegisterSocialOccurrence(connection, campaignId, new Dictionary<string, object>
                        {
                            ["timelineId"] = timelineId, ["archetypeId"] = "affair",
                            ["threadKey"] = string.Join("|", new[] { ReadString(player, "subjectId", ""), ReadString(partner, "subjectId", "") }.OrderBy(x => x)) + "|affair",
                            ["sourceEventId"] = "intimacy|" + signalId, ["worldDay"] = worldDay,
                            ["provenanceSummary"] = ReadString(player, "canonicalName", "The player")
                                + " completed an intimate encounter with " + ReadString(partner, "canonicalName", "another person")
                                + " while at least one participant was married to someone else.",
                            ["participants"] = new List<object> { player, partner }
                        });
                    }
                    ExecuteSql(connection, @"INSERT INTO court_intimacy_exposure_receipts(
campaign_id,timeline_id,signal_id,player_id,partner_id,eligible,exposed,probability,roll,result_json,created_ts)
VALUES($campaign,$timeline,$signal,$player,$partner,$eligible,$exposed,$probability,$roll,$result,$ts);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["signal"] = signalId,
                            ["player"] = ReadString(player, "subjectId", ""), ["partner"] = ReadString(partner, "subjectId", ""),
                            ["eligible"] = eligible ? 1 : 0, ["exposed"] = ReadBool(result, "exposed", ReadBool(result, "created", false)) ? 1 : 0,
                            ["probability"] = ReadDouble(result, "exposureChance", ReadDouble(result, "chance", 0d)),
                            ["roll"] = ReadDouble(result, "exposureRoll", ReadDouble(result, "roll", 0d)),
                            ["result"] = Json.Serialize(result), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    results.Add(result);
                }
            }
            return new Dictionary<string, object> { ["processed"] = results.Count, ["results"] = results };
        }

        private static void ProcessValidatedConversationSocialSignals(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            Dictionary<string, object> session,
            List<Dictionary<string, object>> turns,
            double worldDay,
            List<Dictionary<string, object>> results)
        {
            string sessionId = ReadString(session, "session_id", "");
            HashSet<string> processedFlirtSpeakers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> turn in turns ?? new List<Dictionary<string, object>>())
            {
                Dictionary<string, object> payload =
                    TryParseJsonObject(ReadString(turn, "payload_json", "{}")) ?? new Dictionary<string, object>();
                List<Dictionary<string, object>> signals = ReadDictionaryList(payload, "socialSignals");
                for (int index = 0; index < signals.Count; index++)
                {
                    Dictionary<string, object> signal = signals[index];
                    string type = ReadString(signal, "type", "");
                    string confidence = ReadString(signal, "confidence", "");
                    string validationSource = ReadString(signal, "validationSource", "");
                    if (!string.Equals(type, "flirtation", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(confidence, "explicit", StringComparison.OrdinalIgnoreCase)
                        || !ReadBool(signal, "validated", false)
                        || !string.Equals(validationSource, "reign_conversation_engine", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string speakerId = ReadString(signal, "speakerHeroId", "");
                    string targetId = ReadString(signal, "targetHeroId", "");
                    Dictionary<string, object> speaker = CourtConversationParticipant(connection, speakerId);
                    Dictionary<string, object> target = CourtConversationParticipant(connection, targetId);
                    if (speaker == null || target == null || speakerId == targetId
                        || string.Equals(ReadString(speaker, "sex", ""), ReadString(target, "sex", ""), StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!processedFlirtSpeakers.Add(speakerId)) continue;
                    speaker["clanTier"] = Math.Max(0, Math.Min(6,
                        ReadInt(signal, "speakerClanTier", ReadInt(speaker, "clanTier", 0))));
                    target["clanTier"] = Math.Max(0, Math.Min(6,
                        ReadInt(signal, "targetClanTier", ReadInt(target, "clanTier", 0))));
                    string signalKey = FirstNonEmpty(ReadString(signal, "signalId", ""),
                        ReadString(turn, "turn_id", "") + "|" + index.ToString(CultureInfo.InvariantCulture));
                    if (QuerySql(connection, @"SELECT 1 FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND session_id=$session AND signal_key=$key LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["session"] = sessionId, ["key"] = signalKey
                        }).Any()) continue;

                    speaker["role"] = "speaker";
                    Dictionary<string, object> flirtResult = RegisterSocialOccurrence(connection, campaignId,
                        new Dictionary<string, object>
                        {
                            ["timelineId"] = timelineId, ["archetypeId"] = "flirt",
                            ["threadKey"] = speakerId + "|flirt",
                            ["sourceEventId"] = sessionId + "|flirt|" + signalKey,
                            ["worldDay"] = worldDay,
                            ["provenanceSummary"] = ReadString(speaker, "canonicalName", speakerId)
                                + " directed an explicit flirtatious remark toward "
                                + ReadString(target, "canonicalName", targetId) + " during a recorded conversation.",
                            ["participants"] = new List<object> { speaker }
                        });
                    results.Add(flirtResult);

                    Dictionary<string, object> existingTarget = QuerySql(connection, @"SELECT * FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND target_id=$target LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["subject"] = speakerId, ["target"] = targetId
                        }).FirstOrDefault();
                    int otherTargetCount = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND target_id<>$target;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["subject"] = speakerId, ["target"] = targetId
                        }).FirstOrDefault(), "count", 0);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ExecuteSql(connection, @"INSERT INTO court_flirt_targets(
campaign_id,timeline_id,subject_id,target_id,first_session_id,first_world_day,last_session_id,last_world_day,incident_count,updated_ts)
VALUES($campaign,$timeline,$subject,$target,$session,$day,$session,$day,1,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,target_id) DO UPDATE SET
last_session_id=$session,last_world_day=$day,
incident_count=court_flirt_targets.incident_count+1,updated_ts=$ts;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["subject"] = speakerId, ["target"] = targetId,
                            ["session"] = sessionId, ["day"] = worldDay, ["ts"] = ts
                        });
                    bool flirtReputation = QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND tag_id='flirt' AND status='active' LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["subject"] = speakerId
                        }).Any();
                    if (flirtReputation && existingTarget == null && otherTargetCount > 0)
                    {
                        results.Add(RegisterSocialOccurrence(connection, campaignId,
                            new Dictionary<string, object>
                            {
                                ["timelineId"] = timelineId, ["archetypeId"] = "promiscuous_flirtation",
                                ["threadKey"] = speakerId + "|promiscuous",
                                ["sourceEventId"] = sessionId + "|promiscuous|" + signalKey,
                                ["worldDay"] = worldDay,
                                ["provenanceSummary"] = ReadString(speaker, "canonicalName", speakerId)
                                    + " extended explicit romantic attention to another person after becoming known as a flirt.",
                                ["participants"] = new List<object> { speaker }
                            }));
                    }
                    ExecuteSql(connection, @"INSERT INTO court_conversation_signal_receipts(
campaign_id,timeline_id,session_id,signal_key,signal_type,speaker_id,target_id,result_json,created_ts)
VALUES($campaign,$timeline,$session,$key,'flirtation',$speaker,$target,$result,$ts);",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["session"] = sessionId, ["key"] = signalKey,
                            ["speaker"] = speakerId, ["target"] = targetId,
                            ["result"] = Json.Serialize(flirtResult), ["ts"] = ts
                        });
                }
            }
        }
    }
}
