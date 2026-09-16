using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] EditorDocumentNames =
        {
            "profile", "template", "characteristics", "wealth", "inventory", "appearance", "traits", "background", "voice",
            "motivations", "save_quirks", "hidden_history", "secrets", "pressure", "state", "constructed", "narrative"
        };

        private static void EnsureCharacterEditorSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_heads (
hero_id TEXT PRIMARY KEY,revision_token TEXT NOT NULL DEFAULT '',latest_revision_id TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_revisions (
revision_id TEXT PRIMARY KEY,hero_id TEXT NOT NULL,section TEXT NOT NULL DEFAULT 'all',description TEXT NOT NULL DEFAULT '',
before_json TEXT NOT NULL DEFAULT '{}',after_json TEXT NOT NULL DEFAULT '{}',native_changes_json TEXT NOT NULL DEFAULT '{}',
reconciliation_json TEXT NOT NULL DEFAULT '{}',rollback_of TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_editor_revisions_hero ON character_editor_revisions(hero_id,created_ts DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_native_commands (
command_id TEXT PRIMARY KEY,revision_id TEXT NOT NULL,hero_id TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
changes_json TEXT NOT NULL DEFAULT '{}',inverse_json TEXT NOT NULL DEFAULT '{}',dangerous INTEGER NOT NULL DEFAULT 0,
confirmation TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,claimed_ts INTEGER NOT NULL DEFAULT 0,resolved_ts INTEGER NOT NULL DEFAULT 0,
result_json TEXT NOT NULL DEFAULT '{}');" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_editor_native_commands_status ON character_editor_native_commands(status,created_ts);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_reconciliation (
job_id TEXT PRIMARY KEY,revision_id TEXT NOT NULL,hero_id TEXT NOT NULL,section TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
summary TEXT NOT NULL DEFAULT '',result_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
        }

        private static Dictionary<string, object> CharacterEditorListApi(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaign=ReadString(payload,"campaignId",LatestCampaignId());if(string.IsNullOrWhiteSpace(campaign))campaign=LatestCampaignId();string root=Path.Combine(CampaignDirectory(campaign),"characters");List<Dictionary<string, object>> characters=new List<Dictionary<string, object>>();
            Dictionary<string,Dictionary<string,object>> shippedProfiles=LoadCharacterProfileLibrary();
            HashSet<string> characterIds=new HashSet<string>(shippedProfiles.Keys,StringComparer.OrdinalIgnoreCase);
            if(Directory.Exists(root))foreach(string dir in Directory.GetDirectories(root))characterIds.Add(Path.GetFileName(dir));
            using(ReignDbConnection c=OpenCampaignConnection(campaign))
            {
            Dictionary<string,int> pendingByHero=QuerySql(c,@"SELECT hero_id,COUNT(*) count FROM character_editor_native_commands
WHERE status IN ('pending','claimed','failed') GROUP BY hero_id;")
                .Where(row=>!string.IsNullOrWhiteSpace(ReadString(row,"hero_id","")))
                .ToDictionary(row=>ReadString(row,"hero_id",""),row=>ReadInt(row,"count",0),StringComparer.OrdinalIgnoreCase);
            foreach(string id in characterIds)
            {
                string dir=Path.Combine(root,id);Dictionary<string, object> profile=ReadJsonObject(Path.Combine(dir,"profile.json"));Dictionary<string, object> constructed=ReadJsonObject(Path.Combine(dir,"constructed.json"));int pending=pendingByHero.TryGetValue(id,out int pendingCount)?pendingCount:0;
                profile=WithNarrativeCampaignVersion(campaign,profile);
                Dictionary<string,object> shipped=null;CharacterProfileLibraryFor(profile).TryGetValue(id,out shipped);
                Dictionary<string,object> displayProfile=BuildCharacterEditorCanonicalProfile(shipped,profile,id);
                Dictionary<string, object> library=ReadJsonObject(Path.Combine(dir,"profile_library.json"));Dictionary<string, object> enrichment=ReadJsonObject(Path.Combine(dir,"enrichment.json"));
                string shippedSource=ReadString(shipped,"source","");
                string shippedPack=shipped==null?"":ReadString(shipped,"packVersion",CharacterProfilePackVersion);
                characters.Add(new Dictionary<string, object>{{"heroStringId",id},{"name",ReadString(displayProfile,"name",id)},{"title",ReadFirstString(displayProfile,"title","occupation")},{"occupation",ReadString(displayProfile,"occupation","")},{"clanId",ReadString(displayProfile,"clanId","")},{"kingdomId",ReadString(displayProfile,"kingdomId","")},{"isLord",ReadBool(displayProfile,"isLord",false)},{"isNotable",ReadBool(displayProfile,"isNotable",false)},{"isWanderer",ReadBool(displayProfile,"isWanderer",false)},{"isAlive",ReadBool(displayProfile,"isAlive",true)},{"constructionStatus",ReadString(constructed,"status",shipped==null?"not_constructed":"canonical_ready")},{"profileSource",ReadString(library,"profileSource",shippedSource)},{"profilePackVersion",ReadString(library,"packVersion",shippedPack)},{"enrichmentStatus",ReadString(enrichment,"status",shipped==null?"":"not_required")},{"pendingNativeChanges",pending}});
            }
            }
            return new Dictionary<string, object>{{"ok",true},{"campaignId",campaign},{"characters",characters.OrderBy(x=>ReadString(x,"name","")).ToList()}};
        }

        private static Dictionary<string, object> CharacterEditorLoadApi(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaign=ReadString(payload,"campaignId",LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId");if(string.IsNullOrWhiteSpace(campaign))campaign=LatestCampaignId();if(string.IsNullOrWhiteSpace(hero))return new Dictionary<string, object>{{"ok",false},{"error","heroStringId is required."}};
            Dictionary<string,object> runtimeProfile=ReadJsonObject(CharacterFile(campaign,hero,"profile.json"));
            Dictionary<string,Dictionary<string,object>> shippedProfiles=LoadCharacterProfileLibrary();
            if(shippedProfiles.TryGetValue(hero,out Dictionary<string,object> shipped))
            {
                runtimeProfile=BuildCharacterEditorCanonicalProfile(shipped,runtimeProfile,hero);
                UpsertCharacterProfile(campaign,runtimeProfile);
            }
            else if(runtimeProfile.Count>0)MaterializeCharacterProfile(campaign,hero,runtimeProfile,false,false);
            using(ReignDbConnection c=OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> snapshot=BuildCharacterEditorSnapshot(campaign,hero,c);Dictionary<string, object> head=QuerySql(c,"SELECT * FROM character_editor_heads WHERE hero_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",hero}}).FirstOrDefault();string token=ReadString(head,"revision_token","");if(string.IsNullOrWhiteSpace(token)){token=ComputeEditorToken(snapshot);ExecuteSql(c,"INSERT OR REPLACE INTO character_editor_heads(hero_id,revision_token,latest_revision_id,updated_ts) VALUES($id,$token,'',$ts);",new Dictionary<string, object>{{"id",hero},{"token",token},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});}snapshot["revisionToken"]=token;snapshot["ok"]=true;return snapshot;
            }
        }

        private static Dictionary<string,object> BuildCharacterEditorCanonicalProfile(Dictionary<string,object> shipped,Dictionary<string,object> runtimeProfile,string heroId)
        {
            Dictionary<string,object> merged=shipped==null
                ? new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase)
                : DeepCloneProfileDictionary(ReadDictionary(shipped,"sourceFacts"));
            if(runtimeProfile!=null)foreach(KeyValuePair<string,object> pair in runtimeProfile)merged[pair.Key]=pair.Value;
            merged["heroStringId"]=heroId;
            if(!merged.ContainsKey("characterObjectId"))merged["characterObjectId"]=heroId;
            return merged;
        }

        private static Dictionary<string, object> CharacterEditorConstructApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaign = ReadString(payload, "campaignId", LatestCampaignId());
            string hero = ReadFirstString(payload, "heroStringId", "heroId");
            if (string.IsNullOrWhiteSpace(campaign)) campaign = LatestCampaignId();
            if (string.IsNullOrWhiteSpace(hero)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };

            Dictionary<string, object> profile = ReadJsonObject(CharacterFile(campaign, hero, "profile.json"));
            if (profile.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The game profile has not been captured for this character." };

            Dictionary<string, object> result = ConstructCharacterFiles(
                campaign,
                hero,
                profile,
                ReadBool(payload, "force", true),
                ReadBool(payload, "useLlm", true),
                "character_editor");
            result["campaignId"] = campaign;
            result["heroStringId"] = hero;
            return result;
        }

        private static Dictionary<string, object> BuildCharacterEditorSnapshot(string campaign,string hero,ReignDbConnection c)
        {
            EnsureConversationRelationshipSchema(c);
            EnsureSocialReputationSchema(c);
            string dir=CharacterDirectory(campaign,hero);Dictionary<string, object> profileDocument=ReadJsonObject(Path.Combine(dir,"profile.json"));Dictionary<string, object> traitDocument=ReadJsonObject(Path.Combine(dir,"traits.json"));if(traitDocument.Count>0){bool changed=EnsureTraitPercentageData(traitDocument,hero);changed=EnsureCourtCharacterData(traitDocument,profileDocument,hero)||changed;changed=EnsurePersonalityPortraitData(traitDocument,ReadString(profileDocument,"name",hero),false)||changed;if(changed)WriteJsonObject(Path.Combine(dir,"traits.json"),traitDocument);}Dictionary<string, object> documents=new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);foreach(string name in EditorDocumentNames)documents[name]=name=="traits"?traitDocument:name=="profile"?profileDocument:name=="wealth"?ResolveWealthDocument(campaign,hero,profileDocument):ReadJsonObject(Path.Combine(dir,name+".json"));
            Dictionary<string, object> history=new Dictionary<string, object>{{"dialogue",ReadJsonLinesFromPath(Path.Combine(dir,"history","dialogue.jsonl"))},{"events",ReadJsonLinesFromPath(Path.Combine(dir,"history","events.jsonl"))},{"decisions",ReadJsonLinesFromPath(Path.Combine(dir,"history","decisions.jsonl"))}};
            Dictionary<string, object> memoryFiles=new Dictionary<string, object>{{"summary",ReadJsonObject(Path.Combine(dir,"memory","summary.json"))},{"relationships",ReadJsonObject(Path.Combine(dir,"memory","relationships.json"))},{"memories",ReadJsonLinesFromPath(Path.Combine(dir,"memory","memories.jsonl"))}};
            Dictionary<string, object> profile=ReadDictionary(documents,"profile")??new Dictionary<string, object>();KnowledgeAccessContext knowledge=BuildKnowledgeAccessContext(hero,ReadFirstString(profile,"mainHeroStringId","playerHeroStringId"),ReadFirstString(profile,"currentSettlementId","settlementId"),profile);
            List<Dictionary<string, object>> relationshipNetwork=BuildCharacterEditorRelationshipNetwork(campaign,hero,c,profile);
            Dictionary<string, object> records=new Dictionary<string, object>
            {
                ["worldEvents"]=QueryKnownRows(c,"events",knowledge,"",1000),
                ["knowledgeReceipts"]=QuerySql(c,"SELECT * FROM knowledge_receipts WHERE npc_id=$id ORDER BY acquired_day DESC,created_ts DESC LIMIT 1000;",EditorIdParams(hero)),
                ["conversationSessions"]=QuerySql(c,"SELECT * FROM conversation_sessions WHERE npc_id=$id OR player_id=$id ORDER BY start_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["conversationTurns"]=QuerySql(c,@"SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE (s.npc_id=$id OR s.player_id=$id) AND t.status='active' ORDER BY t.ts DESC,t.turn_order DESC LIMIT 2000;",EditorIdParams(hero)),
                ["memorySources"]=QuerySql(c,@"SELECT ms.* FROM memory_sources ms WHERE
(ms.document_type='summary' AND ms.document_id IN (SELECT summary_id FROM summaries WHERE owner_id=$id))
OR (ms.source_type='session' AND ms.source_id IN (SELECT session_id FROM conversation_sessions WHERE npc_id=$id OR player_id=$id))
ORDER BY ms.document_type,ms.document_id,ms.ordinal LIMIT 2000;",EditorIdParams(hero)),
                ["memories"]=QueryKnownRows(c,"memories",knowledge,"status='active'",1000),
                ["summaries"]=QueryKnownRows(c,"summaries",knowledge,"status='active'",500),
                ["beliefs"]=QueryKnownRows(c,"beliefs",knowledge,"",500),
                ["comprehension"]=QueryKnownRows(c,"comprehension",knowledge,"",500),
                ["obligations"]=QueryKnownRows(c,"obligations",knowledge,"",500),
                ["dynamicCharacteristics"]=QuerySql(c,@"SELECT * FROM dynamic_characteristics WHERE owner_id=$id
ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'rejected' THEN 1 ELSE 2 END,importance DESC,last_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["relationshipNetwork"]=relationshipNetwork,
                ["sharedRelationshipHistory"]=SharedRelationshipHistoryForCharacter(campaign,hero,c),
                ["relationshipIncidents"]=QuerySql(c,"SELECT * FROM relationship_incidents WHERE hero_a_id=$id OR hero_b_id=$id ORDER BY world_day DESC,created_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["conversationRelationshipReceipts"]=QuerySql(c,"SELECT * FROM conversation_relationship_receipts WHERE observer_id=$id OR target_id=$id ORDER BY created_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["conversationBenefits"]=QuerySql(c,"SELECT * FROM conversation_relationship_benefits WHERE giver_id=$id OR recipient_id=$id ORDER BY updated_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["acquaintances"]=QuerySql(c,"SELECT * FROM acquaintances WHERE observer_id=$id OR subject_id=$id ORDER BY updated_ts DESC LIMIT 1000;",EditorIdParams(hero)),
                ["identityEvidence"]=QuerySql(c,"SELECT * FROM identity_evidence WHERE observer_id=$id OR subject_id=$id ORDER BY created_ts DESC LIMIT 1000;",EditorIdParams(hero)),
                ["rumors"]=QuerySql(c,@"SELECT rst.*,ro.archetype_id,ro.world_day,ro.expires_day,ro.provenance_summary
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE rst.subject_id=$id ORDER BY ro.world_day DESC LIMIT 500;",EditorIdParams(hero)),
                ["socialReputations"]=QuerySql(c,"SELECT * FROM character_reputations WHERE subject_id=$id ORDER BY acquired_day DESC LIMIT 500;",EditorIdParams(hero)),
                ["letters"]=QuerySql(c,"SELECT * FROM letters WHERE sender_id=$id OR recipient_id=$id ORDER BY updated_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["conceptions"]=QuerySql(c,"SELECT * FROM conceptions WHERE mother_id=$id OR biological_father_id=$id OR legal_father_id=$id ORDER BY updated_ts DESC LIMIT 500;",EditorIdParams(hero)),
                ["parentage"]=QuerySql(c,"SELECT * FROM parentage WHERE child_id=$id OR mother_id=$id OR biological_father_id=$id OR legal_father_id=$id ORDER BY updated_ts DESC LIMIT 500;",EditorIdParams(hero))
            };
            var narrative = ReadDictionary(documents, "narrative");
            if (NarrativeDocumentReady(narrative))
                documents["narrative"] = ResolveEffectiveNarrative(new Dictionary<string, object> {
                    ["narrative"] = narrative, ["dynamicCharacteristics"] = ReadJsonObject(Path.Combine(dir, "dynamic_characteristics.json")) });
            List<Dictionary<string, object>> revisions=QuerySql(c,"SELECT revision_id,hero_id,section,description,native_changes_json,reconciliation_json,rollback_of,created_ts FROM character_editor_revisions WHERE hero_id=$id ORDER BY created_ts DESC LIMIT 200;",new Dictionary<string, object>{{"id",hero}});
            List<Dictionary<string, object>> commands=QuerySql(c,"SELECT * FROM character_editor_native_commands WHERE hero_id=$id ORDER BY created_ts DESC LIMIT 200;",new Dictionary<string, object>{{"id",hero}});
            Dictionary<string, object> mbtiProfile=ResolveCharacterMbtiProfile(campaign,hero,profileDocument,traitDocument);
            return new Dictionary<string, object>{{"campaignId",campaign},{"heroStringId",hero},{"documents",documents},{"profileLibrary",ReadJsonObject(Path.Combine(dir,"profile_library.json"))},{"campaignProfileOverlay",ReadJsonObject(Path.Combine(dir,"campaign_profile_overlay.json"))},{"enrichment",ReadJsonObject(Path.Combine(dir,"enrichment.json"))},{"memoryFiles",memoryFiles},{"history",history},{"records",records},{"socialStanding",CharacterSocialStandingProjection(c,campaign,hero)},{"portraits",CharacterEditorPortraitInventory(campaign,hero)},{"revisions",revisions},{"nativeCommands",commands},{"traitDefinitions",TraitDefinitions()},{"traitKeys",CoreTraitKeys.ToList()},{"courtVirtueKeys",CourtVirtueKeys.ToList()},{"mbtiProfile",mbtiProfile},{"mbtiType",ReadString(mbtiProfile,"type","XXXX")},{"storageBytes",DirectorySize(dir)}};
        }

        private static List<Dictionary<string, object>> BuildCharacterEditorRelationshipNetwork(
            string campaign, string hero, ReignDbConnection connection, Dictionary<string, object> profile)
        {
            EnsureMbtiRelationshipSchema(connection);
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            string playerId = ReadFirstString(profile, "mainHeroStringId", "playerHeroStringId");
            bool hasPlayerChemistry = !string.IsNullOrWhiteSpace(playerId) && QuerySql(connection,
                "SELECT pair_key FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = AmbientPairKey(hero, playerId) }).Count > 0;
            if (!string.IsNullOrWhiteSpace(playerId) && !playerId.Equals(hero, StringComparison.OrdinalIgnoreCase) && !hasPlayerChemistry)
            {
                Dictionary<string, object> playerProfile = ReadJsonObject(CharacterFile(campaign, playerId, "profile.json"));
                result.Add(new Dictionary<string, object>
                {
                    ["otherHeroId"] = playerId,
                    ["otherHeroName"] = ReadString(playerProfile, "name", "Player"),
                    ["relationshipKind"] = "player",
                    ["directionalAffinity"] = ReadInt(profile, "relationToPlayer", 0),
                    ["reverseAffinity"] = "",
                    ["nativeRelation"] = ReadInt(profile, "relationToPlayer", 0),
                    ["observerMbti"] = ReadString(ResolveCharacterMbtiProfile(
                        campaign, hero, profile, ReadJsonObject(CharacterFile(campaign, hero, "traits.json"))),
                        "type", "XXXX"),
                    ["otherMbti"] = "",
                    ["compatibilityChance"] = "",
                    ["tags"] = new List<string> { "player_relationship" }
                });
            }
            foreach (Dictionary<string, object> row in QuerySql(connection, @"SELECT * FROM relationship_pair_chemistry
WHERE hero_a_id=$id OR hero_b_id=$id ORDER BY last_day DESC,updated_ts DESC LIMIT 1000;",
                new Dictionary<string, object> { ["id"] = hero }))
            {
                bool observerIsA = ReadString(row, "hero_a_id", "").Equals(hero, StringComparison.OrdinalIgnoreCase);
                string otherId = ReadString(row, observerIsA ? "hero_b_id" : "hero_a_id", "");
                if (string.IsNullOrWhiteSpace(otherId)) continue;
                Dictionary<string, object> otherProfile = ReadJsonObject(CharacterFile(campaign, otherId, "profile.json"));
                Dictionary<string, object> lifecycle = RelationshipLifecycleView(connection, hero, otherId);
                result.Add(new Dictionary<string, object>
                {
                    ["otherHeroId"] = otherId,
                    ["otherHeroName"] = ReadString(otherProfile, "name", otherId),
                    ["relationshipKind"] = otherId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "player" : "npc",
                    ["directionalAffinity"] = EffectiveDirectionalAffinity(row, observerIsA, 0),
                    ["reverseAffinity"] = EffectiveDirectionalAffinity(row, !observerIsA, 0),
                    ["nativeRelation"] = ReadInt(row, "projected_native_relation", 0),
                    ["observerMbti"] = ReadString(row, observerIsA ? "mbti_a" : "mbti_b", ""),
                    ["otherMbti"] = ReadString(row, observerIsA ? "mbti_b" : "mbti_a", ""),
                    ["compatibilityChance"] = ReadInt(row, observerIsA ? "chance_a_to_b" : "chance_b_to_a", 0),
                    ["reverseCompatibilityChance"] = ReadInt(row, observerIsA ? "chance_b_to_a" : "chance_a_to_b", 0),
                    ["perceptionTag"] = RelationshipTagForObserver(connection, campaign, hero, otherId,
                        ReadInt(row, "projected_native_relation", 0)),
                    ["tags"] = ReadStringList(lifecycle, "tags"),
                    ["lastMetDay"] = ReadDouble(row, "last_day", 0d),
                    ["lastContext"] = FirstNonEmpty(
                        ReadString(row, "last_context_kind", ""),
                        ReadString(row, "last_context_id", ""))
                });
            }
            return result;
        }

        private static Dictionary<string, object> CharacterEditorSaveApi(Dictionary<string, object> payload)
        {
            lock (NarrativeStateLock) return CharacterEditorSaveApiLocked(payload);
        }

        private static Dictionary<string, object> CharacterEditorSaveApiLocked(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaign=ReadString(payload,"campaignId",LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),section=ReadString(payload,"section","all"),token=ReadString(payload,"revisionToken","");if(string.IsNullOrWhiteSpace(campaign))campaign=LatestCampaignId();if(string.IsNullOrWhiteSpace(hero))return new Dictionary<string, object>{{"ok",false},{"error","heroStringId is required."}};
            using(ReignDbConnection c=OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> head=QuerySql(c,"SELECT * FROM character_editor_heads WHERE hero_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",hero}}).FirstOrDefault();string current=ReadString(head,"revision_token","");if(!string.IsNullOrWhiteSpace(current)&&!string.Equals(token,current,StringComparison.Ordinal))return new Dictionary<string, object>{{"ok",false},{"conflict",true},{"error","This character changed after it was loaded. Reload to compare before saving."},{"currentRevisionToken",current}};
                Dictionary<string, object> before=BuildCharacterEditorSnapshot(campaign,hero,c);Dictionary<string, object> incomingHistory=ReadDictionary(payload,"history");List<long> changedHistoryTs=ChangedHistoryTimestamps(ReadDictionary(before,"history"),incomingHistory);ApplyEditorDocuments(campaign,hero,ReadDictionary(payload,"documents"));ApplyEditorHistory(campaign,hero,incomingHistory);ApplyEditorRecords(c,hero,ReadDictionary(payload,"records"));Dictionary<string, object> native=ReadDictionary(payload,"nativeChanges")??new Dictionary<string, object>();Dictionary<string, object> inverse=BuildNativeInverse(before,native);
                if(changedHistoryTs.Count>0)RebuildEditedHistoryProjections(campaign,hero,c,incomingHistory,changedHistoryTs);
                string revision="editor_revision_"+Guid.NewGuid().ToString("N");long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string job="editor_reconcile_"+Guid.NewGuid().ToString("N");ReconcileCharacterEditor(campaign,hero,c,section,job,revision);
                Dictionary<string, object> after=BuildCharacterEditorSnapshot(campaign,hero,c);string next=ComputeEditorToken(after);ExecuteSql(c,@"INSERT INTO character_editor_revisions(revision_id,hero_id,section,description,before_json,after_json,native_changes_json,reconciliation_json,created_ts) VALUES($revision,$hero,$section,$description,$before,$after,$native,$reconcile,$ts);",new Dictionary<string, object>{{"revision",revision},{"hero",hero},{"section",section},{"description",ReadString(payload,"description","Character Editor save")},{"before",Json.Serialize(before)},{"after",Json.Serialize(after)},{"native",Json.Serialize(native)},{"reconcile",Json.Serialize(new Dictionary<string, object>{{"jobId",job},{"status","completed"}})},{"ts",ts}});
                if(native.Count>0)ExecuteSql(c,@"INSERT INTO character_editor_native_commands(command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts) VALUES($id,$revision,$hero,'pending',$changes,$inverse,$dangerous,$confirmation,$ts);",new Dictionary<string, object>{{"id","editor_command_"+Guid.NewGuid().ToString("N")},{"revision",revision},{"hero",hero},{"changes",Json.Serialize(native)},{"inverse",Json.Serialize(inverse)},{"dangerous",ReadBool(payload,"dangerous",false)?1:0},{"confirmation",ReadString(payload,"confirmation","")},{"ts",ts}});
                ExecuteSql(c,"INSERT OR REPLACE INTO character_editor_heads(hero_id,revision_token,latest_revision_id,updated_ts) VALUES($hero,$token,$revision,$ts);",new Dictionary<string, object>{{"hero",hero},{"token",next},{"revision",revision},{"ts",ts}});LogOperational("character_editor.save",new Dictionary<string, object>{{"campaignId",campaign},{"heroStringId",hero},{"section",section},{"revisionId",revision},{"nativeChangeCount",native.Count}});
                WriteJsonObject(CharacterFile(campaign,hero,"editor_authority.json"),new Dictionary<string, object>{{"version",1},{"authoritative",true},{"latestRevisionId",revision},{"updatedUtc",DateTime.UtcNow.ToString("o")}});
                return new Dictionary<string, object>{{"ok",true},{"revisionId",revision},{"revisionToken",next},{"reconciliationJobId",job},{"nativeQueued",native.Count>0},{"message","Server changes were saved. Native Bannerlord changes will be applied the next time this savegame is loaded."}};
            }
        }

        private static Dictionary<string, object> CharacterEditorRollbackApi(Dictionary<string, object> payload)
        {
            lock (NarrativeStateLock) return CharacterEditorRollbackApiLocked(payload);
        }

        private static Dictionary<string, object> CharacterEditorRollbackApiLocked(Dictionary<string, object> payload)
        {
            string campaign=ReadString(payload,"campaignId",LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),revision=ReadFirstString(payload,"revisionId","revision_id");using(ReignDbConnection c=OpenCampaignConnection(campaign)){Dictionary<string, object> row=QuerySql(c,"SELECT * FROM character_editor_revisions WHERE revision_id=$revision AND hero_id=$hero LIMIT 1;",new Dictionary<string, object>{{"revision",revision},{"hero",hero}}).FirstOrDefault();if(row==null)return new Dictionary<string, object>{{"ok",false},{"error","Revision was not found."}};Dictionary<string, object> snapshot=TryParseJsonObject(ReadString(row,"before_json","{}"))??new Dictionary<string, object>();ApplyEditorSnapshot(campaign,hero,c,snapshot);string newRevision="editor_revision_"+Guid.NewGuid().ToString("N");long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();Dictionary<string, object> inverse=TryParseJsonObject(ReadString(QuerySql(c,"SELECT inverse_json FROM character_editor_native_commands WHERE revision_id=$id ORDER BY created_ts DESC LIMIT 1;",new Dictionary<string, object>{{"id",revision}}).FirstOrDefault(),"inverse_json","{}"))??new Dictionary<string, object>();if(inverse.Count>0)ExecuteSql(c,@"INSERT INTO character_editor_native_commands(command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts) VALUES($id,$revision,$hero,'pending',$changes,'{}',1,'rollback',$ts);",new Dictionary<string, object>{{"id","editor_command_"+Guid.NewGuid().ToString("N")},{"revision",newRevision},{"hero",hero},{"changes",Json.Serialize(inverse)},{"ts",ts}});Dictionary<string, object> after=BuildCharacterEditorSnapshot(campaign,hero,c);string token=ComputeEditorToken(after);ExecuteSql(c,@"INSERT INTO character_editor_revisions(revision_id,hero_id,section,description,before_json,after_json,native_changes_json,reconciliation_json,rollback_of,created_ts) VALUES($id,$hero,'rollback',$description,$before,$after,$native,'{}',$rollback,$ts);",new Dictionary<string, object>{{"id",newRevision},{"hero",hero},{"description","Rollback of "+revision},{"before",ReadString(row,"after_json","{}")},{"after",Json.Serialize(after)},{"native",Json.Serialize(inverse)},{"rollback",revision},{"ts",ts}});ExecuteSql(c,"INSERT OR REPLACE INTO character_editor_heads(hero_id,revision_token,latest_revision_id,updated_ts) VALUES($hero,$token,$revision,$ts);",new Dictionary<string, object>{{"hero",hero},{"token",token},{"revision",newRevision},{"ts",ts}});ReconcileCharacterEditor(campaign,hero,c,"rollback","editor_reconcile_"+Guid.NewGuid().ToString("N"),newRevision);return new Dictionary<string, object>{{"ok",true},{"revisionId",newRevision},{"revisionToken",token},{"message","Revision restored. Any native rollback changes will be applied the next time this savegame is loaded."}};}}

        private static Dictionary<string, object> CharacterEditorNativePollApi(Dictionary<string, object> payload){string campaign=ReadString(payload,"campaignId","default");using(ReignDbConnection c=OpenCampaignConnection(campaign)){long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();List<Dictionary<string, object>> rows=QuerySql(c,"SELECT * FROM character_editor_native_commands WHERE status='pending' OR (status='claimed' AND claimed_ts<$stale) ORDER BY created_ts LIMIT 25;",new Dictionary<string, object>{{"stale",ts-300}});foreach(Dictionary<string, object> row in rows){ExecuteSql(c,"UPDATE character_editor_native_commands SET status='claimed',claimed_ts=$ts WHERE command_id=$id;",new Dictionary<string, object>{{"ts",ts},{"id",ReadString(row,"command_id","")}});row["changes"]=TryParseJsonObject(ReadString(row,"changes_json","{}"))??new Dictionary<string, object>();}return new Dictionary<string, object>{{"ok",true},{"commands",rows}};}}
        private static Dictionary<string, object> CharacterEditorNativeReportApi(
            Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "default");
            string id = ReadFirstString(payload, "commandId", "command_id");
            string status = ReadString(payload, "status", "completed");
            using (ReignDbConnection c = OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> command = QuerySql(c,
                    "SELECT * FROM character_editor_native_commands WHERE command_id=$id LIMIT 1;",
                    new Dictionary<string, object> { ["id"] = id })
                    .FirstOrDefault();
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ExecuteSql(c, @"UPDATE character_editor_native_commands SET
status=$status,resolved_ts=$ts,result_json=$result WHERE command_id=$id;",
                    new Dictionary<string, object>
                    {
                        ["status"] = status, ["ts"] = ts,
                        ["result"] = Json.Serialize(payload), ["id"] = id
                    });
                if (command != null && (status == "completed"
                    || status == "partially_applied"))
                {
                    string hero = ReadString(command, "hero_id", "");
                    Dictionary<string, object> profile = ReadJsonObject(
                        CharacterFile(campaign, hero, "profile.json"));
                    Dictionary<string, object> changes = TryParseJsonObject(
                        ReadString(command, "changes_json", "{}"))
                        ?? new Dictionary<string, object>();
                    List<string> applied = ReadStringList(payload, "applied");
                    foreach (string key in applied)
                        if (changes.ContainsKey(key)
                            && !key.Equals("wealth",
                                StringComparison.OrdinalIgnoreCase))
                            profile[key] = changes[key];
                    profile["updatedUtc"] = DateTime.UtcNow.ToString("o");
                    WriteJsonObject(CharacterFile(campaign, hero,
                        "profile.json"), profile);
                    UpdateCharacterIndex(campaign, profile);

                    if (ReadString(command, "confirmation", "")
                            .Equals("automatic_notable_mbti_sync",
                                StringComparison.OrdinalIgnoreCase)
                        && applied.Contains("traits",
                            StringComparer.OrdinalIgnoreCase))
                    {
                        EnsureNotableMbtiSchema(c);
                        ExecuteSql(c, @"UPDATE notable_mbti_profiles SET
native_sync_status='synchronized',native_action_id='',
last_native_observed_json=$observed,updated_ts=$ts
WHERE hero_id=$hero;", new Dictionary<string, object>
                        {
                            ["observed"] = Json.Serialize(
                                ReadDictionary(changes, "traits")
                                    ?? new Dictionary<string, object>()),
                            ["ts"] = ts, ["hero"] = hero
                        });
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["commandId"] = id, ["status"] = status
            };
        }

        private static Dictionary<string, object> EditorIdParams(string id){return new Dictionary<string, object>{{"id",id},{"like","%\""+id.Replace("\"","")+"\"%"}};}
        private static string ComputeEditorToken(Dictionary<string, object> snapshot){using(SHA256 sha=SHA256.Create()){byte[] hash=sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Serialize(snapshot??new Dictionary<string, object>())));return BitConverter.ToString(hash).Replace("-","").ToLowerInvariant();}}
        private static long DirectorySize(string path){try{return Directory.Exists(path)?Directory.GetFiles(path,"*",SearchOption.AllDirectories).Sum(x=>new FileInfo(x).Length):0;}catch{return 0;}}

        private static void ApplyEditorDocuments(string campaign,string hero,Dictionary<string, object> documents){if(documents==null)return;PrepareEditorNarrative(campaign,hero,documents);Dictionary<string,object> incomingProfile=ReadDictionary(documents,"profile")??ReadJsonObject(CharacterFile(campaign,hero,"profile.json"));foreach(string name in EditorDocumentNames){Dictionary<string, object> value=ReadDictionary(documents,name);if(value==null)continue;if(name=="profile"){string id=ReadFirstString(value,"heroStringId","heroId");if(!string.IsNullOrWhiteSpace(id)&&!id.Equals(hero,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Canonical heroStringId cannot be changed.");value["heroStringId"]=hero;}if(name=="traits"){MarkEditorFoundationTraitsCurrent(value);EnsureTraitPercentageData(value,hero);EnsureCourtCharacterData(value,incomingProfile,hero);EnsurePersonalityPortraitData(value,ReadString(incomingProfile,"name",hero),false);}WriteJsonObject(CharacterFile(campaign,hero,name+".json"),value);}Dictionary<string, object> profile=ReadJsonObject(CharacterFile(campaign,hero,"profile.json"));if(profile.Count>0)UpdateCharacterIndex(campaign,profile);ReconcileEditedNarrative(campaign,hero);InvalidatePromptRuntimeCache();}
        private static void ApplyEditorHistory(string campaign,string hero,Dictionary<string, object> history){if(history==null)return;foreach(string name in new[]{"dialogue","events","decisions"}){List<Dictionary<string, object>> rows=ReadDictionaryList(history,name);if(!history.ContainsKey(name))continue;WriteEditorJsonLines(CharacterFile(campaign,hero,"history",name+".jsonl"),rows);}if(history.ContainsKey("dialogue")){using(ReignDbConnection c=OpenCampaignConnection(campaign))SyncEditorConversationTurns(c,hero,ReadDictionaryList(history,"dialogue"));}}
        private static void WriteEditorJsonLines(string path,List<Dictionary<string, object>> rows){lock(FileLock){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllLines(path,(rows??new List<Dictionary<string, object>>()).Select(Json.Serialize),Encoding.UTF8);}}
        private static void ApplyEditorRecords(ReignDbConnection c,string hero,Dictionary<string, object> records){if(records==null)return;foreach(Tuple<string,string,string> spec in new[]{Tuple.Create("worldEvents","events","event_id"),Tuple.Create("knowledgeReceipts","knowledge_receipts","receipt_id"),Tuple.Create("conversationSessions","conversation_sessions","session_id"),Tuple.Create("conversationTurns","conversation_turns","turn_id"),Tuple.Create("beliefs","beliefs","belief_id"),Tuple.Create("comprehension","comprehension","comprehension_id"),Tuple.Create("obligations","obligations","obligation_id"),Tuple.Create("summaries","summaries","summary_id"),Tuple.Create("memories","memories","memory_id"),Tuple.Create("letters","letters","letter_id"),Tuple.Create("conceptions","conceptions","conception_id"),Tuple.Create("parentage","parentage","child_id")})ApplyEditableRecordRows(c,spec.Item1,spec.Item2,spec.Item3,records);}

        private static void SyncEditorConversationTurns(ReignDbConnection c,string hero,List<Dictionary<string, object>> rows)
        {
            HashSet<string> activeIds=new HashSet<string>((rows??new List<Dictionary<string, object>>()).Select(x=>ReadString(x,"id","")).Where(x=>!string.IsNullOrWhiteSpace(x)),StringComparer.OrdinalIgnoreCase);
            foreach(Dictionary<string, object> row in rows??new List<Dictionary<string, object>>())
            {
                string id=ReadString(row,"id","");if(string.IsNullOrWhiteSpace(id))continue;Dictionary<string, object> existing=QuerySql(c,"SELECT * FROM conversation_turns WHERE turn_id=$id LIMIT 1;",new Dictionary<string, object>{{"id",id}}).FirstOrDefault();if(existing==null)continue;
                ExecuteSql(c,"UPDATE conversation_turns SET text=$text,speaker_name=$speaker,role=$role,world_day=$day,status='active' WHERE turn_id=$id;",new Dictionary<string, object>{{"text",ReadString(row,"text","")},{"speaker",ReadString(row,"speaker","")},{"role",ReadString(row,"role","")},{"day",ReadDouble(row,"worldDay",ReadDouble(existing,"world_day",0d))},{"id",id}});
            }
            foreach(Dictionary<string, object> existing in QuerySql(c,@"SELECT t.turn_id FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE s.npc_id=$id AND t.status='active';",new Dictionary<string, object>{{"id",hero}}))
            {
                string id=ReadString(existing,"turn_id","");if(!activeIds.Contains(id))ExecuteSql(c,"UPDATE conversation_turns SET status='superseded' WHERE turn_id=$id;",new Dictionary<string, object>{{"id",id}});
            }
            RebuildCategorizedSearchIndexes(c);
        }
        private static void ApplyEditableRecordRows(ReignDbConnection c,string group,string table,string key,Dictionary<string, object> records){List<Dictionary<string, object>> rows=ReadDictionaryList(records,group);foreach(Dictionary<string, object> row in rows){string id=ReadString(row,key,"");if(string.IsNullOrWhiteSpace(id))continue;Dictionary<string, object> existing=QuerySql(c,"SELECT * FROM "+table+" WHERE "+key+"=$id LIMIT 1;",new Dictionary<string, object>{{"id",id}}).FirstOrDefault();if(existing==null)continue;List<string> columns=row.Keys.Where(x=>existing.ContainsKey(x)&&x!=key&&System.Text.RegularExpressions.Regex.IsMatch(x,"^[A-Za-z_][A-Za-z0-9_]*$")).ToList();if(columns.Count==0)continue;Dictionary<string, object> args=new Dictionary<string, object>{{"id",id}};foreach(string column in columns)args[column]=row[column];ExecuteSql(c,"UPDATE "+table+" SET "+string.Join(",",columns.Select(x=>x+"=$"+x))+" WHERE "+key+"=$id;",args);}}
        private static void ReconcileCharacterEditor(string campaign,string hero,ReignDbConnection c,string section,string job,string revision){long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();ExecuteSql(c,"INSERT INTO character_editor_reconciliation(job_id,revision_id,hero_id,section,status,summary,created_ts,updated_ts) VALUES($job,$revision,$hero,$section,'running','Rebuilding character projections.',$ts,$ts);",new Dictionary<string, object>{{"job",job},{"revision",revision},{"hero",hero},{"section",section},{"ts",ts}});try{RebuildCategorizedSearchIndexes(c);ExecuteSql(c,"DELETE FROM summary_fts;");foreach(Dictionary<string, object> row in QuerySql(c,"SELECT * FROM summaries WHERE status='active';"))InsertSummaryFts(c,ReadString(row,"summary_id",""),ReadString(row,"summary",""),TextListFromJson(ReadString(row,"tags_json","[]")),ExtractEntityIdsFromJson(ReadString(row,"about_entities_json","[]")));ExecuteSql(c,"UPDATE character_editor_reconciliation SET status='completed',summary='Search and prompt projections rebuilt.',updated_ts=$ts WHERE job_id=$job;",new Dictionary<string, object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"job",job}});}catch(Exception ex){ExecuteSql(c,"UPDATE character_editor_reconciliation SET status='failed',summary=$summary,result_json=$result,updated_ts=$ts WHERE job_id=$job;",new Dictionary<string, object>{{"summary",ex.Message},{"result",Json.Serialize(new Dictionary<string, object>{{"error",ex.Message}})},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"job",job}});}}
        private static void ApplyEditorSnapshot(string campaign,string hero,ReignDbConnection c,Dictionary<string, object> snapshot){ApplyEditorDocuments(campaign,hero,ReadDictionary(snapshot,"documents"));ApplyEditorHistory(campaign,hero,ReadDictionary(snapshot,"history"));ApplyEditorRecords(c,hero,ReadDictionary(snapshot,"records"));}

        private static Dictionary<string, object> BuildNativeInverse(Dictionary<string, object> before,Dictionary<string, object> changes)
        {
            Dictionary<string, object> inverse=new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),profile=ReadDictionary(ReadDictionary(before,"documents"),"profile")??new Dictionary<string, object>();foreach(string key in changes.Keys)if(profile.ContainsKey(key))inverse[key]=profile[key];return inverse;
        }

        private static List<long> ChangedHistoryTimestamps(Dictionary<string, object> before,Dictionary<string, object> after)
        {
            HashSet<long> changed=new HashSet<long>();if(after==null)return changed.ToList();foreach(string group in new[]{"dialogue","events"})
            {
                Dictionary<string,string> oldRows=ReadDictionaryList(before,group).Where(x=>!string.IsNullOrWhiteSpace(ReadString(x,"id",""))).ToDictionary(x=>ReadString(x,"id",""),Json.Serialize,StringComparer.OrdinalIgnoreCase);
                foreach(Dictionary<string, object> row in ReadDictionaryList(after,group)){string id=ReadString(row,"id","");if(string.IsNullOrWhiteSpace(id)||!oldRows.TryGetValue(id,out string old)||old!=Json.Serialize(row))changed.Add(ReadLong(row,"ts",0));oldRows.Remove(id);}foreach(string old in oldRows.Values){Dictionary<string, object> row=TryParseJsonObject(old)??new Dictionary<string, object>();changed.Add(ReadLong(row,"ts",0));}
            }
            changed.Remove(0);return changed.ToList();
        }

        private static void RebuildEditedHistoryProjections(string campaign,string hero,ReignDbConnection c,Dictionary<string, object> history,List<long> timestamps)
        {
            foreach(long ts in timestamps)
            {
                List<string> eventIds=QuerySql(c,"SELECT event_id FROM events WHERE ts=$ts;",new Dictionary<string, object>{{"ts",ts}}).Select(x=>ReadString(x,"event_id","")).Where(x=>!string.IsNullOrWhiteSpace(x)).ToList();foreach(string eventId in eventIds)ExecuteSql(c,"UPDATE memories SET status='superseded' WHERE event_id=$event;",new Dictionary<string, object>{{"event",eventId}});
                List<Dictionary<string, object>> lines=ReadDictionaryList(history,"dialogue").Concat(ReadDictionaryList(history,"events")).Where(x=>ReadLong(x,"ts",0)==ts).ToList();if(lines.Count==0)continue;string summary=string.Join("\n",lines.Select(x=>ReadString(x,"speaker",ReadString(x,"role",""))+": "+ReadString(x,"text","")));Dictionary<string, object> profile=ReadJsonObject(CharacterFile(campaign,hero,"profile.json"));StoreWorldMemoryEvent(new Dictionary<string, object>{{"campaignId",campaign},{"eventId","editor_history_"+hero+"_"+ts},{"ts",ts},{"eventType","edited_conversation"},{"summary",summary},{"participants",new[]{hero,ReadFirstString(profile,"mainHeroStringId","playerHeroStringId") }},{"known_by",new[]{hero,ReadFirstString(profile,"mainHeroStringId","playerHeroStringId") }},{"visibility","private"},{"importance",0.6d},{"source","character_editor_rebuild"}},"character_editor_rebuild");
            }
        }

        private static List<KeyValuePair<string,string>> CharacterEditorGameCacheFolders(string campaign,string hero)
        {
            List<KeyValuePair<string,string>> folders=new List<KeyValuePair<string,string>>();
            if(string.IsNullOrWhiteSpace(hero))return folders;string root=CharacterEditorPortraitCacheRoot();if(string.IsNullOrWhiteSpace(root))return folders;
            foreach(string scope in new[]{"_shared",campaign}.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string dir=Path.Combine(root,SafePathSegment(scope,"default"));if(!Directory.Exists(dir))continue;try{string suffix="("+hero+")";string match=Directory.GetDirectories(dir).FirstOrDefault(x=>Path.GetFileName(x).EndsWith(suffix,StringComparison.OrdinalIgnoreCase));if(!string.IsNullOrWhiteSpace(match))folders.Add(new KeyValuePair<string,string>(scope=="_shared"?"shared":"campaign",match));}catch{}
            }
            return folders;
        }

        private static string CharacterEditorPortraitCacheRoot()
        {
            string modulesRoot=FindRelationshipSimulationModulesRoot();
            string installed=string.IsNullOrWhiteSpace(modulesRoot)?"":Path.Combine(modulesRoot,"ReignBeta","PortraitCache");
            if(Directory.Exists(installed))return installed;
            DirectoryInfo app=new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);string module=app.Parent?.Parent?.FullName??"";string fallback=string.IsNullOrWhiteSpace(module)?"":Path.Combine(module,"PortraitCache");return Directory.Exists(fallback)?fallback:"";
        }

        private static string ResolveCharacterEditorPortraitPath(string campaign,string hero,string variant)
        {
            string serverDir=CharacterFile(campaign,hero,"portraits");variant=SafePathSegment(variant,"active").ToLowerInvariant();List<KeyValuePair<string,string>> locations=CharacterEditorGameCacheFolders(campaign,hero);locations.Add(new KeyValuePair<string,string>("server",serverDir));
            if(variant=="active")
            {
                foreach(string preferred in new[]{"portrait","custom","source"})
                    foreach(KeyValuePair<string,string> location in locations.AsEnumerable().Reverse())
                    {
                        string match=FindCharacterEditorPortraitFile(location.Value,preferred);if(!string.IsNullOrWhiteSpace(match))return match;
                    }
            }
            else
            {
                foreach(KeyValuePair<string,string> location in locations.AsEnumerable().Reverse())
                {
                    string match=FindCharacterEditorPortraitFile(location.Value,variant);if(!string.IsNullOrWhiteSpace(match))return match;
                }
                if(variant=="custom")
                {
                    string active=FindCharacterEditorPortraitFile(serverDir,"portrait");if(!string.IsNullOrWhiteSpace(active))return active;
                }
            }
            return Path.Combine(serverDir,"__missing_portrait__.png");
        }

        private static string FindCharacterEditorPortraitFile(string directory,string variant)
        {
            if(string.IsNullOrWhiteSpace(directory)||string.IsNullOrWhiteSpace(variant))return "";
            string path=Path.Combine(directory,variant+".png");return File.Exists(path)?path:"";
        }

        private static List<Dictionary<string, object>> CharacterEditorPortraitInventory(string campaign,string hero)
        {
            List<KeyValuePair<string,string>> locations=CharacterEditorGameCacheFolders(campaign,hero);locations.Add(new KeyValuePair<string,string>("server",CharacterFile(campaign,hero,"portraits")));return CharacterEditorPortraitInventoryFromLocations(campaign,hero,locations);
        }

        private static List<Dictionary<string, object>> CharacterEditorPortraitInventoryFromLocations(string campaign,string hero,List<KeyValuePair<string,string>> locations)
        {
            Dictionary<string,Dictionary<string,object>> variants=new Dictionary<string,Dictionary<string,object>>(StringComparer.OrdinalIgnoreCase);
            foreach(KeyValuePair<string,string> location in locations??new List<KeyValuePair<string,string>>())
            {
                string dir=location.Value;if(!Directory.Exists(dir))continue;
                foreach(string path in Directory.GetFiles(dir,"*.png"))
                {
                    string name=Path.GetFileNameWithoutExtension(path);variants[name]=new Dictionary<string, object>{{"variant",name},{"fileName",Path.GetFileName(path)},{"origin",location.Key},{"bytes",new FileInfo(path).Length},{"updatedUtc",File.GetLastWriteTimeUtc(path).ToString("o")},{"url","/character-editor/portrait?campaignId="+Uri.EscapeDataString(campaign)+"&heroStringId="+Uri.EscapeDataString(hero)+"&variant="+Uri.EscapeDataString(name)}};
                }
            }
            return variants.Values.OrderBy(x=>ReadString(x,"variant","").Equals("portrait",StringComparison.OrdinalIgnoreCase)?0:ReadString(x,"variant","").Equals("custom",StringComparison.OrdinalIgnoreCase)?1:ReadString(x,"variant","").Equals("source",StringComparison.OrdinalIgnoreCase)?3:2).ThenBy(x=>ReadString(x,"variant",""),StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, object> CharacterEditorPortraitUploadApi(Dictionary<string, object> payload){string campaign=ReadString(payload,"campaignId",LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),data=ReadString(payload,"dataUrl",""),name=SafePathSegment(ReadString(payload,"fileName","custom.png"),"custom.png");int comma=data.IndexOf(',');if(comma>=0)data=data.Substring(comma+1);byte[] bytes;try{bytes=Convert.FromBase64String(data);}catch{return new Dictionary<string, object>{{"ok",false},{"error","The uploaded image was not valid."}};}if(bytes.Length>15*1024*1024)return new Dictionary<string, object>{{"ok",false},{"error","Portrait must be 15 MB or smaller."}};string version="custom_"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)+".png",path=CharacterFile(campaign,hero,"portraits",version);lock(FileLock){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,bytes);}return new Dictionary<string, object>{{"ok",true},{"variant",Path.GetFileNameWithoutExtension(version)},{"portraits",CharacterEditorPortraitInventory(campaign,hero)}};}
        private static Dictionary<string, object> CharacterEditorPortraitSelectApi(Dictionary<string, object> payload){string campaign=ReadString(payload,"campaignId",LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),variant=SafePathSegment(ReadString(payload,"variant",""),"");string source=ResolveCharacterEditorPortraitPath(campaign,hero,variant),active=CharacterFile(campaign,hero,"portraits","portrait.png");if(!File.Exists(source))return new Dictionary<string, object>{{"ok",false},{"error","Portrait variant was not found."}};lock(FileLock){if(File.Exists(active)&&!string.Equals(Path.GetFullPath(active),Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase))File.Copy(active,CharacterFile(campaign,hero,"portraits","active_"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+".png"),true);if(!string.Equals(Path.GetFullPath(active),Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase)){Directory.CreateDirectory(Path.GetDirectoryName(active));File.Copy(source,active,true);}}WriteJsonObject(CharacterFile(campaign,hero,"portraits","portrait.json"),new Dictionary<string, object>{{"campaignId",campaign},{"heroStringId",hero},{"activeVariant",variant},{"source",source.IndexOf("PortraitCache",StringComparison.OrdinalIgnoreCase)>=0?"game_cache":"server"},{"updatedUtc",DateTime.UtcNow.ToString("o")}});return new Dictionary<string, object>{{"ok",true},{"activeUrl","/character-editor/portrait?campaignId="+Uri.EscapeDataString(campaign)+"&heroStringId="+Uri.EscapeDataString(hero)+"&variant=active"}};}

        private static string FinanceSnapshotIndexPath(string campaignId)
        {
            return Path.Combine(CampaignDirectory(campaignId), "finance", "character-finance-index.json");
        }

        private static Dictionary<string, object> PersistFinanceSnapshotIndex(
            string campaignId,
            List<Dictionary<string, object>> snapshots,
            double worldDay)
        {
            snapshots = snapshots ?? new List<Dictionary<string, object>>();
            Dictionary<string, object> characters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> clans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> snapshot in snapshots)
            {
                string heroId = ReadFirstString(snapshot, "heroStringId", "heroId");
                Dictionary<string, object> wealth = ReadDictionary(snapshot, "wealth");
                if (string.IsNullOrWhiteSpace(heroId) || wealth == null || wealth.Count == 0)
                    continue;

                Dictionary<string, object> normalized = NormalizeObservedWealth(wealth);
                normalized["heroStringId"] = heroId;
                normalized["heroName"] = ReadString(snapshot, "name", heroId);
                normalized["clanId"] = ReadString(snapshot, "clanId", "");
                normalized["clanName"] = ReadString(snapshot, "clanName", "");
                normalized["kingdomId"] = ReadString(snapshot, "kingdomId", "");
                normalized["isRuler"] = ReadBool(snapshot, "isRuler", false);
                normalized["isClanLeader"] = ReadBool(snapshot, "isClanLeader", false);
                if (ReadDouble(normalized, "observedWorldDay", 0d) <= 0d)
                    normalized["observedWorldDay"] = worldDay;
                characters[heroId] = normalized;
                string clanId = ReadString(snapshot, "clanId", "");
                if (!string.IsNullOrWhiteSpace(clanId)) clans.Add(clanId);
            }

            if (characters.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    ["captured"] = false,
                    ["characterCount"] = 0,
                    ["reason"] = "No finance snapshots were supplied; the previous authoritative index was preserved."
                };
            }

            Dictionary<string, object> index = new Dictionary<string, object>
            {
                ["version"] = 1,
                ["campaignId"] = campaignId,
                ["dataState"] = "observed",
                ["source"] = "bannerlord_native_identity_census",
                ["observedWorldDay"] = worldDay,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["characterCount"] = characters.Count,
                ["clanCount"] = clans.Count,
                ["characters"] = characters
            };
            WriteJsonObject(FinanceSnapshotIndexPath(campaignId), index);
            return new Dictionary<string, object>
            {
                ["captured"] = true,
                ["characterCount"] = characters.Count,
                ["clanCount"] = clans.Count,
                ["observedWorldDay"] = worldDay,
                ["storageMode"] = "single_index_atomic_replace"
            };
        }

        private static Dictionary<string, object> NormalizeObservedWealth(Dictionary<string, object> wealth)
        {
            Dictionary<string, object> normalized = wealth == null
                ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object>(wealth, StringComparer.OrdinalIgnoreCase);
            normalized["version"] = Math.Max(2, ReadInt(normalized, "version", 0));
            if (string.IsNullOrWhiteSpace(ReadString(normalized, "dataState", "")))
                normalized["dataState"] = "observed";
            if (string.IsNullOrWhiteSpace(ReadString(normalized, "source", "")))
                normalized["source"] = "bannerlord_native";
            normalized["economicCapacityKnown"] = true;
            if (string.IsNullOrWhiteSpace(ReadString(normalized, "wealthEvidenceBasis", "")))
                normalized["wealthEvidenceBasis"] = "bannerlord_native";
            return normalized;
        }

        private static bool IsObservedWealth(Dictionary<string, object> wealth)
        {
            if (wealth == null || wealth.Count == 0) return false;
            string state = ReadString(wealth, "dataState", "");
            if (state.Equals("unknown", StringComparison.OrdinalIgnoreCase)
                || state.Equals("not_captured", StringComparison.OrdinalIgnoreCase))
                return false;
            if (state.Equals("observed", StringComparison.OrdinalIgnoreCase)
                || state.Equals("editor_override", StringComparison.OrdinalIgnoreCase))
                return true;
            string notes = ReadString(wealth, "notes", "");
            if (notes.IndexOf("No detailed wealth payload was supplied", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return wealth.ContainsKey("gold") && wealth.ContainsKey("clanGold");
        }

        private static Dictionary<string, object> FinanceSnapshotForHero(string campaignId, string heroId)
        {
            Dictionary<string, object> index = ReadJsonObject(FinanceSnapshotIndexPath(campaignId));
            Dictionary<string, object> characters = ReadDictionary(index, "characters");
            if (characters == null || !characters.TryGetValue(heroId ?? "", out object value))
                return new Dictionary<string, object>();
            Dictionary<string, object> snapshot = value as Dictionary<string, object>;
            return snapshot == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(snapshot, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> ResolveWealthDocument(
            string campaignId,
            string heroId,
            Dictionary<string, object> profile)
        {
            Dictionary<string, object> stored = ReadJsonObject(CharacterFile(campaignId, heroId, "wealth.json"));
            Dictionary<string, object> indexed = FinanceSnapshotForHero(campaignId, heroId);
            bool storedObserved = IsObservedWealth(stored);
            bool indexedObserved = IsObservedWealth(indexed);
            if (indexedObserved && (!storedObserved
                || ReadDouble(indexed, "observedWorldDay", 0d) >= ReadDouble(stored, "observedWorldDay", 0d)))
                return indexed;
            if (storedObserved) return stored;
            return UnknownWealth(profile);
        }

        private static Dictionary<string, object> UnknownWealth(Dictionary<string, object> hero)
        {
            hero = hero ?? new Dictionary<string, object>();
            int clanTier = ReadInt(hero, "clanTier", 0);
            int clanFiefCount = ReadInt(hero, "clanFiefCount", ReadInt(hero, "fiefCount", 0));
            double clanRenown = ReadDouble(hero, "clanRenown", ReadDouble(hero, "renown", 0d));
            bool isLord = ReadBool(hero, "isLord", false);
            return new Dictionary<string, object>
            {
                ["version"] = 2,
                ["dataState"] = "unknown",
                ["source"] = "not_captured",
                ["observedWorldDay"] = null,
                ["observedUtc"] = "",
                ["gold"] = null,
                ["personalWealthTier"] = "unknown",
                ["partyInventoryValue"] = null,
                ["visibleWealthTier"] = "unknown",
                ["clanGold"] = null,
                ["clanWealthTier"] = "unknown",
                ["clanTier"] = clanTier,
                ["clanRenown"] = Math.Round(clanRenown, 2),
                ["clanFiefCount"] = clanFiefCount,
                ["clanSocialCredit"] = ClanSocialCredit(0, clanTier, clanRenown, clanFiefCount, isLord),
                ["canLeanOnClanReputation"] = clanTier >= 3 || clanRenown >= 200d || clanFiefCount > 0 || isLord,
                ["economicCapacityKnown"] = false,
                ["wealthEvidenceBasis"] = "not_captured",
                ["goldSemantics"] = "unknown",
                ["notes"] = "No authoritative finance snapshot has been captured from the loaded game. Unknown values are not zero and must not be interpreted as poverty."
            };
        }

        private static List<Dictionary<string, object>> RunCharacterEditorSubsystemSelfTests()
        {
            List<Dictionary<string, object>> rows=new List<Dictionary<string, object>>();Action<string,bool,string> add=(id,pass,summary)=>rows.Add(new Dictionary<string, object>{{"id","character_editor_"+id},{"suite","character_editor"},{"passed",pass},{"summary",summary}});
            Dictionary<string, object> sample=new Dictionary<string, object>{{"documents",new Dictionary<string, object>{{"profile",new Dictionary<string, object>{{"heroStringId","hero_a"},{"name","A"},{"age",30}}}}}};add("revision_token",ComputeEditorToken(sample)==ComputeEditorToken(sample),"Equal snapshots produce equal optimistic revision tokens.");
            Dictionary<string, object> inverse=BuildNativeInverse(sample,new Dictionary<string, object>{{"name","B"},{"age",31}});add("native_inverse",ReadString(inverse,"name","")=="A"&&ReadInt(inverse,"age",0)==30,"Native rollback values are captured from the pre-save profile.");
            Dictionary<string,object> unknownWealth=UnknownWealth(new Dictionary<string,object>{{"isLord",true},{"clanTier",6},{"clanFiefCount",4}});
            add("missing_wealth_is_unknown",
                ReadString(unknownWealth,"dataState","")=="unknown"
                &&unknownWealth["gold"]==null
                &&ReadString(unknownWealth,"personalWealthTier","")=="unknown"
                &&!ReadBool(unknownWealth,"economicCapacityKnown",true),
                "Missing native finance evidence remains unknown instead of becoming zero or destitute.");
            Dictionary<string, object> before=new Dictionary<string, object>{{"dialogue",new ArrayList{new Dictionary<string, object>{{"id","line_a"},{"ts",100L},{"text","old"}}}}};Dictionary<string, object> after=new Dictionary<string, object>{{"dialogue",new ArrayList{new Dictionary<string, object>{{"id","line_a"},{"ts",100L},{"text","new"}}}}};add("history_change_detection",ChangedHistoryTimestamps(before,after).SequenceEqual(new[]{100L}),"Edited transcript lines identify their derived projection timestamp.");
            add("document_contract",EditorDocumentNames.Length==17&&EditorDocumentNames.Contains("traits")&&EditorDocumentNames.Contains("hidden_history"),"The editor covers all character construction and runtime documents.");
            string portraitFixture=Path.Combine(Path.GetTempPath(),"reign_character_editor_portraits_"+Guid.NewGuid().ToString("N"));
            try
            {
                string shared=Path.Combine(portraitFixture,"shared"),campaignOverride=Path.Combine(portraitFixture,"campaign"),serverOverride=Path.Combine(portraitFixture,"server");Directory.CreateDirectory(shared);Directory.CreateDirectory(campaignOverride);Directory.CreateDirectory(serverOverride);
                File.WriteAllBytes(Path.Combine(shared,"portrait.png"),new byte[]{1,2,3});File.WriteAllBytes(Path.Combine(shared,"portrait_chest.png"),new byte[]{4,5,6});File.WriteAllBytes(Path.Combine(shared,"source.png"),new byte[]{7});File.WriteAllBytes(Path.Combine(campaignOverride,"source.png"),new byte[]{8,9});File.WriteAllBytes(Path.Combine(campaignOverride,"zoom.png"),new byte[]{10,11,12});
                List<Dictionary<string,object>> portraitRows=CharacterEditorPortraitInventoryFromLocations("campaign_a","hero_a",new List<KeyValuePair<string,string>>{new KeyValuePair<string,string>("shared",shared),new KeyValuePair<string,string>("campaign",campaignOverride),new KeyValuePair<string,string>("server",serverOverride)});
                Dictionary<string,object> sourceRow=portraitRows.FirstOrDefault(x=>ReadString(x,"variant","")=="source");
                add("portrait_shared_merge",
                    portraitRows.Any(x=>ReadString(x,"variant","")=="portrait")
                    &&portraitRows.Any(x=>ReadString(x,"variant","")=="portrait_chest")
                    &&portraitRows.Any(x=>ReadString(x,"variant","")=="zoom")
                    &&sourceRow!=null&&ReadString(sourceRow,"origin","")=="campaign"&&ReadInt(sourceRow,"bytes",0)==2,
                    "The Portraits tab keeps every shared visual asset while campaign files override only matching variants.");
                add("portrait_png_detection",
                    Path.GetFileName(FindCharacterEditorPortraitFile(shared,"portrait_chest"))=="portrait_chest.png"
                    &&Path.GetFileName(FindCharacterEditorPortraitFile(campaignOverride,"zoom"))=="zoom.png",
                    "Every PNG portrait derivative remains independently addressable from the merged inventory.");
            }
            catch(Exception ex)
            {
                add("portrait_shared_merge_fixture",false,ex.ToString());
            }
            finally
            {
                TryDeleteDirectory(portraitFixture);
            }
            string campaign="ce_"+Guid.NewGuid().ToString("N").Substring(0,12);
            try
            {
                Dictionary<string,Dictionary<string,object>> shippedProfiles=LoadCharacterProfileLibrary();
                KeyValuePair<string,Dictionary<string,object>> shippedEntry=shippedProfiles.FirstOrDefault(pair=>ReadString(pair.Value,"source","").Equals("native_sandbox",StringComparison.OrdinalIgnoreCase));
                string shippedHeroId=shippedEntry.Key??"";
                if(!string.IsNullOrWhiteSpace(shippedHeroId))
                {
                    Directory.CreateDirectory(CharacterFile(campaign,shippedHeroId,"memory"));
                    EnsureFile(CharacterFile(campaign,shippedHeroId,"memory","memories.jsonl"));
                    Dictionary<string,object> shippedFacts=ReadDictionary(shippedEntry.Value,"sourceFacts")??new Dictionary<string,object>();
                    Dictionary<string,object> listResult=CharacterEditorListApi(new Dictionary<string,object>{{"campaignId",campaign}});
                    Dictionary<string,object> listRow=ReadDictionaryList(listResult,"characters").FirstOrDefault(item=>ReadString(item,"heroStringId","").Equals(shippedHeroId,StringComparison.OrdinalIgnoreCase));
                    add("sparse_canonical_visible",
                        listRow!=null
                        &&ReadString(listRow,"name","")==ReadString(shippedFacts,"name",shippedHeroId)
                        &&ReadString(listRow,"constructionStatus","")=="canonical_ready"
                        &&ReadString(listRow,"profilePackVersion","")==CharacterProfilePackVersion,
                        "A shipped noble remains fully identified in the editor when campaign storage contains only sparse relationship data.");
                    add("roster_is_text_only",
                        listRow!=null&&!listRow.ContainsKey("portraitUrl"),
                        "The lightweight character roster does not resolve or return portrait URLs.");
                    Dictionary<string,object> loaded=CharacterEditorLoadApi(new Dictionary<string,object>{{"campaignId",campaign},{"heroStringId",shippedHeroId}});
                    Dictionary<string,object> loadedDocuments=ReadDictionary(loaded,"documents")??new Dictionary<string,object>();
                    Dictionary<string,object> loadedProfile=ReadDictionary(loadedDocuments,"profile")??new Dictionary<string,object>();
                    Dictionary<string,object> loadedFoundation=ReadDictionary(loaded,"profileLibrary")??new Dictionary<string,object>();
                    add("sparse_canonical_materialized",
                        ReadBool(loaded,"ok",false)
                        &&ReadString(loadedProfile,"name","")==ReadString(shippedFacts,"name",shippedHeroId)
                        &&ReadString(loadedFoundation,"packVersion","")==CharacterProfilePackVersion
                        &&ReadString(ReadDictionary(loadedDocuments,"constructed"),"status","")=="canonical_ready",
                        "Opening a sparse shipped noble materializes the canonical profile without player knowledge or an LLM call.");
                }
                else
                {
                    add("sparse_canonical_fixture",false,"No native shipped profile was available for the sparse-record test.");
                }

                Dictionary<string,object> heroA=new Dictionary<string,object>{{"heroStringId","editor_a"},{"name","Editor A"},{"mainHeroStringId","main_hero"},{"relationToPlayer",12}};
                Dictionary<string,object> heroB=new Dictionary<string,object>{{"heroStringId","editor_b"},{"name","Editor B"}};
                WriteJsonObject(CharacterFile(campaign,"editor_a","profile.json"),heroA);
                WriteJsonObject(CharacterFile(campaign,"editor_b","profile.json"),heroB);
                WriteJsonObject(CharacterFile(campaign,"editor_a","wealth.json"),UnknownWealth(heroA));
                Dictionary<string,object> financeResult=PersistFinanceSnapshotIndex(campaign,new List<Dictionary<string,object>>
                {
                    new Dictionary<string,object>
                    {
                        ["heroStringId"]="editor_a",["name"]="Editor A",["clanId"]="clan_a",
                        ["wealth"]=new Dictionary<string,object>
                        {
                            ["gold"]=456789,["clanGold"]=456789,["personalWealthTier"]="great wealth",
                            ["clanWealthTier"]="great wealth",["partyInventoryValue"]=8000,["clanTier"]=6,
                            ["clanRenown"]=2400d,["clanFiefCount"]=4,["observedWorldDay"]=25d
                        }
                    }
                },25d);
                Dictionary<string,object> resolvedFinance=ResolveWealthDocument(campaign,"editor_a",heroA);
                add("finance_census_resolves_unknown_cache",
                    ReadBool(financeResult,"captured",false)
                    &&ReadInt(resolvedFinance,"gold",0)==456789
                    &&ReadString(resolvedFinance,"dataState","")=="observed"
                    &&ReadBool(resolvedFinance,"economicCapacityKnown",false),
                    "One atomic native finance census supplies authoritative wealth without per-character daily writes.");
                WriteJsonObject(CharacterFile(campaign,"main_hero","profile.json"),new Dictionary<string,object>{{"heroStringId","main_hero"},{"name","Player"}});
                WriteJsonObject(CharacterFile(campaign,"editor_a","traits.json"),new Dictionary<string,object>{{"foundationTraits",new Dictionary<string,object>{{"assertiveness",2},{"sociability",1},{"pragmatism",1}}}});
                WriteJsonObject(CharacterFile(campaign,"editor_b","traits.json"),new Dictionary<string,object>{{"foundationTraits",new Dictionary<string,object>{{"empathy",1},{"patience",1},{"loyalty",2}}}});
                using(ReignDbConnection connection=OpenCampaignConnection(campaign))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string pair=AmbientPairKey("editor_a","editor_b");
                    ExecuteSql(connection,@"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
shared_tag,projected_native_relation,first_day,last_day,last_context_kind,last_context_id,updated_ts)
VALUES($pair,'editor_a','editor_b','ENTJ','ISFJ',72,63,62,53,74,41,'devoted','friend',
'close_friends',58,10,10,'party','editor_party',$ts);",
                        new Dictionary<string,object>{{"pair",pair},{"ts",ts}});
                    ExecuteSql(connection,@"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,lover_started_day,last_processed_day,updated_ts)
VALUES($pair,'editor_a','editor_b',1,10,10,$ts);",
                        new Dictionary<string,object>{{"pair",pair},{"ts",ts}});
                    List<Dictionary<string,object>> network=BuildCharacterEditorRelationshipNetwork(campaign,"editor_a",connection,heroA);
                    Dictionary<string,object> npc=network.FirstOrDefault(item=>ReadString(item,"otherHeroId","")=="editor_b");
                    Dictionary<string,object> player=network.FirstOrDefault(item=>ReadString(item,"relationshipKind","")=="player");
                    add("relationship_network_exposes_mbti_and_directional_levels",
                        npc!=null&&ReadString(npc,"observerMbti","")=="ENTJ"
                        &&ReadString(npc,"otherMbti","")=="ISFJ"
                        &&ReadInt(npc,"directionalAffinity",0)==74
                        &&ReadInt(npc,"reverseAffinity",0)==41
                        &&ReadInt(npc,"nativeRelation",0)==58,
                        "The Relationships tab exposes MBTI, both directional opinions, and projected native relation for every met NPC pair.");
                    add("relationship_network_exposes_player_and_tags",
                        player!=null&&ReadInt(player,"nativeRelation",0)==12
                        &&npc!=null&&ReadStringList(npc,"tags").Contains("lovers"),
                        "The Relationships tab includes the player's standing and persistent lifecycle tags.");
                }
            }
            catch(Exception ex)
            {
                add("relationship_network_fixture",false,ex.ToString());
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaign));
            }
            return rows;
        }
    }
}
