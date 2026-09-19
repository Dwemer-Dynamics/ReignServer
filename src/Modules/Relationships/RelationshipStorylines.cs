using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] RelationshipStoryKinds =
        {
            "romance", "rivalry", "marital_conflict", "shared_secret", "favor_debt"
        };

        private static void EnsureRelationshipStorylineSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_story_threads (
thread_key TEXT PRIMARY KEY,pair_key TEXT NOT NULL,subject_id TEXT NOT NULL,target_id TEXT NOT NULL,
kind TEXT NOT NULL,route TEXT NOT NULL DEFAULT '',intensity REAL NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'active',
qualifying_events INTEGER NOT NULL DEFAULT 0,last_event_type TEXT NOT NULL DEFAULT '',last_event_id TEXT NOT NULL DEFAULT '',
last_event_day REAL NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(subject_id,target_id,kind));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_story_threads_pair ON relationship_story_threads(pair_key,kind,status,last_event_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_story_seeded_pairs (
pair_key TEXT PRIMARY KEY,seeded_day REAL NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL);");
        }

        private static string StoryThreadKey(string subject, string target, string kind)
        {
            return "story|" + (subject ?? "").ToLowerInvariant() + "|" + (target ?? "").ToLowerInvariant() + "|" + (kind ?? "").ToLowerInvariant();
        }

        private static string StoryPairKey(string a, string b)
        {
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + "|" + b : b + "|" + a;
        }

        private static double EffectiveStoryIntensity(double intensity, string kind, string route, double lastDay, double day)
        {
            intensity = ClampDouble(intensity, 0d, 100d);
            double age = Math.Max(0d, day - lastDay);
            int periods = 0;
            if (kind == "romance" || kind == "shared_secret" || kind == "favor_debt")
                periods = age <= 60d ? 0 : (int)Math.Floor((age - 60d) / 30d) + 1;
            else if (kind == "rivalry")
                periods = age <= 120d ? 0 : (int)Math.Floor((age - 120d) / 60d) + 1;
            double result = Math.Max(0d, intensity - periods * 5d);
            if (kind == "rivalry" && route.IndexOf("lasting", StringComparison.OrdinalIgnoreCase) >= 0)
                result = Math.Max(25d, result);
            return result;
        }

        private static string StoryStage(string kind, double intensity)
        {
            if (intensity <= 0d) return "dormant";
            if (kind == "marital_conflict")
                return intensity < 30d ? "strain" : intensity < 60d ? "troubled" : intensity < 80d ? "separation_ready" : "divorce_ready";
            return intensity < 25d ? "emerging" : intensity < 50d ? "active" : intensity < 75d ? "developed" : "major";
        }

        private static double StoryContinuationMultiplier(double intensity, string kind, double lastDay, double day)
        {
            double activeAt = kind == "marital_conflict" ? 30d : 25d;
            if (intensity <= 0d) return 1d;
            if (intensity < activeAt) return 1.5d;
            double age = Math.Max(0d, day - lastDay);
            if (age <= 14d) return 3d;
            if (age <= 30d) return 2.5d;
            if (age <= 60d) return 2d;
            if (age <= 120d) return 1.5d;
            return 1d;
        }

        private static Dictionary<string, object> LoadStoryThread(ReignDbConnection connection, string subject, string target, string kind, double day)
        {
            Dictionary<string, object> row = QuerySql(connection,
                "SELECT * FROM relationship_story_threads WHERE subject_id=$subject AND target_id=$target AND kind=$kind LIMIT 1;",
                new Dictionary<string, object> { ["subject"] = subject, ["target"] = target, ["kind"] = kind }).FirstOrDefault();
            if (row == null) return new Dictionary<string, object>();
            double effective = EffectiveStoryIntensity(ReadDouble(row, "intensity", 0d), kind, ReadString(row, "route", ""), ReadDouble(row, "last_event_day", day), day);
            Dictionary<string, object> metadata = TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
            row["effectiveIntensity"] = effective;
            row["stage"] = StoryStage(kind, effective);
            row["continuationMultiplier"] = StoryContinuationMultiplier(effective, kind, ReadDouble(row, "last_event_day", day), day);
            row["motiveChannel"] = ReadString(metadata, "motiveChannel", "");
            return row;
        }

        private static string RomanticMotiveChannel(Dictionary<string, object> posture)
        {
            if (posture == null || posture.Count == 0) return "";
            string presentation = ReadString(posture, "presentation", "").ToLowerInvariant();
            if (presentation == "sincere" || presentation == "calculated" || presentation == "mixed") return presentation;
            double genuine = ReadDouble(posture, "genuineInterest", 0d), strategic = ReadDouble(posture, "strategicInterest", 0d);
            return genuine >= strategic + 15d ? "sincere" : strategic >= genuine + 15d ? "calculated" : "mixed";
        }

        private static Dictionary<string, object> CompactStoryThread(Dictionary<string, object> thread)
        {
            if (thread == null || thread.Count == 0 || ReadString(thread, "status", "") != "active"
                || ReadDouble(thread, "effectiveIntensity", 0d) <= 0d) return new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["kind"] = ReadString(thread, "kind", ""),
                ["route"] = ReadString(thread, "route", ""),
                ["motiveChannel"] = ReadString(thread, "motiveChannel", ""),
                ["status"] = "active",
                ["intensity"] = Math.Round(ReadDouble(thread, "effectiveIntensity", 0d), 2),
                ["stage"] = ReadString(thread, "stage", ""),
                ["lastEventDay"] = ReadDouble(thread, "last_event_day", 0d)
            };
        }

        private static Dictionary<string, object> LoadPairStorySummary(ReignDbConnection connection, string a, string b, double day)
        {
            EnsureDirectorPairThreadsSeeded(connection, a, b, day);
            List<Dictionary<string, object>> rows = QuerySql(connection,
                "SELECT * FROM relationship_story_threads WHERE pair_key=$pair AND status='active';",
                new Dictionary<string, object> { ["pair"] = StoryPairKey(a, b) });
            double multiplier = 1d;
            Dictionary<string, object> strongest = null;
            foreach (Dictionary<string, object> row in rows)
            {
                string kind = ReadString(row, "kind", "");
                double effective = EffectiveStoryIntensity(ReadDouble(row, "intensity", 0d), kind, ReadString(row, "route", ""), ReadDouble(row, "last_event_day", day), day);
                double candidate = StoryContinuationMultiplier(effective, kind, ReadDouble(row, "last_event_day", day), day);
                row["effectiveIntensity"] = effective;
                row["stage"] = StoryStage(kind, effective);
                row["continuationMultiplier"] = candidate;
                if (candidate > multiplier)
                {
                    multiplier = candidate;
                    strongest = row;
                }
            }
            return new Dictionary<string, object>
            {
                ["multiplier"] = multiplier,
                ["strongest"] = strongest ?? new Dictionary<string, object>(),
                ["threads"] = rows
            };
        }

        private static void EnsureDirectorPairThreadsSeeded(ReignDbConnection connection, string a, string b, double day)
        {
            string pair = StoryPairKey(a, b);
            if (QuerySql(connection, "SELECT pair_key FROM relationship_story_seeded_pairs WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pair }).Any()) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> prior in QuerySql(connection,
                "SELECT * FROM passive_relationship_events WHERE world_day >= $day AND ((hero_a_id=$a AND hero_b_id=$b) OR (hero_a_id=$b AND hero_b_id=$a)) ORDER BY world_day;",
                new Dictionary<string, object> { ["day"] = day - 120d, ["a"] = a, ["b"] = b }))
                ApplyStoryEventToDatabase(connection, ReadString(prior, "kind", ""), ReadString(prior, "event_id", ""), ReadString(prior, "hero_a_id", a),
                    ReadString(prior, "hero_b_id", b), ReadDouble(prior, "world_day", day), false, false);
            try
            {
                foreach (Dictionary<string, object> milestone in QuerySql(connection,
                    "SELECT subject_id,target_id,kind FROM relationship_milestones WHERE status='active' AND ((subject_id=$a AND target_id=$b) OR (subject_id=$b AND target_id=$a));",
                    new Dictionary<string, object> { ["a"] = a, ["b"] = b }))
                {
                    string kind = ReadString(milestone, "kind", "");
                    if (kind == "in_love" || kind == "devoted")
                        UpsertStoryThread(connection, ReadString(milestone, "subject_id", a), ReadString(milestone, "target_id", b), "romance", "sincere", 50d, 1, "lazy_milestone_seed", "", day);
                    else if (kind == "betrayed" || kind == "nemesis")
                        UpsertStoryThread(connection, ReadString(milestone, "subject_id", a), ReadString(milestone, "target_id", b), "rivalry", "lasting", 75d, 2, "lazy_milestone_seed", "", day);
                }
            }
            catch { }
            try
            {
                foreach(Dictionary<string,object> pressure in QuerySql(connection,
                    "SELECT subject_id,target_id,kind,intensity FROM relationship_pressures WHERE status='active' AND updated_day >= $day AND ((subject_id=$a AND target_id=$b) OR (subject_id=$b AND target_id=$a));",
                    new Dictionary<string,object>{{"day",day-120d},{"a",a},{"b",b}}))
                {
                    string pressureKind=ReadString(pressure,"kind",""),storyKind="",route="";
                    if(pressureKind=="strategic_seduction"||pressureKind=="romantic_displacement"){storyKind="romance";route=pressureKind=="strategic_seduction"?"calculated":"sincere";}
                    else if(pressureKind=="marital_discontent"||pressureKind=="affair_exposure"||pressureKind=="divided_loyalty")storyKind="marital_conflict";
                    else if(pressureKind=="humiliation"||pressureKind=="suspected_betrayal"||pressureKind=="ambition_collision"||pressureKind=="status_threat")storyKind="rivalry";
                    else if(pressureKind=="unpaid_obligation")storyKind="favor_debt";
                    else if(pressureKind=="exposure_blackmail")storyKind="shared_secret";
                    if(!string.IsNullOrWhiteSpace(storyKind))SeedStoryThreadAtLeast(connection,ReadString(pressure,"subject_id",a),ReadString(pressure,"target_id",b),
                        storyKind,route,Math.Min(75d,Math.Max(25d,ReadDouble(pressure,"intensity",25d))),"lazy_pressure_seed",day);
                }
                foreach(Dictionary<string,object> development in QuerySql(connection,
                    "SELECT subject_id,target_id,kind FROM relationship_developments WHERE status IN ('ready','claimed') AND available_day >= $day AND ((subject_id=$a AND target_id=$b) OR (subject_id=$b AND target_id=$a));",
                    new Dictionary<string,object>{{"day",day-120d},{"a",a},{"b",b}}))
                {
                    string developmentKind=ReadString(development,"kind","");
                    if(developmentKind=="romantic_turning_point"||developmentKind=="strategic_romantic_entanglement")
                        SeedStoryThreadAtLeast(connection,ReadString(development,"subject_id",a),ReadString(development,"target_id",b),"romance",
                            developmentKind=="strategic_romantic_entanglement"?"calculated":"sincere",50d,"lazy_development_seed",day);
                    else if(developmentKind=="betrayal_confrontation"||developmentKind=="humiliation_reckoning")
                        SeedStoryThreadAtLeast(connection,ReadString(development,"subject_id",a),ReadString(development,"target_id",b),"rivalry","lasting",50d,"lazy_development_seed",day);
                }
            }
            catch { }
            ExecuteSql(connection, "INSERT OR IGNORE INTO relationship_story_seeded_pairs(pair_key,seeded_day,created_ts) VALUES($pair,$day,$ts);",
                new Dictionary<string, object> { ["pair"] = pair, ["day"] = day, ["ts"] = ts });
        }

        private static void SeedStoryThreadAtLeast(ReignDbConnection connection,string subject,string target,string kind,string route,double targetIntensity,string source,double day)
        {
            Dictionary<string,object> existing=LoadStoryThread(connection,subject,target,kind,day);
            double current=ReadDouble(existing,"effectiveIntensity",0d);
            if(current>=targetIntensity)return;
            UpsertStoryThread(connection,subject,target,kind,route,targetIntensity-current,0,source,"",day);
        }

        private static void UpsertStoryThread(ReignDbConnection connection, string subject, string target, string kind, string route,
            double delta, int qualifyingDelta, string eventType, string eventId, double day, string motiveChannel = "")
        {
            if (!RelationshipStoryKinds.Contains(kind, StringComparer.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(target)) return;
            Dictionary<string, object> existing = LoadStoryThread(connection, subject, target, kind, day);
            double current = existing.Count == 0 ? 0d : ReadDouble(existing, "effectiveIntensity", ReadDouble(existing, "intensity", 0d));
            double next = ClampDouble(current + delta, 0d, 100d);
            int qualifying = Math.Max(0, ReadInt(existing, "qualifying_events", 0) + qualifyingDelta);
            string status = next <= 0d ? "dormant" : "active";
            Dictionary<string, object> metadata = TryParseJsonObject(ReadString(existing, "payload_json", "{}")) ?? new Dictionary<string, object>();
            metadata["stage"] = StoryStage(kind, next);
            if (!string.IsNullOrWhiteSpace(motiveChannel)) metadata["motiveChannel"] = motiveChannel;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO relationship_story_threads(thread_key,pair_key,subject_id,target_id,kind,route,intensity,status,qualifying_events,last_event_type,last_event_id,last_event_day,payload_json,created_ts,updated_ts)
VALUES($key,$pair,$subject,$target,$kind,$route,$intensity,$status,$qualifying,$eventType,$event,$day,$payload,$ts,$ts)
ON CONFLICT(subject_id,target_id,kind) DO UPDATE SET route=CASE WHEN $route='' THEN relationship_story_threads.route ELSE $route END,
intensity=$intensity,status=$status,qualifying_events=$qualifying,last_event_type=$eventType,last_event_id=$event,last_event_day=$day,payload_json=$payload,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["key"] = StoryThreadKey(subject, target, kind), ["pair"] = StoryPairKey(subject, target), ["subject"] = subject, ["target"] = target,
                    ["kind"] = kind, ["route"] = route ?? "", ["intensity"] = next, ["status"] = status, ["qualifying"] = qualifying,
                    ["eventType"] = eventType ?? "", ["event"] = eventId ?? "", ["day"] = day,
                    ["payload"] = Json.Serialize(metadata), ["ts"] = ts
                });
            if(kind=="rivalry"&&current<75d&&next>=75d&&qualifying>=2)
                ExecuteSql(connection,@"INSERT OR IGNORE INTO relationship_milestones(milestone_id,subject_id,target_id,kind,status,stability,strength,reason,created_event_id,last_event_id,evidence_json,created_day,last_meaningful_day,updated_ts,payload_json)
VALUES($id,$subject,$target,'feud','active',75,80,'Repeated political conflict escalated into an active feud.',$event,$event,$evidence,$day,$day,$ts,$payload);",
                    new Dictionary<string,object>{{"id","rms_"+Guid.NewGuid().ToString("N")},{"subject",subject},{"target",target},{"event",eventId??""},
                        {"evidence",Json.Serialize(new[]{eventId??""})},{"day",day},{"ts",ts},{"payload",Json.Serialize(new Dictionary<string,object>{{"threadKind","rivalry"},{"intensity",next}})}});
        }

        private static void ApplyStoryEventToDatabase(ReignDbConnection connection, string eventType, string eventId, string a, string b, double day,
            bool aMarried, bool bMarried, Dictionary<string, object> postureA = null, Dictionary<string, object> postureB = null)
        {
            if(!string.IsNullOrWhiteSpace(eventId)&&QuerySql(connection,
                "SELECT thread_key FROM relationship_story_threads WHERE pair_key=$pair AND last_event_id=$event LIMIT 1;",
                new Dictionary<string,object>{{"pair",StoryPairKey(a,b)},{"event",eventId}}).Any())return;
            switch ((eventType ?? "").ToLowerInvariant())
            {
                case "mutual_romantic_flirtation":
                    UpsertStoryThread(connection, a, b, "romance", aMarried || bMarried ? "temptation" : "courtship", 25d, 1, eventType, eventId, day, RomanticMotiveChannel(postureA));
                    UpsertStoryThread(connection, b, a, "romance", aMarried || bMarried ? "temptation" : "courtship", 25d, 1, eventType, eventId, day, RomanticMotiveChannel(postureB));
                    break;
                case "romantic_confidence":
                    UpsertStoryThread(connection, a, b, "romance", aMarried || bMarried ? "temptation" : "courtship", 15d, 1, eventType, eventId, day, RomanticMotiveChannel(postureA));
                    UpsertStoryThread(connection, b, a, "romance", aMarried || bMarried ? "temptation" : "courtship", 15d, 1, eventType, eventId, day, RomanticMotiveChannel(postureB));
                    break;
                case "romantic_intimacy":
                case "secret_affair_intimacy":
                    UpsertStoryThread(connection, a, b, "romance", eventType == "secret_affair_intimacy" ? "affair" : "courtship", 30d, 1, eventType, eventId, day, RomanticMotiveChannel(postureA));
                    UpsertStoryThread(connection, b, a, "romance", eventType == "secret_affair_intimacy" ? "affair" : "courtship", 30d, 1, eventType, eventId, day, RomanticMotiveChannel(postureB));
                    break;
                case "romantic_rejection":
                case "romantic_boundary":
                    UpsertStoryThread(connection, a, b, "romance", "", -30d, 0, eventType, eventId, day, RomanticMotiveChannel(postureA));
                    UpsertStoryThread(connection, b, a, "romance", "", -30d, 0, eventType, eventId, day, RomanticMotiveChannel(postureB));
                    break;
                case "political_rivalry_argument":
                case "political_obstruction":
                    UpsertStoryThread(connection, a, b, "rivalry", "", 20d, 1, eventType, eventId, day);
                    UpsertStoryThread(connection, b, a, "rivalry", "", 20d, 1, eventType, eventId, day);
                    break;
                case "marital_argument":
                    UpsertStoryThread(connection, a, b, "marital_conflict", "", 20d, 1, eventType, eventId, day);
                    UpsertStoryThread(connection, b, a, "marital_conflict", "", 20d, 1, eventType, eventId, day);
                    break;
                case "marital_separation":
                    UpsertStoryThread(connection, a, b, "marital_conflict", "", 20d, 1, eventType, eventId, day);
                    UpsertStoryThread(connection, b, a, "marital_conflict", "", 20d, 1, eventType, eventId, day);
                    break;
                case "private_spousal_confidence":
                    UpsertStoryThread(connection, a, b, "marital_conflict", "", -10d, 0, eventType, eventId, day);
                    UpsertStoryThread(connection, b, a, "marital_conflict", "", -10d, 0, eventType, eventId, day);
                    break;
                case "shared_confidence":
                    UpsertStoryThread(connection, a, b, "shared_secret", "", 15d, 1, eventType, eventId, day);
                    UpsertStoryThread(connection, b, a, "shared_secret", "", 15d, 1, eventType, eventId, day);
                    break;
                case "practical_favor":
                    UpsertStoryThread(connection, b, a, "favor_debt", "", 15d, 1, eventType, eventId, day);
                    break;
            }
        }

        private static double PairStoryKindIntensity(ReignDbConnection connection, string a, string b, string kind, double day, bool bilateral)
        {
            double ab = ReadDouble(LoadStoryThread(connection, a, b, kind, day), "effectiveIntensity", 0d);
            double ba = ReadDouble(LoadStoryThread(connection, b, a, kind, day), "effectiveIntensity", 0d);
            return bilateral ? Math.Min(ab, ba) : Math.Max(ab, ba);
        }

        private static double PairStoryKindMultiplier(ReignDbConnection connection,string a,string b,string kind,double day)
        {
            Dictionary<string,object> ab=LoadStoryThread(connection,a,b,kind,day),ba=LoadStoryThread(connection,b,a,kind,day);
            return Math.Max(ReadDouble(ab,"continuationMultiplier",1d),ReadDouble(ba,"continuationMultiplier",1d));
        }

        private static double ThirdPartyCourtshipArrangementMultiplier(ReignDbConnection connection,string heroId,string proposedSpouseId,double day)
        {
            double strongest=0d;
            foreach(Dictionary<string,object> row in QuerySql(connection,
                "SELECT target_id FROM relationship_story_threads WHERE subject_id=$subject AND kind='romance' AND route='courtship' AND status='active' AND target_id<>$proposed;",
                new Dictionary<string,object>{{"subject",heroId},{"proposed",proposedSpouseId??""}}))
            {
                string partner=ReadString(row,"target_id","");
                strongest=Math.Max(strongest,PairStoryKindIntensity(connection,heroId,partner,"romance",day,true));
            }
            return strongest>=75d?0.50d:strongest>=50d?0.75d:1d;
        }

        private static double ArrangedMarriageCourtshipMultiplier(ReignDbConnection connection,string a,string b,double day)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return 1d;
            int activeThirdPartyLovers = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM relationship_pair_lifecycle
WHERE lover_active=1
  AND ((hero_a_id=$a AND hero_b_id<>$b) OR (hero_b_id=$a AND hero_a_id<>$b)
    OR (hero_a_id=$b AND hero_b_id<>$a) OR (hero_b_id=$b AND hero_a_id<>$a));",
                new Dictionary<string, object> { ["a"] = a, ["b"] = b }).FirstOrDefault(), "count", 0);
            return activeThirdPartyLovers > 0 ? 0.50d : 1d;
        }

        private static int ConvertDisplacedCourtshipsToTemptation(ReignDbConnection connection,string newlywed,string spouse,double day,string eventId)
        {
            List<string> partners=QuerySql(connection,
                "SELECT target_id FROM relationship_story_threads WHERE subject_id=$subject AND kind='romance' AND route='courtship' AND status='active' AND target_id<>$spouse;",
                new Dictionary<string,object>{{"subject",newlywed},{"spouse",spouse}})
                .Select(x=>ReadString(x,"target_id","")).Where(x=>!string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int converted=0;
            foreach(string partner in partners)
            {
                double bilateral=PairStoryKindIntensity(connection,newlywed,partner,"romance",day,true);
                if(bilateral<50d)continue;
                Dictionary<string,object> a=LoadStoryThread(connection,newlywed,partner,"romance",day);
                Dictionary<string,object> b=LoadStoryThread(connection,partner,newlywed,"romance",day);
                UpsertStoryThread(connection,newlywed,partner,"romance","temptation",0d,0,"arranged_marriage_displacement",eventId,day,ReadString(a,"motiveChannel",""));
                UpsertStoryThread(connection,partner,newlywed,"romance","temptation",0d,0,"arranged_marriage_displacement",eventId,day,ReadString(b,"motiveChannel",""));
                Dictionary<string,object> evaluation=new Dictionary<string,object>
                {
                    ["pressureUpdates"]=new List<Dictionary<string,object>>
                    {
                        new Dictionary<string,object>{{"kind","divided_loyalty"},{"operation","create"},{"intensityDelta",Math.Min(75d,bilateral)},
                            {"secrecy",0.9d},{"summary","A political marriage displaced an established private courtship."},{"triggered",false}}
                    }
                };
                ApplyPressureUpdates(connection,newlywed,partner,eventId,day,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),evaluation);
                evaluation["pressureUpdates"]=new List<Dictionary<string,object>>
                {
                    new Dictionary<string,object>{{"kind","romantic_displacement"},{"operation","create"},{"intensityDelta",Math.Min(75d,bilateral)},
                        {"secrecy",0.9d},{"summary","An established courtship was displaced by a political marriage."},{"triggered",false}}
                };
                ApplyPressureUpdates(connection,partner,newlywed,eventId,day,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),evaluation);
                converted++;
            }
            return converted;
        }

        private static bool StoryPhysicalRomanceEligible(bool coLocated,bool relationshipRouteAllowed,double bilateralIntensity,double requiredIntensity,
            Dictionary<string,object> postureA,Dictionary<string,object> postureB)
        {
            return coLocated&&relationshipRouteAllowed&&bilateralIntensity>=requiredIntensity
                &&DirectorRomanceEligible(postureA,60d)&&DirectorRomanceEligible(postureB,60d);
        }
    }
}
