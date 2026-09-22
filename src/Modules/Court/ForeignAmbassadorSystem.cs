using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly Dictionary<string, int> AmbassadorPermissionBaseChances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "trade_agreement", 45 },
            { "caravan_protection", 40 },
            { "supply_agreement", 35 },
            { "prisoner_exchange", 30 },
            { "ransom_package", 30 }
        };

        private static readonly string[] AmbassadorReferralOnlyActions =
        {
            "war", "peace", "alliance", "defensive_pact", "non_aggression_pact", "land_transfer",
            "marriage", "vassalage", "treaty_termination"
        };

        private static void EnsureForeignAmbassadorSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS foreign_ambassador_postings (
posting_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,
origin_kingdom_name TEXT NOT NULL DEFAULT '',origin_ruler_id TEXT NOT NULL,origin_ruler_name TEXT NOT NULL DEFAULT '',host_kingdom_id TEXT NOT NULL,
capital_settlement_id TEXT NOT NULL DEFAULT '',shelter_settlement_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'traveling',
assigned_day REAL NOT NULL,arrival_day REAL NOT NULL,ended_day REAL NOT NULL DEFAULT -1,end_reason TEXT NOT NULL DEFAULT '',charm INTEGER NOT NULL,
ruler_trust INTEGER NOT NULL DEFAULT 0,authority_revision INTEGER NOT NULL DEFAULT 1,authority_charter_json TEXT NOT NULL DEFAULT '{}',
revision INTEGER NOT NULL DEFAULT 1,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_foreign_ambassador_active ON foreign_ambassador_postings(campaign_id,timeline_id,origin_kingdom_id,status,assigned_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_official_archive (
archive_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,
ruler_id_at_turn TEXT NOT NULL,envoy_id TEXT NOT NULL,conversation_session_id TEXT NOT NULL,exchange_id TEXT NOT NULL DEFAULT '',turn_id TEXT NOT NULL,
speaker_id TEXT NOT NULL,speaker_role TEXT NOT NULL,text TEXT NOT NULL,world_day REAL NOT NULL,created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,posting_id,turn_id));" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_ambassador_archive_kingdom ON ambassador_official_archive(campaign_id,timeline_id,origin_kingdom_id,world_day,created_ts);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_referrals (
referral_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,action_id TEXT NOT NULL,
exact_terms_json TEXT NOT NULL,terms_hash TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',outcome TEXT NOT NULL DEFAULT 'refer',
created_day REAL NOT NULL,due_day REAL NOT NULL,resolved_day REAL NOT NULL DEFAULT -1,counter_terms_json TEXT NOT NULL DEFAULT '{}',
authoritative_receipt_json TEXT NOT NULL DEFAULT '{}',revision INTEGER NOT NULL DEFAULT 1,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_ambassador_referrals_due ON ambassador_referrals(campaign_id,timeline_id,status,due_day);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_decisions (
decision_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,conversation_session_id TEXT NOT NULL,
exchange_id TEXT NOT NULL DEFAULT '',requested_action TEXT NOT NULL DEFAULT '',authority_result TEXT NOT NULL,outcome TEXT NOT NULL,
terms_hash TEXT NOT NULL DEFAULT '',referral_id TEXT NOT NULL DEFAULT '',referral_state TEXT NOT NULL DEFAULT '',
authoritative_receipt_json TEXT NOT NULL DEFAULT '{}',created_day REAL NOT NULL,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_pressure_daily (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,host_kingdom_id TEXT NOT NULL,day_index INTEGER NOT NULL,
net_delta INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,origin_kingdom_id,host_kingdom_id,day_index));" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_pressure_audit (
audit_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,
host_kingdom_id TEXT NOT NULL,conversation_session_id TEXT NOT NULL,classification TEXT NOT NULL,requested_delta INTEGER NOT NULL,
applied_delta INTEGER NOT NULL,before_value INTEGER NOT NULL,after_value INTEGER NOT NULL,pressure_channel TEXT NOT NULL,
evidence_turn_ids_json TEXT NOT NULL DEFAULT '[]',world_day REAL NOT NULL,created_ts INTEGER NOT NULL);" );
        }

        private static Dictionary<string, object> ForeignAmbassadorEstablishApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            if (string.IsNullOrWhiteSpace(ctx.CommandId)) return CourtError("commandId is required.");
            string originKingdomId = ReadString(payload, "originKingdomStringId", "");
            string originRulerId = ReadString(payload, "originRulerHeroStringId", "");
            string capitalId = ReadString(payload, "capitalSettlementStringId", "");
            if (string.IsNullOrWhiteSpace(originKingdomId) || string.IsNullOrWhiteSpace(originRulerId) || string.IsNullOrWhiteSpace(capitalId))
                return CourtError("Origin kingdom, ruler, and capital are required.");
            if (!ReadBool(payload, "eventGenerated", false) && ReadInt(payload, "relationWithRuler", -1000) < -30)
                return CourtError("Relations with that ruler must be -30 or better.");
            if (ReadBool(payload, "atWar", false)) return CourtError("Ambassadors cannot be established while the kingdoms are at war.");

            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> existing = QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND origin_kingdom_id=$origin AND status IN ('traveling','resident','sheltered')
ORDER BY assigned_day DESC LIMIT 1;", new Dictionary<string, object>{{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"origin",originKingdomId}}).FirstOrDefault();
                if (existing != null)
                {
                    Dictionary<string, object> duplicate = ForeignAmbassadorPostingResult(ctx, existing, "foreign_ambassador_already_active");
                    StoreCourtCommand(connection, ctx, "foreign_ambassador_establish", duplicate);
                    return duplicate;
                }

                List<Dictionary<string, object>> candidates = ReadDictionaryList(payload, "candidates")
                    .Where(IsValidForeignAmbassadorCandidate)
                    .OrderBy(x => ReadString(x, "heroStringId", ""), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (candidates.Count == 0) return CourtError("No eligible adult, active, free lord or lady with at least 50 Charm is available.");
                int highestBand = candidates.Max(x => Reign.Core.Contracts.Court.ReignInternationalDocketRules.AmbassadorCharmBand(ReadInt(x, "charm", 0)));
                candidates = candidates.Where(x => Reign.Core.Contracts.Court.ReignInternationalDocketRules.AmbassadorCharmBand(ReadInt(x, "charm", 0)) == highestBand).ToList();
                string seed = ctx.CampaignId + "|" + ctx.TimelineId + "|" + originKingdomId + "|" + capitalId + "|resident_ambassador";
                int selectedIndex = StableAmbassadorInt(seed, candidates.Count);
                Dictionary<string, object> selected = candidates[selectedIndex];
                string heroId = ReadString(selected, "heroStringId", "");
                int charm = ReadInt(selected, "charm", 0);
                int trust = ReadInt(selected, "rulerTrust", 0);
                int travelDays = Math.Max(1, Math.Min(3, ReadInt(selected, "travelDays", 3)));
                string postingId = "foreign_ambassador_" + AmbassadorHash(seed + "|" + heroId).Substring(0, 24);
                Dictionary<string, object> charter = BuildAmbassadorAuthorityCharter(ctx.CampaignId, ctx.TimelineId, postingId, 1, charm, trust, originRulerId);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string hostKingdomId = ReadString(payload, "playerKingdomStringId", "");
                ExecuteSql(connection, @"INSERT INTO foreign_ambassador_postings(posting_id,campaign_id,timeline_id,hero_id,origin_kingdom_id,origin_kingdom_name,
origin_ruler_id,origin_ruler_name,host_kingdom_id,capital_settlement_id,status,assigned_day,arrival_day,charm,ruler_trust,authority_revision,
authority_charter_json,revision,payload_json,created_ts,updated_ts)
VALUES($posting,$campaign,$timeline,$hero,$origin,$originName,$ruler,$rulerName,$host,$capital,'traveling',$assigned,$arrival,$charm,$trust,1,$charter,1,$payload,$ts,$ts);",
                    new Dictionary<string, object>
                    {
                        {"posting",postingId},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"hero",heroId},{"origin",originKingdomId},
                        {"originName",ReadString(payload,"originKingdomName",originKingdomId)},{"ruler",originRulerId},{"rulerName",ReadString(payload,"originRulerName",originRulerId)},
                        {"host",hostKingdomId},{"capital",capitalId},{"assigned",ctx.WorldDay},{"arrival",ctx.WorldDay+travelDays},{"charm",charm},{"trust",trust},
                        {"charter",Json.Serialize(charter)},{"payload",Json.Serialize(new Dictionary<string,object>{{"selectionSeedHash",AmbassadorHash(seed)},{"sourceSettlementId",ReadString(selected,"sourceSettlementStringId","")},{"travelDays",travelDays}})},{"ts",now}
                    });
                Dictionary<string, object> row = ReadForeignAmbassadorPosting(connection, ctx, postingId);
                Dictionary<string, object> response = ForeignAmbassadorPostingResult(ctx, row, "foreign_ambassador_established");
                StoreCourtCommand(connection, ctx, "foreign_ambassador_establish", response);
                AddCourtAudit(connection, ctx, "foreign_ambassador", postingId, "establish", "completed", response);
                return response;
            }
        }

        private static Dictionary<string, object> ForeignAmbassadorDismissApi(Dictionary<string, object> payload)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(payload ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
            copy["reason"] = "dismissed";
            return ForeignAmbassadorEndApi(copy);
        }

        private static Dictionary<string, object> ForeignAmbassadorEndApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string postingId = ReadString(payload, "postingId", "");
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || string.IsNullOrWhiteSpace(postingId)) return CourtError("commandId and postingId are required.");
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> row = ReadForeignAmbassadorPosting(connection, ctx, postingId);
                if (row == null) return CourtError("Foreign ambassador posting was not found.");
                long revision = ReadLong(row, "revision", 0);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                string reason = ReadString(payload, "reason", "ended");
                ExecuteSql(connection, @"UPDATE foreign_ambassador_postings SET status='ended',ended_day=$day,end_reason=$reason,revision=revision+1,updated_ts=$ts WHERE posting_id=$posting;",
                    new Dictionary<string, object>{{"day",ctx.WorldDay},{"reason",reason},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"posting",postingId}});
                ExecuteSql(connection, @"UPDATE ambassador_referrals SET status='cancelled_war',outcome='refer',resolved_day=$day,revision=revision+1,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting AND status='pending';",
                    new Dictionary<string, object>{{"day",ctx.WorldDay},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId},{"posting",postingId}});
                Dictionary<string, object> response = ForeignAmbassadorPostingResult(ctx, ReadForeignAmbassadorPosting(connection, ctx, postingId), "foreign_ambassador_ended");
                StoreCourtCommand(connection, ctx, "foreign_ambassador_end", response);
                AddCourtAudit(connection, ctx, "foreign_ambassador", postingId, "end", reason, response);
                return response;
            }
        }

        private static Dictionary<string, object> ForeignAmbassadorLocationApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string postingId = ReadString(payload, "postingId", "");
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || string.IsNullOrWhiteSpace(postingId)) return CourtError("commandId and postingId are required.");
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> row = ReadForeignAmbassadorPosting(connection, ctx, postingId);
                if (row == null) return CourtError("Foreign ambassador posting was not found.");
                long revision = ReadLong(row, "revision", 0);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                string status = ReadString(payload, "status", ReadString(row, "status", "traveling"));
                if (!new[] { "traveling", "resident", "sheltered" }.Contains(status, StringComparer.OrdinalIgnoreCase)) return CourtError("Invalid ambassador location status.");
                ExecuteSql(connection, @"UPDATE foreign_ambassador_postings SET status=$status,capital_settlement_id=$capital,shelter_settlement_id=$shelter,
revision=revision+1,updated_ts=$ts WHERE posting_id=$posting;", new Dictionary<string, object>
                {
                    {"status",status},{"capital",ReadString(payload,"capitalSettlementStringId",ReadString(row,"capital_settlement_id",""))},
                    {"shelter",ReadString(payload,"shelterSettlementStringId",ReadString(row,"shelter_settlement_id",""))},
                    {"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"posting",postingId}
                });
                Dictionary<string, object> response = ForeignAmbassadorPostingResult(ctx, ReadForeignAmbassadorPosting(connection, ctx, postingId), "foreign_ambassador_location_updated");
                StoreCourtCommand(connection, ctx, "foreign_ambassador_location", response);
                return response;
            }
        }

        private static Dictionary<string, object> ForeignAmbassadorAuthorityApi(Dictionary<string, object> payload)
        {
            CourtApiContext ctx = ReadCourtContext(payload);
            string postingId = ReadString(payload, "postingId", "");
            if (string.IsNullOrWhiteSpace(ctx.CommandId) || string.IsNullOrWhiteSpace(postingId)) return CourtError("commandId and postingId are required.");
            using (ReignDbConnection connection = OpenCampaignConnection(ctx.CampaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                if (TryReadCourtCommand(connection, ctx, out Dictionary<string, object> prior)) return prior;
                Dictionary<string, object> row = ReadForeignAmbassadorPosting(connection, ctx, postingId);
                if (row == null) return CourtError("Foreign ambassador posting was not found.");
                long revision = ReadLong(row, "revision", 0);
                if (revision != ctx.ExpectedRevision) return CourtRevisionError(revision);
                long authorityRevision = ReadLong(row, "authority_revision", 1) + 1;
                string rulerId = ReadString(payload, "originRulerHeroStringId", "");
                int charm = ReadInt(payload, "charm", ReadInt(row, "charm", 200));
                int trust = ReadInt(payload, "rulerTrust", ReadInt(row, "ruler_trust", 0));
                Dictionary<string, object> charter = BuildAmbassadorAuthorityCharter(ctx.CampaignId, ctx.TimelineId, postingId, authorityRevision, charm, trust, rulerId);
                ExecuteSql(connection, @"UPDATE foreign_ambassador_postings SET origin_ruler_id=$ruler,origin_ruler_name=$name,charm=$charm,ruler_trust=$trust,
authority_revision=$authorityRevision,authority_charter_json=$charter,revision=revision+1,updated_ts=$ts WHERE posting_id=$posting;", new Dictionary<string, object>
                {
                    {"ruler",rulerId},{"name",ReadString(payload,"originRulerName",rulerId)},{"charm",charm},{"trust",trust},
                    {"authorityRevision",authorityRevision},{"charter",Json.Serialize(charter)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"posting",postingId}
                });
                RelayOfficialArchiveToSuccessor(ctx.CampaignId, ctx.TimelineId, postingId, rulerId);
                Dictionary<string, object> response = ForeignAmbassadorPostingResult(ctx, ReadForeignAmbassadorPosting(connection, ctx, postingId), "foreign_ambassador_authority_refreshed");
                StoreCourtCommand(connection, ctx, "foreign_ambassador_authority", response);
                return response;
            }
        }

        private static bool IsValidForeignAmbassadorCandidate(Dictionary<string, object> candidate)
        {
            return !string.IsNullOrWhiteSpace(ReadString(candidate, "heroStringId", ""))
                && ReadInt(candidate, "charm", 0) >= 50
                && ReadBool(candidate, "isAdult", false)
                && ReadBool(candidate, "isAlive", false)
                && ReadBool(candidate, "isActive", false)
                && ReadBool(candidate, "isLordOrLady", false)
                && !ReadBool(candidate, "isPrisoner", false)
                && !ReadBool(candidate, "isRuler", false)
                && !ReadBool(candidate, "isGovernor", false)
                && !ReadBool(candidate, "isPartyLeader", false)
                && !ReadBool(candidate, "isActiveEnvoy", false);
        }

        private static Dictionary<string, object> BuildAmbassadorAuthorityCharter(string campaignId, string timelineId, string postingId, long revision, int charm, int rulerTrust, string rulerId)
        {
            int charmBonus = Math.Max(0, Math.Min(20, (int)Math.Floor((charm - 200) / 5d)));
            int trustBonus = Math.Max(-10, Math.Min(10, (int)Math.Round(rulerTrust / 5d, MidpointRounding.AwayFromZero)));
            List<Dictionary<string, object>> permissions = new List<Dictionary<string, object>>();
            foreach (KeyValuePair<string, int> permission in AmbassadorPermissionBaseChances.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                int chance = Math.Max(10, Math.Min(85, permission.Value + charmBonus + trustBonus));
                int roll = StableAmbassadorInt(campaignId + "|" + timelineId + "|" + postingId + "|" + permission.Key + "|" + revision, 100) + 1;
                permissions.Add(new Dictionary<string, object>
                {
                    {"actionId",permission.Key},{"baseChance",permission.Value},{"charmBonus",charmBonus},{"rulerRelationBonus",trustBonus},
                    {"permissionChance",chance},{"roll",roll},{"granted",roll<=chance}
                });
            }
            return new Dictionary<string, object>
            {
                {"postingId",postingId},{"revision",revision},{"rulerId",rulerId},{"generatedFromCharm",charm},{"generatedFromRulerRelation",rulerTrust},
                {"permissions",permissions},{"referralOnlyActions",new ArrayList(AmbassadorReferralOnlyActions)},
                {"prohibitions",new ArrayList{"No war, peace, alliance, pact, land transfer, marriage, vassalage, or treaty-termination decision may be made without ruler referral.","No agreement is in force without an authoritative action receipt."}}
            };
        }

        private static string BuildAmbassadorRolePrompt(string campaignId, string heroId, string heroName, Dictionary<string, object> turnPayload)
        {
            Dictionary<string, object> context = ReadDictionary(turnPayload, "ambassadorContext") ?? new Dictionary<string, object>();
            string postingId = ReadString(context, "postingId", "");
            string timelineId = ReadString(turnPayload, "timelineId", "main");
            bool officialMode = string.Equals(ReadString(turnPayload, "conversationMode", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase)
                || ReadBool(turnPayload, "officialMemoryFirewall", false);
            Dictionary<string, object> row = null;
            if (!string.IsNullOrWhiteSpace(postingId) || !string.IsNullOrWhiteSpace(heroId))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureForeignAmbassadorSchema(connection);
                    if (!string.IsNullOrWhiteSpace(postingId))
                    {
                        row = QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                            new Dictionary<string, object>{{"posting",postingId},{"campaign",campaignId},{"timeline",timelineId}}).FirstOrDefault();
                    }
                    else
                    {
                        row = QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero
AND status IN ('traveling','resident','sheltered') ORDER BY assigned_day DESC LIMIT 1;",
                            new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"hero",heroId}}).FirstOrDefault();
                    }
                }
            }
            if (row == null && !officialMode) return string.Empty;
            postingId = FirstNonEmpty(postingId, ReadString(row, "posting_id", ""));
            Dictionary<string, object> charter = row == null
                ? ReadDictionary(context, "authorityCharter") ?? new Dictionary<string, object>()
                : ParseAmbassadorJsonObject(ReadString(row, "authority_charter_json", "{}"));
            string rulerName = FirstNonEmpty(ReadString(row, "origin_ruler_name", ""), ReadString(context, "representedRulerName", ""), ReadString(context, "representedRulerId", "unknown ruler"));
            string kingdomName = FirstNonEmpty(ReadString(row, "origin_kingdom_name", ""), ReadString(context, "representedKingdomName", ""), ReadString(context, "representedKingdomId", "unknown kingdom"));
            string capitalName = FirstNonEmpty(ReadString(context, "capitalSettlementName", ""), ReadString(row, "capital_settlement_id", ""), "the host capital");
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(LoadPromptTemplate("ambassador_role.txt").Trim());
            builder.AppendLine();
            builder.AppendLine("CURRENT OFFICIAL POSTING AND IDENTITY");
            builder.AppendLine("- Envoy: " + FirstNonEmpty(heroName, heroId, "unknown envoy") + " (" + heroId + ")");
            builder.AppendLine("- Represented ruler: " + rulerName + " (" + FirstNonEmpty(ReadString(row, "origin_ruler_id", ""), ReadString(context, "representedRulerId", "")) + ")");
            builder.AppendLine("- Represented kingdom: " + kingdomName + " (" + FirstNonEmpty(ReadString(row, "origin_kingdom_id", ""), ReadString(context, "representedKingdomId", "")) + ")");
            builder.AppendLine("- Posting id: " + postingId);
            builder.AppendLine("- Host capital: " + capitalName);
            builder.AppendLine("- Purpose: maintain the resident diplomatic channel, faithfully relay this official discussion, protect the represented realm's interests, and negotiate only within the charter.");
            builder.AppendLine("- Loyalty: the envoy owes primary political loyalty to the represented ruler and kingdom while retaining personal judgment and interests.");
            builder.AppendLine();
            builder.AppendLine("IMMUTABLE AUTHORITY SNAPSHOT FOR THIS CONVERSATION");
            builder.AppendLine(CourtCanonicalJson(charter));
            builder.AppendLine("This charter remains authoritative in every setting. A granted entry permits this envoy to negotiate and conclude that national action wherever the conversation occurs, but only after explicit agreement on exact terms and an authoritative action receipt. Every omitted, ungranted, prohibited, or referral-only action requires ruler referral. There are no clan-only diplomatic agreements: diplomacy is always made for the represented kingdom. If earlier dialogue or derived memory described a valid ambassador agreement as personal or clan-only, preserve the authoritative native agreement but correct its provenance when discussing it: it was concluded for the represented kingdom under this charter, never for the envoy's clan.");
            if (!officialMode)
            {
                builder.AppendLine();
                builder.AppendLine("CURRENT SETTING IS NOT AN OFFICIAL AMBASSADOR AUDIENCE");
                builder.AppendLine("Never forget or contradict this posting in social, castle, party, event, correspondence, or other conversation. Let the role naturally inform loyalties, knowledge, concerns, and self-description when relevant.");
                builder.AppendLine("Informal setting changes protocol, not identity or granted authority. Do not treat casual words as acceptance; if exact national terms are explicitly accepted and the charter grants the action, the envoy may conclude it here. Referral-only business must move to official Ambassador Mode and await the represented ruler.");
            }
            return builder.ToString().Trim();
        }

        private static Dictionary<string, object> ValidateOfficialAmbassadorContext(string campaignId, Dictionary<string, object> payload)
        {
            Dictionary<string, object> context = ReadDictionary(payload, "ambassadorContext");
            if (context == null) return CourtError("Official Ambassador Mode requires a posting context.");
            string postingId = ReadString(context, "postingId", "");
            string heroId = ReadString(payload, "heroStringId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                Dictionary<string, object> row = QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object>{{"posting",postingId},{"campaign",campaignId},{"timeline",timelineId}}).FirstOrDefault();
                if (row == null || !new[] { "resident", "sheltered" }.Contains(ReadString(row, "status", ""), StringComparer.OrdinalIgnoreCase))
                    return CourtError("The official ambassador posting is not active and resident.");
                if (!string.Equals(ReadString(row, "hero_id", ""), heroId, StringComparison.OrdinalIgnoreCase))
                    return CourtError("The requested speaker is not the envoy assigned to this posting.");
                string contextKingdom = ReadString(context, "representedKingdomId", "");
                if (!string.Equals(ReadString(row, "origin_kingdom_id", ""), contextKingdom, StringComparison.OrdinalIgnoreCase))
                    return CourtError("The represented kingdom does not match the posting.");
                if (ReadLong(context, "authorityRevision", 0) != ReadLong(row, "authority_revision", 1))
                    return CourtError("The ambassador's authority charter changed. Close and reopen Ambassador Mode.");
                Dictionary<string, object> suppliedCharter = ReadDictionary(context, "authorityCharter") ?? new Dictionary<string, object>();
                Dictionary<string, object> currentCharter = ParseAmbassadorJsonObject(ReadString(row, "authority_charter_json", "{}"));
                if (!string.Equals(CourtHash(CourtCanonicalJson(suppliedCharter)), CourtHash(CourtCanonicalJson(currentCharter)), StringComparison.OrdinalIgnoreCase))
                    return CourtError("The immutable authority snapshot does not match the posting charter.");
                return null;
            }
        }

        private static bool HasActiveAmbassadorPosting(string campaignId, string timelineId, string heroId, string postingId)
        {
            if (string.IsNullOrWhiteSpace(heroId) && string.IsNullOrWhiteSpace(postingId)) return false;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                Dictionary<string, object> row = !string.IsNullOrWhiteSpace(postingId)
                    ? QuerySql(connection, @"SELECT posting_id FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting
AND status IN ('traveling','resident','sheltered') LIMIT 1;",
                        new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId}}).FirstOrDefault()
                    : QuerySql(connection, @"SELECT posting_id FROM foreign_ambassador_postings
WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero
AND status IN ('traveling','resident','sheltered') LIMIT 1;",
                        new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"hero",heroId}}).FirstOrDefault();
                return row != null;
            }
        }

        private static List<Dictionary<string, object>> FilterOfficialAmbassadorActionCandidates(
            string campaignId, Dictionary<string, object> payload, List<Dictionary<string, object>> candidates,
            Dictionary<string, object> actionGate, string playerText, string visibleReply,
            out Dictionary<string, object> decision)
        {
            candidates = candidates ?? new List<Dictionary<string, object>>();
            Dictionary<string, object> context = ReadDictionary(payload, "ambassadorContext") ?? new Dictionary<string, object>();
            string postingId = ReadString(context, "postingId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            string heroId = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId");
            bool officialMode = string.Equals(ReadString(payload, "conversationMode", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase)
                || ReadBool(payload, "officialMemoryFirewall", false);
            Dictionary<string, object> posting;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                posting = !string.IsNullOrWhiteSpace(postingId)
                    ? QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline AND status IN ('traveling','resident','sheltered') LIMIT 1;",
                        new Dictionary<string, object>{{"posting",postingId},{"campaign",campaignId},{"timeline",timelineId}}).FirstOrDefault()
                    : QuerySql(connection, @"SELECT * FROM foreign_ambassador_postings WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero
AND status IN ('traveling','resident','sheltered') ORDER BY assigned_day DESC LIMIT 1;",
                        new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"hero",heroId}}).FirstOrDefault();
            }
            if (posting == null)
            {
                decision = NewAmbassadorDecision("", "not_an_active_ambassador", "none", "", "", "not_created", new Dictionary<string, object>());
                return candidates;
            }
            postingId = ReadString(posting, "posting_id", postingId);
            Dictionary<string, object> charter = ParseAmbassadorJsonObject(ReadString(posting, "authority_charter_json", "{}"));
            Dictionary<string, object> candidate = candidates.FirstOrDefault();
            if (candidate == null)
            {
                string commitment = ReadString(actionGate, "commitment", "roleplay_only").ToLowerInvariant();
                string outcome = commitment.Contains("counter") ? "counter" : commitment.Contains("refus") ? "refuse" : commitment.Contains("refer") ? "refer" : "none";
                decision = NewAmbassadorDecision("", "no_action_requested", outcome, "", "", "", new Dictionary<string, object>());
                return new List<Dictionary<string, object>>();
            }

            string command = ReadFirstString(candidate, "command", "action", "commandType", "intent", "type");
            string actionId = CanonicalAmbassadorActionId(command);
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? candidate;
            string termsHash = CourtHash(CourtCanonicalJson(terms));
            Dictionary<string, object> permission = ReadDictionaryList(charter, "permissions")
                .FirstOrDefault(x => string.Equals(ReadString(x, "actionId", ""), actionId, StringComparison.OrdinalIgnoreCase));
            bool granted = permission != null && ReadBool(permission, "granted", false);
            bool referralOnly = IsAmbassadorReferralOnlyAction(actionId, command);
            if (granted && !referralOnly)
            {
                Dictionary<string, object> receipt = new Dictionary<string, object>
                {
                    ["authorityKind"] = "ambassador_charter_direct",
                    ["postingId"] = postingId,
                    ["authorityRevision"] = ReadLong(posting, "authority_revision", ReadLong(charter, "revision", 1)),
                    ["acceptedByHeroStringId"] = ReadString(posting, "hero_id", heroId),
                    ["representedKingdomId"] = ReadString(posting, "origin_kingdom_id", ""),
                    ["representedRulerHeroStringId"] = ReadString(posting, "origin_ruler_id", ""),
                    ["hostKingdomId"] = ReadString(posting, "host_kingdom_id", ""),
                    ["actionId"] = actionId
                };
                terms["authorityReceipt"] = receipt;
                candidate["terms"] = terms;
                candidate["acceptedByHeroStringId"] = ReadString(posting, "hero_id", heroId);
                decision = NewAmbassadorDecision(actionId, "granted", "accept", CourtHash(CourtCanonicalJson(terms)), "", "not_required", receipt);
                return new List<Dictionary<string, object>> { candidate };
            }

            if (referralOnly || permission != null)
            {
                if (!officialMode)
                {
                    decision = NewAmbassadorDecision(actionId, "official_mode_required", "refer", termsHash,
                        "", "not_created", new Dictionary<string, object>());
                    return new List<Dictionary<string, object>>();
                }
                Dictionary<string, object> referral = CreateAmbassadorReferral(campaignId, timelineId, postingId, actionId, terms, termsHash, ReadDouble(payload, "worldDay", 0d));
                decision = NewAmbassadorDecision(actionId, referralOnly ? "referral_required" : "permission_not_granted", "refer", termsHash,
                    ReadString(referral, "referralId", ""), ReadString(referral, "status", "pending"), new Dictionary<string, object>());
                return new List<Dictionary<string, object>>();
            }

            decision = NewAmbassadorDecision(actionId, "prohibited", "refuse", termsHash, "", "not_created", new Dictionary<string, object>());
            return new List<Dictionary<string, object>>();
        }

        private static void CompleteOfficialAmbassadorDecision(Dictionary<string, object> payload, List<Dictionary<string, object>> queued, List<string> errors)
        {
            Dictionary<string, object> decision = ReadDictionary(payload, "ambassadorDecision") ?? NewAmbassadorDecision("", "no_action_requested", "none", "", "", "", new Dictionary<string, object>());
            if (ReadString(decision, "authorityResult", "") == "granted")
            {
                decision["referralState"] = (queued?.Count ?? 0) > 0 ? "pending_authoritative_execution" : "validation_failed";
                if ((queued?.Count ?? 0) == 0 && (errors?.Count ?? 0) > 0) decision["validationError"] = string.Join("; ", errors.Take(3));
            }
            payload["ambassadorDecision"] = decision;
        }

        private static Dictionary<string, object> CreateAmbassadorReferral(string campaignId, string timelineId, string postingId, string actionId,
            Dictionary<string, object> terms, string termsHash, double worldDay)
        {
            string referralId = "ambassador_referral_" + AmbassadorHash(campaignId + "|" + timelineId + "|" + postingId + "|" + actionId + "|" + termsHash).Substring(0, 24);
            int delay = StableAmbassadorInt(referralId + "|delay", 3) + 1;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(connection, @"INSERT OR IGNORE INTO ambassador_referrals(referral_id,campaign_id,timeline_id,posting_id,action_id,exact_terms_json,terms_hash,
status,outcome,created_day,due_day,created_ts,updated_ts) VALUES($id,$campaign,$timeline,$posting,$action,$terms,$hash,'pending','refer',$day,$due,$ts,$ts);",
                    new Dictionary<string, object>{{"id",referralId},{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId},{"action",actionId},
                    {"terms",CourtCanonicalJson(terms)},{"hash",termsHash},{"day",worldDay},{"due",worldDay+delay},{"ts",ts}});
            }
            return new Dictionary<string, object>{{"referralId",referralId},{"status","pending"},{"dueDay",worldDay+delay},{"termsHash",termsHash}};
        }

        private static Dictionary<string, object> NewAmbassadorDecision(string action, string authorityResult, string outcome, string termsHash,
            string referralId, string referralState, Dictionary<string, object> receipt)
        {
            return new Dictionary<string, object>
            {
                {"requestedAction",action??""},{"authorityResult",authorityResult??""},{"outcome",outcome??"none"},{"exactTermsHash",termsHash??""},
                {"referralId",referralId??""},{"referralState",referralState??""},{"authoritativeReceipt",receipt??new Dictionary<string,object>()}
            };
        }

        private static string CanonicalAmbassadorActionId(string command)
        {
            string value = (command ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Contains("trade_agreement")) return "trade_agreement";
            if (value.Contains("caravan_protection")) return "caravan_protection";
            if (value.Contains("supply_agreement")) return "supply_agreement";
            if (value.Contains("exchange_prisoner") || value.Contains("prisoner_exchange")) return "prisoner_exchange";
            if (value.Contains("ransom_package")) return "ransom_package";
            if (value.Contains("declare_war")) return "war";
            if (value.Contains("peace")) return "peace";
            if (value.Contains("alliance")) return "alliance";
            if (value.Contains("defensive_pact")) return "defensive_pact";
            if (value.Contains("non_aggression") || value.Contains("nonaggression")) return "non_aggression_pact";
            if (value.Contains("settlement") || value.Contains("land_transfer")) return "land_transfer";
            if (value.Contains("marriage")) return "marriage";
            if (value.Contains("vassal")) return "vassalage";
            if (value.Contains("terminate") || value.Contains("break_treaty")) return "treaty_termination";
            if (value.Contains("diplomatic_package")) return "diplomatic_package";
            return value;
        }

        private static bool IsAmbassadorReferralOnlyAction(string actionId, string command)
        {
            return AmbassadorReferralOnlyActions.Contains(actionId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                || string.Equals(actionId, "diplomatic_package", StringComparison.OrdinalIgnoreCase)
                || ContainsAny(command ?? string.Empty, "declare_war", "make_peace", "alliance", "pact", "settlement", "marriage", "vassal", "terminate");
        }

        private static List<Dictionary<string, object>> ReadOfficialAmbassadorArchiveLines(string campaignId, Dictionary<string, object> payload, int limit)
        {
            Dictionary<string, object> context = ReadDictionary(payload, "ambassadorContext") ?? new Dictionary<string, object>();
            string postingId = ReadString(context, "postingId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            if (string.IsNullOrWhiteSpace(postingId)) return new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                return QuerySql(connection, @"SELECT * FROM ambassador_official_archive WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting
ORDER BY created_ts DESC LIMIT $limit;", new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId},{"limit",Math.Max(1,limit)}})
                    .OrderBy(x => ReadLong(x, "created_ts", 0)).Select(x => new Dictionary<string, object>
                    {
                        {"id",ReadString(x,"turn_id","")},{"turnId",ReadString(x,"turn_id","")},{"sessionId",ReadString(x,"conversation_session_id","")},
                        {"exchangeId",ReadString(x,"exchange_id","")},{"role",ReadString(x,"speaker_role","")},{"speaker",ReadString(x,"speaker_id","")},
                        {"speakerHeroStringId",ReadString(x,"speaker_id","")},{"text",ReadString(x,"text","")},{"channel","ambassador_official"},
                        {"worldDay",ReadDouble(x,"world_day",0d)},{"postingId",postingId}
                    }).ToList();
            }
        }

        private static void RecordOfficialAmbassadorExchange(string campaignId, Dictionary<string, object> payload, string envoyId,
            string playerId, Dictionary<string, object> playerLine, Dictionary<string, object> npcLine, Dictionary<string, object> conversationExchange, long ts)
        {
            Dictionary<string, object> context = ReadDictionary(payload, "ambassadorContext") ?? new Dictionary<string, object>();
            string postingId = ReadString(context, "postingId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            string sessionId = ReadString(conversationExchange, "sessionId", "");
            string exchangeId = ReadString(conversationExchange, "exchangeId", "");
            List<string> turnIds = ReadStringList(conversationExchange, "turnIds");
            string rulerId = ReadString(context, "representedRulerId", "");
            string originKingdomId = ReadString(context, "representedKingdomId", "");
            if (string.IsNullOrWhiteSpace(postingId) || string.IsNullOrWhiteSpace(rulerId) || turnIds.Count < 2) return;
            Dictionary<string, object>[] lines = { playerLine, npcLine };
            string[] speakerIds = { playerId, envoyId };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                for (int i = 0; i < 2; i++)
                {
                    Dictionary<string, object> line = lines[i];
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO ambassador_official_archive(archive_id,campaign_id,timeline_id,posting_id,origin_kingdom_id,
ruler_id_at_turn,envoy_id,conversation_session_id,exchange_id,turn_id,speaker_id,speaker_role,text,world_day,created_ts)
VALUES($archive,$campaign,$timeline,$posting,$kingdom,$ruler,$envoy,$session,$exchange,$turn,$speaker,$role,$text,$day,$ts);",
                        new Dictionary<string, object>{{"archive","ambassador_archive_"+turnIds[i]},{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId},
                        {"kingdom",originKingdomId},{"ruler",rulerId},{"envoy",envoyId},{"session",sessionId},{"exchange",exchangeId},{"turn",turnIds[i]},
                        {"speaker",speakerIds[i]??""},{"role",ReadString(line,"role","")},{"text",ReadString(line,"text","")},{"day",ReadDouble(payload,"worldDay",0d)},{"ts",ts+i}});
                }
            }
            foreach (Dictionary<string, object> line in lines)
            {
                Dictionary<string, object> rulerLine = new Dictionary<string, object>(line, StringComparer.OrdinalIgnoreCase)
                {
                    ["channel"] = "ambassador_official",
                    ["postingId"] = postingId,
                    ["representedKingdomId"] = originKingdomId,
                    ["relayedByEnvoyId"] = envoyId,
                    ["officialRelay"] = true
                };
                AppendJsonLineToPath(CharacterFile(campaignId, rulerId, "history", "dialogue.jsonl"), rulerLine);
            }
        }

        private static Dictionary<string, object> PersistOfficialAmbassadorDecision(string campaignId, Dictionary<string, object> payload,
            Dictionary<string, object> conversationExchange, long ts)
        {
            Dictionary<string, object> decision = ReadDictionary(payload, "ambassadorDecision")
                ?? NewAmbassadorDecision("", "no_action_requested", "none", "", "", "", new Dictionary<string, object>());
            Dictionary<string, object> context = ReadDictionary(payload, "ambassadorContext") ?? new Dictionary<string, object>();
            string postingId = ReadString(context, "postingId", "");
            string timelineId = ReadString(payload, "timelineId", "main");
            string decisionId = "ambassador_decision_" + AmbassadorHash(postingId + "|" + ReadString(conversationExchange, "exchangeId", "")).Substring(0, 24);
            decision["decisionId"] = decisionId;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                ExecuteSql(connection, @"INSERT OR REPLACE INTO ambassador_decisions(decision_id,campaign_id,timeline_id,posting_id,conversation_session_id,exchange_id,
requested_action,authority_result,outcome,terms_hash,referral_id,referral_state,authoritative_receipt_json,created_day,payload_json,created_ts)
VALUES($id,$campaign,$timeline,$posting,$session,$exchange,$action,$authority,$outcome,$hash,$referral,$state,$receipt,$day,'{}',$ts);",
                    new Dictionary<string, object>{{"id",decisionId},{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId},
                    {"session",ReadString(conversationExchange,"sessionId","")},{"exchange",ReadString(conversationExchange,"exchangeId","")},
                    {"action",ReadString(decision,"requestedAction","")},{"authority",ReadString(decision,"authorityResult","")},{"outcome",ReadString(decision,"outcome","none")},
                    {"hash",ReadString(decision,"exactTermsHash","")},{"referral",ReadString(decision,"referralId","")},{"state",ReadString(decision,"referralState","")},
                    {"receipt",Json.Serialize(ReadDictionary(decision,"authoritativeReceipt")??new Dictionary<string,object>())},{"day",ReadDouble(payload,"worldDay",0d)},{"ts",ts}});
            }
            return decision;
        }

        private static Dictionary<string, object> FinalizeOfficialAmbassadorScene(string campaignId, Dictionary<string, object> session,
            List<Dictionary<string, object>> turns, Dictionary<string, object> scene, double worldDay, long ts,
            string forcedTestClassification = "")
        {
            if (!string.Equals(ReadString(session, "channel", ""), "ambassador_official", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>();
            Dictionary<string, object> sessionPayload = ParseAmbassadorJsonObject(ReadString(session, "payload_json", "{}"));
            Dictionary<string, object> context = ReadDictionary(sessionPayload, "ambassadorContext") ?? new Dictionary<string, object>();
            string postingId = ReadString(context, "postingId", "");
            string timelineId = ReadString(sessionPayload, "timelineId", "main");
            Dictionary<string, object> posting;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                posting = QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object>{{"posting",postingId},{"campaign",campaignId},{"timeline",timelineId}}).FirstOrDefault();
            }
            if (posting == null) return new Dictionary<string, object>{{"processed",false},{"reason","posting_not_found"}};
            Dictionary<string, object> classification = string.IsNullOrWhiteSpace(forcedTestClassification)
                ? ClassifyAmbassadorScene(campaignId, postingId, turns)
                : new Dictionary<string, object>
                {
                    {"classification",NormalizeAmbassadorPressureClassification(forcedTestClassification)},
                    {"evidenceTurnIds",(turns ?? new List<Dictionary<string,object>>()).Select(x => ReadString(x,"turn_id","")).Where(x => !string.IsNullOrWhiteSpace(x)).Take(6).ToList()},
                    {"classifier","forced_test_hook"}
                };
            string label = ReadString(classification, "classification", "neutral");
            int requestedDelta = AmbassadorPressureDelta(label);
            string originKingdomId = ReadString(posting, "origin_kingdom_id", "");
            string hostKingdomId = ReadString(posting, "host_kingdom_id", "");
            int dayIndex = (int)Math.Floor(worldDay);
            int priorDaily;
            int afterDaily;
            int appliedDelta;
            int pressureBefore = ReadPoliticalPressureValue(campaignId, timelineId, originKingdomId, hostKingdomId);
            int pressureAfter = pressureBefore;
            string auditId = "ambassador_pressure_" + AmbassadorHash(campaignId + "|" + timelineId + "|" + ReadString(session, "session_id", "")).Substring(0, 24);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                priorDaily = ReadInt(QuerySql(connection, @"SELECT net_delta FROM ambassador_pressure_daily WHERE campaign_id=$campaign AND timeline_id=$timeline
AND origin_kingdom_id=$origin AND host_kingdom_id=$host AND day_index=$day LIMIT 1;", new Dictionary<string, object>
                    {{"campaign",campaignId},{"timeline",timelineId},{"origin",originKingdomId},{"host",hostKingdomId},{"day",dayIndex}}).FirstOrDefault(), "net_delta", 0);
                afterDaily = ClampAmbassadorDailyPressure(priorDaily, requestedDelta);
                appliedDelta = afterDaily - priorDaily;
                ExecuteSql(connection, @"INSERT INTO ambassador_pressure_daily(campaign_id,timeline_id,origin_kingdom_id,host_kingdom_id,day_index,net_delta,updated_ts)
VALUES($campaign,$timeline,$origin,$host,$day,$delta,$ts)
ON CONFLICT(campaign_id,timeline_id,origin_kingdom_id,host_kingdom_id,day_index) DO UPDATE SET net_delta=$delta,updated_ts=$ts;",
                    new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"origin",originKingdomId},{"host",hostKingdomId},{"day",dayIndex},{"delta",afterDaily},{"ts",ts}});
            }
            if (appliedDelta != 0 && !string.IsNullOrWhiteSpace(originKingdomId) && !string.IsNullOrWhiteSpace(hostKingdomId))
            {
                Dictionary<string, object> actor = new Dictionary<string, object>
                {
                    {"kingdomId",originKingdomId},{"name",ReadString(posting,"origin_kingdom_name",originKingdomId)},{"leaderHeroId",ReadString(posting,"origin_ruler_id","")}
                };
                Dictionary<string, object> target = new Dictionary<string, object>
                {
                    {"kingdomId",hostKingdomId},{"name",ReadString(context,"hostKingdomName",hostKingdomId)},{"leaderHeroId",ReadString(session,"player_id","")}
                };
                pressureAfter = ApplyPoliticalPressureDelta(campaignId, timelineId, worldDay, actor, target, appliedDelta,
                    requestedDelta > 0 ? "treaty_strain" : "trade", auditId);
            }
            List<string> evidenceTurnIds = ReadStringList(classification, "evidenceTurnIds");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, @"INSERT OR REPLACE INTO ambassador_pressure_audit(audit_id,campaign_id,timeline_id,posting_id,origin_kingdom_id,
host_kingdom_id,conversation_session_id,classification,requested_delta,applied_delta,before_value,after_value,pressure_channel,evidence_turn_ids_json,world_day,created_ts)
VALUES($id,$campaign,$timeline,$posting,$origin,$host,$session,$classification,$requested,$applied,$before,$after,$channel,$evidence,$day,$ts);",
                    new Dictionary<string, object>{{"id",auditId},{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId},{"origin",originKingdomId},{"host",hostKingdomId},
                    {"session",ReadString(session,"session_id","")},{"classification",label},{"requested",requestedDelta},{"applied",appliedDelta},{"before",pressureBefore},{"after",pressureAfter},
                    {"channel",requestedDelta>0?"treaty_strain":"trade"},{"evidence",Json.Serialize(evidenceTurnIds)},{"day",worldDay},{"ts",ts}});
            }
            return new Dictionary<string, object>
            {
                {"processed",true},{"classification",label},{"evidenceTurnIds",evidenceTurnIds},{"requestedPressureDelta",requestedDelta},
                {"appliedPressureDelta",appliedDelta},{"dailyBefore",priorDaily},{"dailyAfter",afterDaily},{"pressureBefore",pressureBefore},{"pressureAfter",pressureAfter},
                {"pressureChannel",requestedDelta>0?"treaty_strain":"trade"},{"sceneSummary",LimitText(ReadFirstString(scene,"summary","text","memory"),800)}
            };
        }

        private static Dictionary<string, object> ClassifyAmbassadorScene(string campaignId, string postingId, List<Dictionary<string, object>> turns)
        {
            List<Dictionary<string, object>> transcript = (turns ?? new List<Dictionary<string, object>>()).Select(x => new Dictionary<string, object>
            {
                {"turnId",ReadString(x,"turn_id","")},{"role",ReadString(x,"role","")},{"text",LimitText(ReadString(x,"text",""),1200)}
            }).ToList();
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                {"requestType","ambassador_tone_classifier"},{"campaignId",campaignId},{"correlationId",postingId+":tone:"+Guid.NewGuid().ToString("N")},
                {"reasoningDisabled",true},{"temperature",0d},{"maxTokens",180},{"response_format",new Dictionary<string,object>{{"type","json_object"}}},
                {"system","Classify only the overall official diplomatic tone toward the player's kingdom as exactly one of: strongly conciliatory, conciliatory, neutral, hostile, strongly hostile. Judge threats, contempt, provocation, reassurance, compromise, respect, and de-escalation. Return JSON {classification,evidenceTurnIds}. Evidence IDs must come from the supplied transcript. Do not return reasoning."},
                {"prompt",Json.Serialize(transcript)}
            };
            Dictionary<string, object> llm = ChatWithLlm(request);
            Dictionary<string, object> parsed = ReadBool(llm, "ok", false) ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
            string label = NormalizeAmbassadorPressureClassification(ReadString(parsed, "classification", ""));
            if (string.IsNullOrWhiteSpace(label)) label = DeterministicAmbassadorToneFallback(transcript);
            HashSet<string> validTurnIds = new HashSet<string>(transcript.Select(x => ReadString(x, "turnId", "")).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            List<string> evidence = ReadStringList(parsed, "evidenceTurnIds").Where(validTurnIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            if (evidence.Count == 0) evidence = validTurnIds.Take(6).ToList();
            return new Dictionary<string, object>{{"classification",label},{"evidenceTurnIds",evidence},{"classifier",ReadBool(llm,"ok",false)?"llm":"deterministic_fallback"}};
        }

        private static string NormalizeAmbassadorPressureClassification(string value)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', ' ');
            return new[] { "strongly conciliatory", "conciliatory", "neutral", "hostile", "strongly hostile" }.Contains(normalized) ? normalized : string.Empty;
        }

        private static string DeterministicAmbassadorToneFallback(List<Dictionary<string, object>> transcript)
        {
            string text = string.Join(" ", (transcript ?? new List<Dictionary<string, object>>()).Select(x => ReadString(x, "text", ""))).ToLowerInvariant();
            int hostile = new[] { "threat", "war", "crush", "enemy", "insult", "demand", "punish", "blood" }.Count(text.Contains);
            int conciliatory = new[] { "peace", "respect", "cooperate", "compromise", "friend", "mutual", "reassure", "understand" }.Count(text.Contains);
            int score = hostile - conciliatory;
            if (score >= 3) return "strongly hostile";
            if (score >= 1) return "hostile";
            if (score <= -3) return "strongly conciliatory";
            if (score <= -1) return "conciliatory";
            return "neutral";
        }

        private static int AmbassadorPressureDelta(string classification)
        {
            switch (NormalizeAmbassadorPressureClassification(classification))
            {
                case "strongly conciliatory": return -6;
                case "conciliatory": return -3;
                case "hostile": return 3;
                case "strongly hostile": return 6;
                default: return 0;
            }
        }

        private static int ClampAmbassadorDailyPressure(int priorDaily, int requestedDelta)
        {
            return Math.Max(-6, Math.Min(6, priorDaily + requestedDelta));
        }

        private static Dictionary<string, object> ForeignAmbassadorTickApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> due;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                due = QuerySql(connection, @"SELECT * FROM ambassador_referrals WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='pending' AND due_day<=$day ORDER BY due_day,created_ts LIMIT 50;",
                    new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"day",worldDay}});
            }
            List<Dictionary<string, object>> resolved = due.Select(row => ResolveAmbassadorReferral(campaignId, timelineId, payload, row, worldDay)).ToList();
            List<Dictionary<string, object>> counters;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                counters = QuerySql(connection, @"SELECT referral_id,posting_id,action_id,terms_hash,status,outcome,due_day,counter_terms_json,revision
FROM ambassador_referrals WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='countered' ORDER BY updated_ts LIMIT 50;",
                    new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId}});
            }
            counters = counters.Where(x => !IsInternationalCourtReferral(campaignId, timelineId, ReadString(x, "referral_id", ""))).ToList();
            foreach (Dictionary<string, object> counter in counters)
            {
                Dictionary<string, object> terms = ParseAmbassadorJsonObject(ReadString(counter, "counter_terms_json", "{}"));
                counter["counterTerms"] = terms;
                counter["counterTermsHash"] = CourtHash(CourtCanonicalJson(terms));
            }
            return new Dictionary<string, object>{{"ok",true},{"resolvedReferrals",resolved},{"pendingCounteroffers",counters},{"nativeActions",new ArrayList()}};
        }

        private static Dictionary<string, object> ResolveAmbassadorReferral(string campaignId, string timelineId, Dictionary<string, object> payload,
            Dictionary<string, object> referral, double worldDay)
        {
            string referralId = ReadString(referral, "referral_id", "");
            string postingId = ReadString(referral, "posting_id", "");
            Dictionary<string, object> candidate = ParseAmbassadorJsonObject(ReadString(referral, "exact_terms_json", "{}"));
            Dictionary<string, object> world = ReadDictionary(payload, "worldSnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> evaluationPayload = new Dictionary<string, object>(world, StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["worldDay"] = worldDay,
                ["referralId"] = referralId, ["source"] = "resident_ambassador_referral"
            };
            Dictionary<string, object> posting;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                posting = QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting LIMIT 1;",
                    new Dictionary<string, object>{{"posting",postingId}}).FirstOrDefault();
            }
            if (posting == null) return UpdateAmbassadorReferralState(campaignId, referralId, "cancelled", "posting_ended", worldDay, new Dictionary<string, object>(), new Dictionary<string, object>());
            Dictionary<string, object> proposer = FindDirectorKingdom(evaluationPayload, ReadString(posting, "host_kingdom_id", ""));
            Dictionary<string, object> responder = FindDirectorKingdom(evaluationPayload, ReadString(posting, "origin_kingdom_id", ""));
            if (proposer == null || responder == null)
                return new Dictionary<string, object>{{"referralId",referralId},{"status","pending"},{"reason","Current diplomacy snapshot does not contain both kingdoms."}};

            Dictionary<string, object> response = AskRulerToRespond(campaignId, evaluationPayload, proposer, responder, candidate, "relations", false);
            Dictionary<string, object> answer = ReadDictionary(response, "response") ?? new Dictionary<string, object>();
            string answerKind = ReadString(answer, "response", "").Trim().ToLowerInvariant();
            if (!ReadBool(response, "ok", false) || !new[] { "accept", "refuse", "counter" }.Contains(answerKind))
            {
                if (IsInternationalCourtReferral(campaignId, timelineId, referralId))
                    return new Dictionary<string, object> { ["referralId"] = referralId, ["status"] = "pending",
                        ["technicalFailure"] = true, ["reason"] = "The sovereign's response was unavailable; the exact referral remains pending for retry." };
                int fallback = StableAmbassadorInt(referralId + "|ruler_referral", 100);
                answerKind = fallback < 45 ? "accept" : "refuse";
            }
            if (answerKind == "counter")
            {
                Dictionary<string, object> counter = ApplyCounteroffer(candidate, answer);
                string counterHash = CourtHash(CourtCanonicalJson(counter));
                return UpdateAmbassadorReferralState(campaignId, referralId, "countered", "counter", worldDay, counter,
                    new Dictionary<string, object>{{"counterTermsHash",counterHash},{"requiresPlayerAcceptance",true}});
            }
            if (answerKind == "refuse")
                return UpdateAmbassadorReferralState(campaignId, referralId, "refused", "refuse", worldDay, new Dictionary<string, object>(), new Dictionary<string, object>());

            Dictionary<string, object> normalizationPayload = BuildDirectorNormalizationPayload(evaluationPayload, responder);
            Dictionary<string, object> normalized = NormalizeActionCommand(candidate, campaignId, out List<string> errors, normalizationPayload,
                ReadString(answer, "acceptReason", "The represented ruler accepted the referred terms."));
            if (normalized == null || errors.Count > 0)
                return UpdateAmbassadorReferralState(campaignId, referralId, "refused", "validation_failed", worldDay, new Dictionary<string, object>(),
                    new Dictionary<string, object>{{"errors",errors}});
            if (IsInternationalCourtReferral(campaignId, timelineId, referralId))
                return UpdateAmbassadorReferralState(campaignId, referralId, "accepted_awaiting_court", "accept", worldDay,
                    new Dictionary<string, object>(), new Dictionary<string, object>
                    { ["state"] = "awaiting_player_review", ["normalizedAction"] = normalized });
            normalized["source"] = "resident_ambassador_referral";
            normalized["ambassadorReferralId"] = referralId;
            normalized["requiresAcceptance"] = false;
            Dictionary<string, object> queued = QueueAction(campaignId, normalized);
            Dictionary<string, object> receipt = new Dictionary<string, object>{{"state","queued_for_authoritative_execution"},{"actionId",ReadFirstString(queued,"id","actionId")}};
            return UpdateAmbassadorReferralState(campaignId, referralId, "accepted_pending_execution", "accept", worldDay, new Dictionary<string, object>(), receipt);
        }

        private static Dictionary<string, object> ForeignAmbassadorReferralRespondApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string referralId = ReadString(payload, "referralId", "");
            bool accept = ReadBool(payload, "accept", false);
            Dictionary<string, object> referral;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                referral = QuerySql(connection, "SELECT * FROM ambassador_referrals WHERE referral_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                    new Dictionary<string, object>{{"id",referralId},{"campaign",campaignId},{"timeline",timelineId}}).FirstOrDefault();
            }
            if (referral == null || !string.Equals(ReadString(referral, "status", ""), "countered", StringComparison.OrdinalIgnoreCase))
                return CourtError("The ambassador counteroffer is no longer awaiting a player decision.");
            Dictionary<string, object> counter = ParseAmbassadorJsonObject(ReadString(referral, "counter_terms_json", "{}"));
            string hash = CourtHash(CourtCanonicalJson(counter));
            if (!string.Equals(hash, ReadString(payload, "termsHash", ""), StringComparison.OrdinalIgnoreCase)) return CourtError("The counteroffer terms hash is stale.");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            if (!accept) return UpdateAmbassadorReferralState(campaignId, referralId, "counter_refused", "counter_refuse", worldDay, new Dictionary<string, object>(), new Dictionary<string, object>());

            Dictionary<string, object> world = ReadDictionary(payload, "worldSnapshot") ?? new Dictionary<string, object>();
            Dictionary<string, object> evaluationPayload = new Dictionary<string, object>(world, StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId, ["worldDay"] = worldDay
            };
            Dictionary<string, object> posting;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                posting = QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting LIMIT 1;", new Dictionary<string, object>{{"posting",ReadString(referral,"posting_id","")}}).FirstOrDefault();
            Dictionary<string, object> actor = FindDirectorKingdom(evaluationPayload, ReadString(posting, "origin_kingdom_id", ""));
            Dictionary<string, object> normalized = NormalizeActionCommand(counter, campaignId, out List<string> errors,
                BuildDirectorNormalizationPayload(evaluationPayload, actor ?? new Dictionary<string, object>()), "The player accepted the ruler's exact counteroffer.");
            if (normalized == null || errors.Count > 0) return CourtError("The counteroffer no longer validates: " + string.Join("; ", errors));
            if (IsInternationalCourtReferral(campaignId, timelineId, referralId))
                return UpdateAmbassadorReferralState(campaignId, referralId, "accepted_awaiting_court", "counter_accept", worldDay,
                    new Dictionary<string, object>(), new Dictionary<string, object>
                    { ["state"] = "awaiting_player_review", ["normalizedAction"] = normalized });
            normalized["source"] = "resident_ambassador_counteroffer";
            normalized["ambassadorReferralId"] = referralId;
            normalized["requiresAcceptance"] = false;
            Dictionary<string, object> queued = QueueAction(campaignId, normalized);
            Dictionary<string, object> receipt = new Dictionary<string, object>{{"state","queued_for_authoritative_execution"},{"actionId",ReadFirstString(queued,"id","actionId")}};
            Dictionary<string, object> result = UpdateAmbassadorReferralState(campaignId, referralId, "accepted_pending_execution", "counter_accept", worldDay, new Dictionary<string, object>(), receipt);
            result["ok"] = true;
            result["nativeActions"] = new ArrayList();
            return result;
        }

        private static Dictionary<string, object> UpdateAmbassadorReferralState(string campaignId, string referralId, string status, string outcome,
            double worldDay, Dictionary<string, object> counterTerms, Dictionary<string, object> receipt)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                ExecuteSql(connection, @"UPDATE ambassador_referrals SET status=$status,outcome=$outcome,resolved_day=CASE WHEN $terminal=1 THEN $day ELSE resolved_day END,
counter_terms_json=$counter,authoritative_receipt_json=$receipt,revision=revision+1,updated_ts=$ts WHERE referral_id=$id;",
                    new Dictionary<string, object>{{"status",status},{"outcome",outcome},{"terminal",status=="countered"?0:1},{"day",worldDay},
                    {"counter",CourtCanonicalJson(counterTerms??new Dictionary<string,object>())},{"receipt",CourtCanonicalJson(receipt??new Dictionary<string,object>())},{"ts",ts},{"id",referralId}});
                Dictionary<string, object> row = QuerySql(connection, "SELECT * FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;", new Dictionary<string, object>{{"id",referralId}}).FirstOrDefault();
                return new Dictionary<string, object>{{"ok",true},{"referralId",referralId},{"status",status},{"outcome",outcome},{"termsHash",ReadString(row,"terms_hash","")},
                    {"counterTerms",counterTerms??new Dictionary<string,object>()},{"counterTermsHash",CourtHash(CourtCanonicalJson(counterTerms??new Dictionary<string,object>()))},{"authoritativeReceipt",receipt??new Dictionary<string,object>()}};
            }
        }

        private static void UpdateAmbassadorReferralActionReceipt(string campaignId, Dictionary<string, object> payload, string status)
        {
            Dictionary<string, object> record = ReadDictionary(payload, "record") ?? ReadDictionary(payload, "action") ?? new Dictionary<string, object>();
            string referralId = FirstNonEmpty(ReadString(payload, "ambassadorReferralId", ""), ReadString(record, "ambassadorReferralId", ""));
            if (string.IsNullOrWhiteSpace(referralId)) return;
            Dictionary<string, object> receipt = new Dictionary<string, object>
            {
                {"state",status},{"actionId",ReadFirstString(payload,"serverActionId","actionId","id")},{"result",ReadDictionary(payload,"result")??new Dictionary<string,object>()}
            };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureForeignAmbassadorSchema(connection);
                string next = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? "executed" : "execution_failed";
                ExecuteSql(connection, "UPDATE ambassador_referrals SET status=$status,authoritative_receipt_json=$receipt,revision=revision+1,updated_ts=$ts WHERE referral_id=$id;",
                    new Dictionary<string, object>{{"status",next},{"receipt",CourtCanonicalJson(receipt)},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"id",referralId}});
                ExecuteSql(connection, "UPDATE ambassador_decisions SET authoritative_receipt_json=$receipt,referral_state=$status WHERE referral_id=$id;",
                    new Dictionary<string, object>{{"receipt",CourtCanonicalJson(receipt)},{"status",next},{"id",referralId}});
            }
        }

        private static void AppendForeignAmbassadorSelfTests(List<Dictionary<string, object>> results,
            Action<string, bool, string, object> add, string campaignId, string timelineId, string capitalSessionId)
        {
            Dictionary<string, object> localOpen = CourtSessionOpenApi(new Dictionary<string, object>
            {
                {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","amb_local_open"},{"expectedRevision",0},{"sessionId","amb_local_session"},
                {"authority","royal"},{"playerIsKingdomRuler",true},{"hostSettlementStringId","castle_test"},{"capitalSettlementStringId","town_test"},{"scope","local"},{"worldDay",14d},{"playerHeroStringId","player"}
            });
            Dictionary<string, object> localDenied = RequireCapitalCourtScope(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"sessionId","amb_local_session"}});
            Dictionary<string, object> capitalAllowed = RequireCapitalCourtScope(new Dictionary<string, object>{{"campaignId",campaignId},{"timelineId",timelineId},{"sessionId",capitalSessionId}});
            add("capital_scope_server_boundary", ReadBool(localOpen,"ok",false) && localDenied != null && capitalAllowed == null,
                "Local Rule Mode is server-rejected for royal commands while an active capital session is accepted.", new Dictionary<string,object>{{"localOpen",localOpen},{"localDenied",localDenied??new Dictionary<string,object>()}});

            List<object> candidateRows = new List<object>
            {
                new Dictionary<string,object>{{"heroStringId","envoy_b"},{"charm",225},{"rulerTrust",15},{"travelDays",2},{"isAdult",true},{"isAlive",true},{"isActive",true},{"isLordOrLady",true}},
                new Dictionary<string,object>{{"heroStringId","envoy_a"},{"charm",200},{"rulerTrust",0},{"travelDays",1},{"isAdult",true},{"isAlive",true},{"isActive",true},{"isLordOrLady",true}},
                new Dictionary<string,object>{{"heroStringId","too_low"},{"charm",199},{"rulerTrust",0},{"travelDays",1},{"isAdult",true},{"isAlive",true},{"isActive",true},{"isLordOrLady",true}}
            };
            Dictionary<string, object> establishmentBase = new Dictionary<string, object>
            {
                {"campaignId",campaignId},{"timelineId",timelineId},{"sessionId",capitalSessionId},{"courtScope","capital"},{"hostSettlementStringId","town_test"},
                {"originKingdomStringId","foreign_test"},{"originKingdomName","Foreign Test"},{"originRulerHeroStringId","foreign_ruler"},{"originRulerName","Foreign Ruler"},
                {"playerKingdomStringId","player_kingdom"},{"capitalSettlementStringId","town_test"},{"relationWithRuler",-30},{"atWar",false},{"worldDay",14d},{"candidates",candidateRows}
            };
            Dictionary<string, object> tooHostilePayload = new Dictionary<string, object>(establishmentBase, StringComparer.OrdinalIgnoreCase);
            tooHostilePayload["commandId"] = "amb_relation_fail";
            tooHostilePayload["relationWithRuler"] = -31;
            Dictionary<string, object> tooHostile = CapitalCourtApi(tooHostilePayload, ForeignAmbassadorEstablishApi);
            Dictionary<string, object> establishPayload = new Dictionary<string, object>(establishmentBase, StringComparer.OrdinalIgnoreCase){{"commandId","amb_establish"}};
            Dictionary<string, object> established = CapitalCourtApi(establishPayload, ForeignAmbassadorEstablishApi);
            Dictionary<string, object> duplicatePayload = new Dictionary<string, object>(establishmentBase, StringComparer.OrdinalIgnoreCase){{"commandId","amb_establish_duplicate"}};
            Dictionary<string, object> duplicate = CapitalCourtApi(duplicatePayload, ForeignAmbassadorEstablishApi);
            add("ambassador_establishment_boundaries", !ReadBool(tooHostile,"ok",true) && ReadBool(established,"ok",false)
                && ReadString(established,"heroStringId","")==ReadString(duplicate,"heroStringId","") && ReadString(established,"recordId","")==ReadString(duplicate,"recordId","")
                && ReadDouble(established,"arrivalDay",0d)>=15d && ReadDouble(established,"arrivalDay",0d)<=17d,
                "Relation -30 passes, -31 fails, Charm 199 is excluded, selection is persisted, and arrival is one to three days.",
                new Dictionary<string,object>{{"tooHostile",tooHostile},{"established",established},{"duplicate",duplicate}});

            string postingId = ReadString(established,"recordId","");
            Dictionary<string, object> charterA = BuildAmbassadorAuthorityCharter(campaignId,timelineId,postingId,1,200,0,"foreign_ruler");
            Dictionary<string, object> charterB = BuildAmbassadorAuthorityCharter(campaignId,timelineId,postingId,1,200,0,"foreign_ruler");
            Dictionary<string, object> trade = ReadDictionaryList(charterA,"permissions").FirstOrDefault(x=>ReadString(x,"actionId","")=="trade_agreement")??new Dictionary<string,object>();
            add("ambassador_authority_stable_formula", CourtCanonicalJson(charterA)==CourtCanonicalJson(charterB) && ReadInt(trade,"permissionChance",0)==45
                && ReadDictionaryList(charterA,"permissions").Count==5 && ReadStringList(charterA,"referralOnlyActions").Contains("war"),
                "Authority uses stable D100 rolls, the exact five commercial bases, and mandatory high-level referrals.",charterA);

            Dictionary<string, object> exactTerms = new Dictionary<string,object>{{"command","sign_alliance"},{"actorKingdomId","player_kingdom"},{"targetKingdomId","foreign_test"},{"terms",new Dictionary<string,object>{{"durationDays",120}}}};
            string exactHash = CourtHash(CourtCanonicalJson(exactTerms));
            Dictionary<string, object> referral = CreateAmbassadorReferral(campaignId,timelineId,postingId,"alliance",exactTerms,exactHash,14d);
            add("ambassador_referral_exact_terms",ReadString(referral,"termsHash","")==exactHash && ReadDouble(referral,"dueDay",0d)>=15d && ReadDouble(referral,"dueDay",0d)<=17d,
                "Unauthorized concrete terms are hash-bound and deterministically due in one to three days.",referral);

            bool pressureValues = AmbassadorPressureDelta("strongly conciliatory")==-6 && AmbassadorPressureDelta("conciliatory")==-3
                && AmbassadorPressureDelta("neutral")==0 && AmbassadorPressureDelta("hostile")==3 && AmbassadorPressureDelta("strongly hostile")==6;
            add("ambassador_pressure_discrete_values",pressureValues,"Official-scene tone maps only to -6, -3, 0, +3, or +6 with positive values hostile.",new Dictionary<string,object>());

            bool pressureCap = ClampAmbassadorDailyPressure(6,3)==6 && ClampAmbassadorDailyPressure(-6,-3)==-6
                && ClampAmbassadorDailyPressure(6,-6)==0 && ClampAmbassadorDailyPressure(-3,6)==3;
            add("ambassador_pressure_daily_net_cap",pressureCap,
                "Daily pressure remains within +/-6 while later conversations can offset an earlier result.",new Dictionary<string,object>());

            string rolePrompt = LoadPromptTemplate("ambassador_role.txt");
            bool roleContract = ContainsAny(rolePrompt,"no off-the-record option") && ContainsAny(rolePrompt,"official diplomatic record")
                && ContainsAny(rolePrompt,"authoritative successful action receipt") && ContainsAny(rolePrompt,"Private Keep conversations");
            add("ambassador_non_overridable_role_contract",roleContract,
                "The dedicated role prompt mandates total relay, exact authority, receipt gating, and the private Keep firewall.",new Dictionary<string,object>{{"promptHash",CourtHash(rolePrompt)}});

            Dictionary<string, object> ended = ForeignAmbassadorEndApi(new Dictionary<string,object>
            {
                {"campaignId",campaignId},{"timelineId",timelineId},{"commandId","amb_war_recall"},{"expectedRevision",ReadLong(established,"revision",0)},
                {"postingId",postingId},{"reason","war_declared"},{"worldDay",14.5d}
            });
            string referralStatus;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                referralStatus = ReadString(QuerySql(connection,"SELECT status FROM ambassador_referrals WHERE referral_id=$id LIMIT 1;",
                    new Dictionary<string,object>{{"id",ReadString(referral,"referralId","")}}).FirstOrDefault(),"status","");
            }
            add("ambassador_war_recall_cancels_referrals",ReadBool(ended,"ok",false) && referralStatus=="cancelled_war",
                "War termination ends the posting and immediately cancels its pending ruler referrals.",new Dictionary<string,object>{{"ended",ended},{"referralStatus",referralStatus}});
        }

        private static Dictionary<string, object> ReadForeignAmbassadorPosting(ReignDbConnection connection, CourtApiContext ctx, string postingId)
        {
            return QuerySql(connection, "SELECT * FROM foreign_ambassador_postings WHERE posting_id=$posting AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;",
                new Dictionary<string, object>{{"posting",postingId},{"campaign",ctx.CampaignId},{"timeline",ctx.TimelineId}}).FirstOrDefault();
        }

        private static void RelayOfficialArchiveToSuccessor(string campaignId, string timelineId, string postingId, string rulerId)
        {
            if (string.IsNullOrWhiteSpace(rulerId)) return;
            HashSet<string> existing = new HashSet<string>(ReadJsonLinesFromPath(CharacterFile(campaignId, rulerId, "history", "dialogue.jsonl"))
                .Select(x => ReadFirstString(x, "turnId", "id")).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> rows;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                rows = QuerySql(connection, @"SELECT * FROM ambassador_official_archive WHERE campaign_id=$campaign AND timeline_id=$timeline AND posting_id=$posting ORDER BY created_ts;",
                    new Dictionary<string, object>{{"campaign",campaignId},{"timeline",timelineId},{"posting",postingId}});
            }
            foreach (Dictionary<string, object> row in rows)
            {
                string turnId = ReadString(row, "turn_id", "");
                if (string.IsNullOrWhiteSpace(turnId) || existing.Contains(turnId)) continue;
                AppendJsonLineToPath(CharacterFile(campaignId, rulerId, "history", "dialogue.jsonl"), new Dictionary<string, object>
                {
                    {"id",turnId},{"turnId",turnId},{"ts",ReadLong(row,"created_ts",0)},{"role",ReadString(row,"speaker_role","")},
                    {"speaker",ReadString(row,"speaker_id","")},{"text",ReadString(row,"text","")},{"channel","ambassador_official"},
                    {"sessionId",ReadString(row,"conversation_session_id","")},{"exchangeId",ReadString(row,"exchange_id","")},
                    {"worldDay",ReadDouble(row,"world_day",0d)},{"postingId",postingId},{"representedKingdomId",ReadString(row,"origin_kingdom_id","")},
                    {"relayedByEnvoyId",ReadString(row,"envoy_id","")},{"officialRelay",true},{"inheritedBySuccessor",true}
                });
                existing.Add(turnId);
            }
        }

        private static Dictionary<string, object> ForeignAmbassadorPostingResult(CourtApiContext ctx, Dictionary<string, object> row, string result)
        {
            if (row == null) return CourtError("Foreign ambassador posting was not found.");
            Dictionary<string, object> response = CourtMutationResult(ctx, ReadString(row, "posting_id", ""), ReadLong(row, "revision", 0), result, new ArrayList());
            response["heroStringId"] = ReadString(row, "hero_id", "");
            response["arrivalDay"] = ReadDouble(row, "arrival_day", 0d);
            response["status"] = ReadString(row, "status", "");
            response["authorityRevision"] = ReadLong(row, "authority_revision", 1);
            response["authorityCharter"] = ParseAmbassadorJsonObject(ReadString(row, "authority_charter_json", "{}"));
            response["posting"] = row;
            return response;
        }

        private static int StableAmbassadorInt(string seed, int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 1) return 0;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(seed ?? string.Empty));
                uint value = BitConverter.ToUInt32(bytes, 0);
                return (int)(value % (uint)exclusiveMaximum);
            }
        }

        private static string AmbassadorHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder result = new StringBuilder(bytes.Length * 2);
                foreach (byte item in bytes) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        private static Dictionary<string, object> ParseAmbassadorJsonObject(string json)
        {
            try { return CourtObjectDictionary(Json.DeserializeObject(string.IsNullOrWhiteSpace(json) ? "{}" : json)) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }
    }
}
