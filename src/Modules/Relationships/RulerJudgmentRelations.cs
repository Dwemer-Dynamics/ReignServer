using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Rulers;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> RulerRelationsApi(string operation, Dictionary<string, object> p)
        {
            string campaign = ReadString(p, "campaignId", ""), timeline = ContinuityTimeline(p);
            double day = ReadDouble(p, "worldDay", -1);
            string victor = ReadString(p, "victorId", ""), prisoner = ReadString(p, "prisonerId", "");
            if (campaign.Length == 0 || !ReignRulerPolicy.Finite(day) || day < 0)
                return TestDict("ok", false, "error", "Ruler state requires a campaign, timeline and native day.");
            using var ownership = EnterCampaignWorkLock(CampaignRelationshipWriteLock(campaign));
            using var c = OpenCampaignConnection(campaign);
            EnsureMbtiRelationshipSchema(c); EnsureRelationshipNativeTargetSchema(c);
            string generation = NpcLivesGeneration(c);
            if (operation == "context")
            {
                if (victor.Length == 0 || prisoner.Length == 0) return TestDict("ok", false, "error", "Both rulers are required.");
                var suppliedProfile = ReadDictionary(p, "victorProfile");
                if (suppliedProfile != null && ReadString(suppliedProfile, "heroStringId", "") != victor)
                    return TestDict("ok", false, "error", "The native personality snapshot must identify the deciding ruler.");
                var traits = RulerFoundationTraits(campaign, victor, suppliedProfile, out string traitSource);
                var scaled = new Dictionary<string, object>();
                foreach (string key in new[] { "mercy", "vengefulness", "aggression", "dutyMotivation", "traditionalism", "compassion", "ambition", "pragmatism", "powerMotivation", "legacyMotivation" })
                    scaled[key] = Trait100(traits, key);
                var rows = new List<Dictionary<string, object>>();
                foreach (var clan in ReadDictionaryList(p, "clans").Take(512))
                {
                    string leader = ReadString(clan, "leaderId", ""); if (leader.Length == 0) continue;
                    rows.Add(TestDict("clanId", ReadString(clan, "clanId", ""), "leaderId", leader,
                        "affinity", RulerPersonalAffinity(c, campaign, timeline, leader, prisoner, day),
                        "affinityToVictor", RulerPersonalAffinity(c, campaign, timeline, leader, victor, day)));
                }
                return TestDict("ok", true, "generation", generation, "traits", scaled, "traitSource", traitSource, "clans", rows,
                    "victorAffinity", RulerPersonalAffinity(c, campaign, timeline, victor, prisoner, day));
            }
            if (operation != "relations") return TestDict("ok", false, "error", "Unknown ruler operation.");
            if (ReadString(p, "generation", "") != generation) return TestDict("ok", false, "error", "Campaign restore generation changed.");
            string id = ReadString(p, "operationId", ""); var deltas = ReadDictionaryList(p, "deltas");
            if (id.Length < 1 || id.Length > 200 || deltas.Count < 1 || deltas.Count > 2048)
                return TestDict("ok", false, "error", "A bounded ruler relationship operation is required.");
            foreach (var row in deltas)
            {
                int delta = ReadInt(row, "delta", 0); string reason = ReadString(row, "reason", "");
                if ((reason != "submission" || delta != 75) && (reason != "unauthorized_execution" || (delta != -30 && delta != -60))
                    && (reason != "native_execution" || delta >= 0 || delta < -100))
                    return TestDict("ok", false, "error", "Unsupported ruler relationship consequence.");
                if (ReadString(row, "subjectId", "").Length == 0 || ReadString(row, "targetId", "").Length == 0)
                    return TestDict("ok", false, "error", "Relationship participants are required.");
            }
            ExecuteSql(c, @"CREATE TABLE IF NOT EXISTS relationship_ruler_receipts(timeline_id TEXT NOT NULL,operation_id TEXT NOT NULL,terms_hash TEXT NOT NULL,PRIMARY KEY(timeline_id,operation_id));");
            string hash = PromptHash(CanonicalJson(deltas));
            using var transaction = c.BeginTransaction();
            var prior = QuerySql(c, "SELECT terms_hash FROM relationship_ruler_receipts WHERE timeline_id=$timeline AND operation_id=$id;", TestDict("timeline", timeline, "id", id)).FirstOrDefault();
            if (prior != null)
            {
                if (ReadString(prior, "terms_hash", "") != hash) return TestDict("ok", false, "error", "Ruler consequence terms changed.");
                return TestDict("ok", true, "operationId", id, "replay", true);
            }
            foreach (var row in deltas)
            {
                string subject = ReadString(row, "subjectId", ""), target = ReadString(row, "targetId", ""); if (subject == target) continue;
                string pairKey = AmbientPairKey(subject, target); var ids = new[] { subject, target }.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                EnsurePoliticalRelationshipPair(c, campaign, timeline, pairKey, ids[0], ids[1], day, true);
                var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", pairKey)).Single();
                string column = ReadString(pair, "hero_a_id", "") == subject ? "affinity_a_to_b" : "affinity_b_to_a";
                int value = Clamp(ReadInt(pair, column, 0) + ReadInt(row, "delta", 0), -100, 100);
                ExecuteSql(c, "UPDATE relationship_pair_chemistry SET " + column + "=$value,state_revision=state_revision+1 WHERE pair_key=$pair;", TestDict("value", value, "pair", pairKey));
                pair[column] = value;
                RefreshEffectivePairProjection(c, campaign, timeline, pair, day, "ruler_" + ReadString(row, "reason", ""));
            }
            ExecuteSql(c, "INSERT INTO relationship_ruler_receipts(timeline_id,operation_id,terms_hash) VALUES($timeline,$id,$hash);", TestDict("timeline", timeline, "id", id, "hash", hash));
            transaction.Commit(); InvalidateRelationshipPairStateCache(campaign);
            return TestDict("ok", true, "operationId", id, "replay", false);
        }
        private static Dictionary<string, object> RulerFoundationTraits(string campaign, string victor,
            Dictionary<string, object> nativeProfile, out string source)
        {
            var stored = ReadDictionary(ReadJsonObject(CharacterFile(campaign, victor, "traits.json")), "foundationTraits");
            if (stored != null && stored.Count > 0)
            {
                source = "campaign_foundation_traits";
                return stored;
            }
            var profile = ReadJsonObject(CharacterFile(campaign, victor, "profile.json"));
            if (nativeProfile != null) foreach (var item in nativeProfile) profile[item.Key] = item.Value;
            var library = CharacterProfileLibraryFor(WithNarrativeCampaignVersion(campaign, profile));
            if (library.TryGetValue(victor, out var shipped))
            {
                var canonical = ReadDictionary(ReadDictionary(shipped, "traits"), "foundationTraits");
                if (canonical != null && canonical.Count > 0)
                {
                    source = "shipped_foundation_traits";
                    return canonical;
                }
            }
            if (ReadDictionary(profile, "traits") != null || ReadDictionary(profile, "skills") != null)
            {
                source = "transient_deterministic_native_traits_and_skills";
                return ReadDictionary(BuildTraitDocument(profile), "foundationTraits") ?? new Dictionary<string, object>();
            }
            source = "neutral_missing_trait_evidence";
            return new Dictionary<string, object>();
        }
        private static int RulerPersonalAffinity(ReignDbConnection c, string campaign, string timeline, string subject, string target, double day)
        {
            if (subject == target) return 100;
            var ids = new[] { subject, target }.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            string key = AmbientPairKey(subject, target);
            EnsurePoliticalRelationshipPair(c, campaign, timeline, key, ids[0], ids[1], day, true);
            var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", key)).FirstOrDefault();
            if (pair == null) throw new InvalidOperationException("Ruler affinity is unavailable.");
            return ReadInt(pair, ReadString(pair, "hero_a_id", "") == subject ? "affinity_a_to_b" : "affinity_b_to_a", 0);
        }
    }
}
