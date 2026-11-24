using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private static readonly string[] EditorDocumentNames =
        {
            "profile", "template", "characteristics", "wealth", "inventory", "appearance", "traits", "background", "voice",
            "motivations", "save_quirks", "hidden_history", "secrets", "pressure", "state", "constructed", "narrative"
        };

        private static readonly byte[] EditorProfileBasisKey = RandomNumberGenerator.GetBytes(32);
        private static readonly HashSet<string> EditorProfileManagedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "campaignId", "heroStringId", "heroId", "characterObjectId", "createdUtc", "updatedUtc"
        };

        private static readonly HashSet<string> EditorDangerousProfileFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "isAlive", "isPregnant", "spouseId", "fatherId", "motherId", "clanId", "kingdomId"
        };

        internal static void EnsureCharacterEditorSchema(ReignDbConnection connection)
        {
            CharactersSchema.CreateCharacterEditorHeads(connection);
            CharactersSchema.CreateCharacterEditorRevisions(connection);
            CharactersSchema.CreateIndexEditorRevisionsHero(connection);
            CharactersSchema.CreateCharacterEditorNativeCommands(connection);
            CharactersSchema.CreateIndexEditorNativeCommandsStatus(connection);
            CharactersSchema.CreateCharacterEditorReconciliation(connection);
        }

        internal static Dictionary<string, object> CharacterEditorListApi(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaign=ReadString(payload,"campaignId",SettingsService.LatestCampaignId());if(string.IsNullOrWhiteSpace(campaign))campaign=SettingsService.LatestCampaignId();string root=Path.Combine(CampaignFileService.CampaignDirectory(campaign),"characters");List<Dictionary<string, object>> characters=new List<Dictionary<string, object>>();
            Dictionary<string,Dictionary<string,object>> shippedProfiles=LoadCharacterProfileLibrary();
            HashSet<string> characterIds=new HashSet<string>(shippedProfiles.Keys,StringComparer.OrdinalIgnoreCase);
            if(Directory.Exists(root))foreach(string dir in Directory.GetDirectories(root))characterIds.Add(Path.GetFileName(dir));
            using(ReignDbConnection c=ReignDatabase.OpenCampaignConnection(campaign))
            {
            Dictionary<string,int> pendingByHero=CharactersStore.SelectCharacterEditorNativeCommands(c)
                .Where(row=>!string.IsNullOrWhiteSpace(ReadString(row,"hero_id","")))
                .ToDictionary(row=>ReadString(row,"hero_id",""),row=>ReadInt(row,"count",0),StringComparer.OrdinalIgnoreCase);
            foreach(string id in characterIds)
            {
                string dir=Path.Combine(root,id);Dictionary<string, object> profile=CampaignFileService.ReadJsonObject(Path.Combine(dir,"profile.json"));Dictionary<string, object> constructed=CampaignFileService.ReadJsonObject(Path.Combine(dir,"constructed.json"));int pending=pendingByHero.TryGetValue(id,out int pendingCount)?pendingCount:0;
                profile=WithNarrativeCampaignVersion(campaign,profile);
                Dictionary<string,object> shipped=null;CharacterProfileLibraryFor(profile).TryGetValue(id,out shipped);
                Dictionary<string,object> displayProfile=BuildCharacterEditorCanonicalProfile(shipped,profile,id);
                Dictionary<string, object> library=CampaignFileService.ReadJsonObject(Path.Combine(dir,"profile_library.json"));Dictionary<string, object> enrichment=CampaignFileService.ReadJsonObject(Path.Combine(dir,"enrichment.json"));
                string shippedSource=ReadString(shipped,"source","");
                string shippedPack=shipped==null?"":ReadString(shipped,"packVersion",CharacterProfilePackVersion);
                characters.Add(new Dictionary<string, object>{{"heroStringId",id},{"name",ReadString(displayProfile,"name",id)},{"title",ReadFirstString(displayProfile,"title","occupation")},{"occupation",ReadString(displayProfile,"occupation","")},{"clanId",ReadString(displayProfile,"clanId","")},{"kingdomId",ReadString(displayProfile,"kingdomId","")},{"isLord",ReadBool(displayProfile,"isLord",false)},{"isNotable",ReadBool(displayProfile,"isNotable",false)},{"isWanderer",ReadBool(displayProfile,"isWanderer",false)},{"isAlive",ReadBool(displayProfile,"isAlive",true)},{"constructionStatus",ReadString(constructed,"status",shipped==null?"not_constructed":"canonical_ready")},{"profileSource",ReadString(library,"profileSource",shippedSource)},{"profilePackVersion",ReadString(library,"packVersion",shippedPack)},{"enrichmentStatus",ReadString(enrichment,"status",shipped==null?"":"not_required")},{"pendingNativeChanges",pending}});
            }
            }
            return new Dictionary<string, object>{{"ok",true},{"campaignId",campaign},{"characters",characters.OrderBy(x=>ReadString(x,"name","")).ToList()}};
        }

        internal static Dictionary<string, object> CharacterEditorLoadApi(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaign=ReadString(payload,"campaignId",SettingsService.LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId");if(string.IsNullOrWhiteSpace(campaign))campaign=SettingsService.LatestCampaignId();if(string.IsNullOrWhiteSpace(hero))return new Dictionary<string, object>{{"ok",false},{"error","heroStringId is required."}};
            Dictionary<string,object> runtimeProfile=CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign,hero,"profile.json"));
            Dictionary<string,Dictionary<string,object>> shippedProfiles=LoadCharacterProfileLibrary();
            if(shippedProfiles.TryGetValue(hero,out Dictionary<string,object> shipped))
            {
                runtimeProfile=BuildCharacterEditorCanonicalProfile(shipped,runtimeProfile,hero);
                CharactersService.UpsertCharacterProfile(campaign,runtimeProfile);
            }
            else if(runtimeProfile.Count>0)MaterializeCharacterProfile(campaign,hero,runtimeProfile,false,false);
            using(ReignDbConnection c=ReignDatabase.OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> snapshot=BuildCharacterEditorSnapshot(campaign,hero,c);Dictionary<string, object> head=CharactersStore.SelectCharacterEditorHeads(c, new Dictionary<string, object>{{"id",hero}}).FirstOrDefault();string token=ReadString(head,"revision_token","");if(string.IsNullOrWhiteSpace(token)){token=ComputeEditorToken(snapshot);CharactersStore.UpsertCharacterEditorHeads(c, new Dictionary<string, object>{{"id",hero},{"token",token},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()}});}snapshot["revisionToken"]=token;
                string generation = EditorProfileRestoreGeneration(c);
                snapshot["profileEditBasis"] = IssueEditorProfileBasis(campaign, hero, token, generation,
                    ReadDictionary(ReadDictionary(snapshot, "documents"), "profile"));
                snapshot["historyEditBasis"] = IssueEditorHistoryBasis(ReadDictionary(snapshot, "history"),
                    new EditorHistoryEditBasis(campaign, hero, token, generation));
                snapshot["ok"] = true;
                return snapshot;
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

        internal static Dictionary<string, object> CharacterEditorConstructApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaign = ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
            string hero = ReadFirstString(payload, "heroStringId", "heroId");
            if (string.IsNullOrWhiteSpace(campaign)) campaign = SettingsService.LatestCampaignId();
            if (string.IsNullOrWhiteSpace(hero)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "heroStringId is required." };

            Dictionary<string, object> profile = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "profile.json"));
            if (profile.Count == 0) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The game profile has not been captured for this character." };

            Dictionary<string, object> result = CharactersService.ConstructCharacterFiles(
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

        private static Dictionary<string, object> BuildCharacterEditorSnapshot(string campaign,string hero,ReignDbConnection c,
            bool persistDerivedTraits = true)
        {
            RelationshipsService.EnsureConversationRelationshipSchema(c);
            ReputationService.EnsureSocialReputationSchema(c);
            string dir=CampaignFileService.CharacterDirectory(campaign,hero);Dictionary<string, object> profileDocument=CampaignFileService.ReadJsonObject(Path.Combine(dir,"profile.json"));Dictionary<string, object> traitDocument=CampaignFileService.ReadJsonObject(Path.Combine(dir,"traits.json"));if(traitDocument.Count>0){bool changed=EnsureTraitPercentageData(traitDocument,hero);changed=CourtService.EnsureCourtCharacterData(traitDocument,profileDocument,hero)||changed;changed=EnsurePersonalityPortraitData(traitDocument,ReadString(profileDocument,"name",hero),false)||changed;if(changed&&persistDerivedTraits)CampaignFileService.WriteJsonObject(Path.Combine(dir,"traits.json"),traitDocument);}Dictionary<string, object> documents=new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);foreach(string name in EditorDocumentNames)documents[name]=name=="traits"?traitDocument:name=="profile"?profileDocument:name=="wealth"?ResolveWealthDocument(campaign,hero,profileDocument):CampaignFileService.ReadJsonObject(Path.Combine(dir,name+".json"));
            Dictionary<string, object> history = ReadEditorHistory(Path.Combine(dir, "history"));
            Dictionary<string, object> memoryFiles=new Dictionary<string, object>{{"summary",CampaignFileService.ReadJsonObject(Path.Combine(dir,"memory","summary.json"))},{"relationships",CampaignFileService.ReadJsonObject(Path.Combine(dir,"memory","relationships.json"))},{"memories",Program.ReadJsonLinesFromPath(Path.Combine(dir,"memory","memories.jsonl"))}};
            Dictionary<string, object> profile=ReadDictionary(documents,"profile")??new Dictionary<string, object>();DialogueService.KnowledgeAccessContext knowledge=DialogueService.BuildKnowledgeAccessContext(hero,ReadFirstString(profile,"mainHeroStringId","playerHeroStringId"),ReadFirstString(profile,"currentSettlementId","settlementId"),profile);
            List<Dictionary<string, object>> relationshipNetwork=BuildCharacterEditorRelationshipNetwork(campaign,hero,c,profile);
            Dictionary<string, object> records=new Dictionary<string, object>
            {
                ["worldEvents"]=DialogueService.QueryKnownRows(c,"events",knowledge,"",1000),
                ["knowledgeReceipts"]=CharactersStore.SelectKnowledgeReceipts(c, EditorIdParams(hero)),
                ["conversationSessions"]=CharactersStore.SelectConversationSessions(c, EditorIdParams(hero)),
                ["conversationTurns"]=CharactersStore.SelectConversationTurns(c, EditorIdParams(hero)),
                ["memorySources"]=CharactersStore.SelectMemorySources(c, EditorIdParams(hero)),
                ["memories"]=DialogueService.QueryKnownRows(c,"memories",knowledge,"status='active'",1000),
                ["summaries"]=DialogueService.QueryKnownRows(c,"summaries",knowledge,"status='active'",500),
                ["beliefs"]=DialogueService.QueryKnownRows(c,"beliefs",knowledge,"",500),
                ["comprehension"]=DialogueService.QueryKnownRows(c,"comprehension",knowledge,"",500),
                ["obligations"]=DialogueService.QueryKnownRows(c,"obligations",knowledge,"",500),
                ["dynamicCharacteristics"]=CharactersStore.SelectDynamicCharacteristics(c, EditorIdParams(hero)),
                ["relationshipNetwork"]=relationshipNetwork,
                ["sharedRelationshipHistory"]=RelationshipsService.SharedRelationshipHistoryForCharacter(campaign,hero,c),
                ["relationshipIncidents"]=CharactersStore.SelectRelationshipIncidents(c, EditorIdParams(hero)),
                ["conversationRelationshipReceipts"]=CharactersStore.SelectConversationRelationshipReceipts(c, EditorIdParams(hero)),
                ["conversationBenefits"]=CharactersStore.SelectConversationRelationshipBenefits(c, EditorIdParams(hero)),
                ["acquaintances"]=CharactersStore.SelectAcquaintances(c, EditorIdParams(hero)),
                ["identityEvidence"]=CharactersStore.SelectIdentityEvidence(c, EditorIdParams(hero)),
                ["rumors"]=CharactersStore.SelectRumorSubjectTags(c, EditorIdParams(hero)),
                ["socialReputations"]=CharactersStore.SelectCharacterReputations(c, EditorIdParams(hero)),
                ["letters"]=CharactersStore.SelectLetters(c, EditorIdParams(hero)),
                ["conceptions"]=CharactersStore.SelectConceptions(c, EditorIdParams(hero)),
                ["parentage"]=CharactersStore.SelectParentage(c, EditorIdParams(hero))
            };
            var narrative = ReadDictionary(documents, "narrative");
            if (NarrativeDocumentReady(narrative))
                documents["narrative"] = ResolveEffectiveNarrative(new Dictionary<string, object> {
                    ["narrative"] = narrative, ["dynamicCharacteristics"] = CampaignFileService.ReadJsonObject(Path.Combine(dir, "dynamic_characteristics.json")) });
            List<Dictionary<string, object>> revisions=CharactersStore.SelectCharacterEditorRevisions(c, new Dictionary<string, object>{{"id",hero}});
            List<Dictionary<string, object>> commands=CharactersStore.SelectCharacterEditorNativeCommands2(c, new Dictionary<string, object>{{"id",hero}});
            Dictionary<string, object> mbtiProfile=RelationshipsService.ResolveCharacterMbtiProfile(campaign,hero,profileDocument,traitDocument);
            return new Dictionary<string, object>{{"campaignId",campaign},{"heroStringId",hero},{"documents",documents},{"profileLibrary",CampaignFileService.ReadJsonObject(Path.Combine(dir,"profile_library.json"))},{"campaignProfileOverlay",CampaignFileService.ReadJsonObject(Path.Combine(dir,"campaign_profile_overlay.json"))},{"enrichment",CampaignFileService.ReadJsonObject(Path.Combine(dir,"enrichment.json"))},{"memoryFiles",memoryFiles},{"history",history},{"records",records},{"socialStanding",ReputationService.CharacterSocialStandingProjection(c,campaign,hero)},{"portraits",CharacterEditorPortraitInventory(campaign,hero)},{"revisions",revisions},{"nativeCommands",commands},{"traitDefinitions",TraitDefinitions()},{"traitKeys",CoreTraitKeys.ToList()},{"courtVirtueKeys",CourtVirtueKeys.ToList()},{"mbtiProfile",mbtiProfile},{"mbtiType",ReadString(mbtiProfile,"type","XXXX")},{"storageBytes",DirectorySize(dir)}};
        }

        private static List<Dictionary<string, object>> BuildCharacterEditorRelationshipNetwork(
            string campaign, string hero, ReignDbConnection connection, Dictionary<string, object> profile)
        {
            RelationshipsService.EnsureMbtiRelationshipSchema(connection);
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            string playerId = ReadFirstString(profile, "mainHeroStringId", "playerHeroStringId");
            bool hasPlayerChemistry = !string.IsNullOrWhiteSpace(playerId) && CharactersStore.SelectRelationshipPairChemistry(connection, new Dictionary<string, object> { ["pair"] = RelationshipsService.AmbientPairKey(hero, playerId) }).Count > 0;
            if (!string.IsNullOrWhiteSpace(playerId) && !playerId.Equals(hero, StringComparison.OrdinalIgnoreCase) && !hasPlayerChemistry)
            {
                Dictionary<string, object> playerProfile = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, playerId, "profile.json"));
                result.Add(new Dictionary<string, object>
                {
                    ["otherHeroId"] = playerId,
                    ["otherHeroName"] = ReadString(playerProfile, "name", "Player"),
                    ["relationshipKind"] = "player",
                    ["directionalAffinity"] = ReadInt(profile, "relationToPlayer", 0),
                    ["reverseAffinity"] = "",
                    ["nativeRelation"] = ReadInt(profile, "relationToPlayer", 0),
                    ["observerMbti"] = ReadString(RelationshipsService.ResolveCharacterMbtiProfile(
                        campaign, hero, profile, CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, hero, "traits.json"))),
                        "type", "XXXX"),
                    ["otherMbti"] = "",
                    ["compatibilityChance"] = "",
                    ["tags"] = new List<string> { "player_relationship" }
                });
            }
            foreach (Dictionary<string, object> row in CharactersStore.SelectRelationshipPairChemistry2(connection, new Dictionary<string, object> { ["id"] = hero }))
            {
                bool observerIsA = ReadString(row, "hero_a_id", "").Equals(hero, StringComparison.OrdinalIgnoreCase);
                string otherId = ReadString(row, observerIsA ? "hero_b_id" : "hero_a_id", "");
                if (string.IsNullOrWhiteSpace(otherId)) continue;
                Dictionary<string, object> otherProfile = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, otherId, "profile.json"));
                Dictionary<string, object> lifecycle = RelationshipsService.RelationshipLifecycleView(connection, hero, otherId);
                result.Add(new Dictionary<string, object>
                {
                    ["otherHeroId"] = otherId,
                    ["otherHeroName"] = ReadString(otherProfile, "name", otherId),
                    ["relationshipKind"] = otherId.Equals(playerId, StringComparison.OrdinalIgnoreCase) ? "player" : "npc",
                    ["directionalAffinity"] = ReputationService.EffectiveDirectionalAffinity(row, observerIsA, 0),
                    ["reverseAffinity"] = ReputationService.EffectiveDirectionalAffinity(row, !observerIsA, 0),
                    ["nativeRelation"] = ReadInt(row, "projected_native_relation", 0),
                    ["observerMbti"] = ReadString(row, observerIsA ? "mbti_a" : "mbti_b", ""),
                    ["otherMbti"] = ReadString(row, observerIsA ? "mbti_b" : "mbti_a", ""),
                    ["compatibilityChance"] = ReadInt(row, observerIsA ? "chance_a_to_b" : "chance_b_to_a", 0),
                    ["reverseCompatibilityChance"] = ReadInt(row, observerIsA ? "chance_b_to_a" : "chance_a_to_b", 0),
                    ["perceptionTag"] = RelationshipsService.RelationshipTagForObserver(connection, campaign, hero, otherId,
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

        internal static Dictionary<string, object> CharacterEditorSaveApi(Dictionary<string, object> payload)
        {
            lock (NarrativeStateLock) return CharacterEditorSaveApiLocked(payload);
        }

        private static Dictionary<string, object> CharacterEditorSaveApiLocked(Dictionary<string, object> payload)
        {
            payload=payload??new Dictionary<string, object>();string campaign=ReadString(payload,"campaignId",SettingsService.LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),section=ReadString(payload,"section","all"),token=ReadString(payload,"revisionToken","");if(string.IsNullOrWhiteSpace(campaign))campaign=SettingsService.LatestCampaignId();if(string.IsNullOrWhiteSpace(hero))return new Dictionary<string, object>{{"ok",false},{"error","heroStringId is required."}};
            using(ReignDbConnection c=ReignDatabase.OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> head=CharactersStore.SelectCharacterEditorHeads(c, new Dictionary<string, object>{{"id",hero}}).FirstOrDefault();string current=ReadString(head,"revision_token","");if(!string.IsNullOrWhiteSpace(current)&&!string.Equals(token,current,StringComparison.Ordinal))return new Dictionary<string, object>{{"ok",false},{"conflict",true},{"error","This character changed after it was loaded. Reload to compare before saving."},{"currentRevisionToken",current}};
                if (!TryReadEditorHistoryEdits(payload, out var historyEdits))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["conflict"] = true, ["currentRevisionToken"] = current,
                        ["error"] = "The history change request was invalid. Reload the Character Editor before saving."
                    };
                var documents = ReadDictionary(payload, "documents");
                var native = ReadDictionary(payload, "nativeChanges") ?? new Dictionary<string, object>();
                bool hasProfileEdit = payload.ContainsKey("profileEdit");
                // The original snapshot and narrative validation must complete before the first profile write.
                var before = BuildCharacterEditorSnapshot(campaign, hero, c, persistDerivedTraits: !hasProfileEdit);
                if (hasProfileEdit)
                {
                    if (documents != null) PrepareEditorNarrative(campaign, hero, documents);
                    if (!TryApplyEditorProfileEdit(campaign, hero, current, EditorProfileRestoreGeneration(c),
                        payload, native, out var profileBefore))
                    {
                        return new Dictionary<string, object>
                        {
                            ["ok"] = false, ["conflict"] = true, ["currentRevisionToken"] = current,
                            ["error"] = "The profile edit could not be applied to the current character. Reload to compare before saving."
                        };
                    }
                    if (profileBefore != null) ReadDictionary(before, "documents")["profile"] = profileBefore;
                }
                ApplyEditorDocuments(campaign, hero, documents, hasProfileEdit);
                ApplyEditorRecords(c, ReadDictionary(payload, "records"), ReadDictionary(before, "records"));
                var historyDelta = new EditorHistoryDelta();
                var historyBasis = historyEdits.Count == 0 ? null
                    : new EditorHistoryEditBasis(campaign, hero, current, EditorProfileRestoreGeneration(c));
                var historyIssues = ApplyEditorHistory(campaign, hero, c, null, null, historyEdits, historyBasis, historyDelta);
                Dictionary<string, object> inverse = BuildNativeInverse(before, native);
                string revision="editor_revision_"+Guid.NewGuid().ToString("N");long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();string job="editor_reconcile_"+Guid.NewGuid().ToString("N");ReconcileCharacterEditor(campaign,hero,c,section,job,revision);
                var after = BuildCharacterEditorSnapshot(campaign, hero, c);
                string next = ComputeEditorToken(after);
                before[EditorHistoryDeltaKey] = historyDelta.Before;
                after[EditorHistoryDeltaKey] = historyDelta.After;
                CharactersStore.InsertCharacterEditorRevisions(c, new Dictionary<string, object>{{"revision",revision},{"hero",hero},{"section",section},{"description",ReadString(payload,"description","Character Editor save")},{"before",Json.Serialize(before)},{"after",Json.Serialize(after)},{"native",Json.Serialize(native)},{"reconcile",Json.Serialize(new Dictionary<string, object>{{"jobId",job},{"status","completed"},{"historyProjectionIssues",historyIssues}})},{"ts",ts}});
                if(native.Count>0)CharactersStore.InsertCharacterEditorNativeCommands(c, new Dictionary<string, object>{{"id","editor_command_"+Guid.NewGuid().ToString("N")},{"revision",revision},{"hero",hero},{"changes",Json.Serialize(native)},{"inverse",Json.Serialize(inverse)},{"dangerous",ReadBool(payload,"dangerous",false)?1:0},{"confirmation",ReadString(payload,"confirmation","")},{"ts",ts}});
                CharactersStore.UpsertCharacterEditorHeads2(c, new Dictionary<string, object>{{"hero",hero},{"token",next},{"revision",revision},{"ts",ts}});OperationsService.LogOperational("character_editor.save",new Dictionary<string, object>{{"campaignId",campaign},{"heroStringId",hero},{"section",section},{"revisionId",revision},{"nativeChangeCount",native.Count}});
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,hero,"editor_authority.json"),new Dictionary<string, object>{{"version",1},{"authoritative",true},{"latestRevisionId",revision},{"updatedUtc",DateTime.UtcNow.ToString("o")}});
                return new Dictionary<string, object>{{"ok",true},{"revisionId",revision},{"revisionToken",next},{"reconciliationJobId",job},{"nativeQueued",native.Count>0},{"historyProjectionIssues",historyIssues},{"message","Server changes were saved. Native Bannerlord changes will be applied the next time this savegame is loaded."+EditorHistoryIssueMessage(historyIssues)}};
            }
        }

        private static string EditorProfileRestoreGeneration(ReignDbConnection connection) =>
            ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");

        private static string EditorProfileValueHash(object value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(value))));

        // Seal the values returned by this load against character, editor head and restore identity.
        // A restart invalidates open profile drafts along with the process-local signing key.
        private static string IssueEditorProfileBasis(string campaign, string hero, string revision, string generation,
            Dictionary<string, object> profile)
        {
            var fields = profile.ToDictionary(field => field.Key, field => EditorProfileValueHash(field.Value), StringComparer.Ordinal);
            byte[] body = Encoding.UTF8.GetBytes(Json.Serialize(fields));
            string encoded = Convert.ToBase64String(body);
            return encoded + "." + Convert.ToHexString(SignEditorProfileBasis(campaign, hero, revision, generation, encoded));
        }

        private static byte[] SignEditorProfileBasis(string campaign, string hero, string revision, string generation, string body) =>
            HMACSHA256.HashData(EditorProfileBasisKey,
                Encoding.UTF8.GetBytes(Json.Serialize(new[] { "profile-edit-v1", campaign, hero, revision, generation, body })));

        private static Dictionary<string, object> ReadEditorProfileBasis(string campaign, string hero, string revision,
            string generation, string basis)
        {
            int separator = basis.LastIndexOf('.');
            if (separator <= 0 || basis.Length - separator != 65) return null;
            string body = basis.Substring(0, separator);
            try
            {
                byte[] signature = Convert.FromHexString(basis.Substring(separator + 1));
                if (!CryptographicOperations.FixedTimeEquals(signature,
                    SignEditorProfileBasis(campaign, hero, revision, generation, body))) return null;
                return TryParseStrictJsonObject(Encoding.UTF8.GetString(Convert.FromBase64String(body)));
            }
            catch (FormatException) { return null; }
        }

        private static bool TryApplyEditorProfileEdit(string campaign, string hero, string revision, string generation,
            Dictionary<string, object> payload, Dictionary<string, object> native, out Dictionary<string, object> before)
        {
            before = null;
            var edit = ReadDictionary(payload, "profileEdit");
            var fields = ReadDictionary(edit, "fields");
            if (fields == null) return false;
            var basis = ReadEditorProfileBasis(campaign, hero, revision, generation, ReadString(edit, "basis", ""));
            if (basis == null) return false;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (fields.Keys.Any(key => string.IsNullOrWhiteSpace(key) || EditorProfileManagedFields.Contains(key) || !names.Add(key)))
                return false;
            if (fields.Keys.Any(key => !native.ContainsKey(key))
                || native.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != native.Count) return false;
            var wealth = ReadDictionary(ReadDictionary(payload, "documents"), "wealth");
            foreach (var change in native)
            {
                string value = CanonicalJson(change.Value);
                if (fields.TryGetValue(change.Key, out object expected) && CanonicalJson(expected) == value) continue;
                // Wealth can also come from its independently edited document.
                if (change.Key == "wealth" && wealth != null && CanonicalJson(wealth) == value) continue;
                return false;
            }
            if (EditorDangerousProfileFields.Overlaps(names) && (!ReadBool(payload, "dangerous", false)
                || !ConfirmsEditorProfileName(hero, basis, fields, ReadString(payload, "confirmation", "")))) return false;
            if (fields.Count == 0) return true;

            Dictionary<string, object> preimage = null;
            bool saved = CampaignFileService.TryUpdateJsonObject(CampaignFileService.CharacterFile(campaign, hero, "profile.json"), current =>
            {
                var replacements = MergeEditorProfileFields(current, basis, fields);
                if (replacements != null) preimage = current;
                return replacements;
            });
            before = preimage;
            return saved;
        }

        private static bool ConfirmsEditorProfileName(string hero, Dictionary<string, object> basis,
            Dictionary<string, object> fields, string confirmation)
        {
            if (fields.TryGetValue("name", out object editedName))
                return confirmation == (editedName is string name && name.Length > 0 ? name : hero);
            if (!basis.TryGetValue("name", out object loadedName)
                || Equals(loadedName, EditorProfileValueHash(null)) || Equals(loadedName, EditorProfileValueHash("")))
                return confirmation == hero;
            return Equals(loadedName, EditorProfileValueHash(confirmation));
        }

        private static Dictionary<string, object> MergeEditorProfileFields(Dictionary<string, object> current,
            Dictionary<string, object> basis, Dictionary<string, object> changes)
        {
            foreach (string key in changes.Keys)
            {
                bool wasPresent = basis.TryGetValue(key, out object expectedHash);
                bool isPresent = current.TryGetValue(key, out object actual);
                if (wasPresent != isPresent || (isPresent && !Equals(expectedHash, EditorProfileValueHash(actual)))) return null;
                if (!isPresent && (basis.Keys.Contains(key, StringComparer.OrdinalIgnoreCase)
                    || current.Keys.Contains(key, StringComparer.OrdinalIgnoreCase))) return null;
            }

            var merged = new Dictionary<string, object>(current, StringComparer.Ordinal);
            foreach (var change in changes) merged[change.Key] = change.Value;
            return merged;
        }

        internal static Dictionary<string, object> CharacterEditorRollbackApi(Dictionary<string, object> payload)
        {
            lock (NarrativeStateLock) return CharacterEditorRollbackApiLocked(payload);
        }

        private static Dictionary<string, object> CharacterEditorRollbackApiLocked(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", SettingsService.LatestCampaignId());
            string hero = ReadFirstString(payload, "heroStringId", "heroId");
            string revision = ReadFirstString(payload, "revisionId", "revision_id");
            using var c = ReignDatabase.OpenCampaignConnection(campaign);
            var row = CharactersStore.SelectCharacterEditorRevisions2(c, new Dictionary<string, object>
                { ["revision"] = revision, ["hero"] = hero }).FirstOrDefault();
            if (row == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Revision was not found." };

            var snapshot = TryParseJsonObject(ReadString(row, "before_json", "{}"));
            var revisionAfter = TryParseJsonObject(ReadString(row, "after_json", "{}"));
            if (!TryReadEditorHistoryRevision(snapshot, revisionAfter, out var history, out var postimage, out bool scopedUndo))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The retained history revision is invalid." };
            var before = BuildCharacterEditorSnapshot(campaign, hero, c);
            ApplyEditorDocuments(campaign, hero, ReadDictionary(snapshot, "documents"));
            ApplyEditorRecords(c, ReadDictionary(snapshot, "records"), ReadDictionary(revisionAfter, "records"));
            var historyDelta = new EditorHistoryDelta();
            var historyIssues = ApplyEditorHistory(campaign, hero, c, history, postimage, null, null, historyDelta, scopedUndo);

            string newRevision = "editor_revision_" + Guid.NewGuid().ToString("N");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var command = CharactersStore.SelectCharacterEditorNativeCommands3(c, new Dictionary<string, object> { ["id"] = revision }).FirstOrDefault();
            var inverse = TryParseJsonObject(ReadString(command, "inverse_json", "{}")) ?? new Dictionary<string, object>();
            if (inverse.Count > 0)
                CharactersStore.InsertCharacterEditorNativeCommands2(c, new Dictionary<string, object>
                    { ["id"] = "editor_command_" + Guid.NewGuid().ToString("N"), ["revision"] = newRevision,
                      ["hero"] = hero, ["changes"] = Json.Serialize(inverse), ["ts"] = ts });
            var after = BuildCharacterEditorSnapshot(campaign, hero, c);
            string token = ComputeEditorToken(after);
            before[EditorHistoryDeltaKey] = historyDelta.Before;
            after[EditorHistoryDeltaKey] = historyDelta.After;
            CharactersStore.InsertCharacterEditorRevisions2(c, new Dictionary<string, object>
                { ["id"] = newRevision, ["hero"] = hero, ["description"] = "Rollback of " + revision,
                  ["before"] = Json.Serialize(before), ["after"] = Json.Serialize(after),
                  ["native"] = Json.Serialize(inverse), ["rollback"] = revision, ["ts"] = ts });
            CharactersStore.UpsertCharacterEditorHeads2(c, new Dictionary<string, object>
                { ["hero"] = hero, ["token"] = token, ["revision"] = newRevision, ["ts"] = ts });
            ReconcileCharacterEditor(campaign, hero, c, "rollback", "editor_reconcile_" + Guid.NewGuid().ToString("N"), newRevision);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["revisionId"] = newRevision, ["revisionToken"] = token, ["historyProjectionIssues"] = historyIssues,
                ["message"] = "Revision restored. Any native rollback changes will be applied the next time this savegame is loaded."
                    + EditorHistoryIssueMessage(historyIssues)
            };
        }

        internal static Dictionary<string, object> CharacterEditorNativePollApi(Dictionary<string, object> payload){string campaign=ReadString(payload,"campaignId","default");using(ReignDbConnection c=ReignDatabase.OpenCampaignConnection(campaign)){long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();List<Dictionary<string, object>> rows=CharactersStore.SelectCharacterEditorNativeCommands4(c, new Dictionary<string, object>{{"stale",ts-300}});foreach(Dictionary<string, object> row in rows){CharactersStore.UpdateCharacterEditorNativeCommands(c, new Dictionary<string, object>{{"ts",ts},{"id",ReadString(row,"command_id","")}});row["changes"]=TryParseJsonObject(ReadString(row,"changes_json","{}"))??new Dictionary<string, object>();}return new Dictionary<string, object>{{"ok",true},{"commands",rows}};}}
        internal static Dictionary<string, object> CharacterEditorNativeReportApi(
            Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "default");
            string id = ReadFirstString(payload, "commandId", "command_id");
            string status = ReadString(payload, "status", "completed");
            using (ReignDbConnection c = ReignDatabase.OpenCampaignConnection(campaign))
            {
                Dictionary<string, object> command = CharactersStore.SelectCharacterEditorNativeCommands5(c, new Dictionary<string, object> { ["id"] = id })
                    .FirstOrDefault();
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                CharactersStore.UpdateCharacterEditorNativeCommands2(c, new Dictionary<string, object>
                    {
                        ["status"] = status, ["ts"] = ts,
                        ["result"] = Json.Serialize(payload), ["id"] = id
                    });
                if (command != null && (status == "completed"
                    || status == "partially_applied"))
                {
                    string hero = ReadString(command, "hero_id", "");
                    Dictionary<string, object> profile = CampaignFileService.ReadJsonObject(
                        CampaignFileService.CharacterFile(campaign, hero, "profile.json"));
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
                    CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, hero,
                        "profile.json"), profile);
                    CampaignFileService.UpdateCharacterIndex(campaign, profile);

                    if (ReadString(command, "confirmation", "")
                            .Equals("automatic_notable_mbti_sync",
                                StringComparison.OrdinalIgnoreCase)
                        && applied.Contains("traits",
                            StringComparer.OrdinalIgnoreCase))
                    {
                        RelationshipsService.EnsureNotableMbtiSchema(c);
                        CharactersStore.UpdateNotableMbtiProfiles(c, new Dictionary<string, object>
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

        private static void ApplyEditorDocuments(string campaign,string hero,Dictionary<string, object> documents,bool profileAlreadyPrepared=false){if(documents==null)return;if(!profileAlreadyPrepared)PrepareEditorNarrative(campaign,hero,documents);Dictionary<string,object> incomingProfile=(profileAlreadyPrepared?null:ReadDictionary(documents,"profile"))??CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign,hero,"profile.json"));foreach(string name in EditorDocumentNames){if(profileAlreadyPrepared&&name=="profile")continue;Dictionary<string, object> value=ReadDictionary(documents,name);if(value==null)continue;if(name=="profile"){string id=ReadFirstString(value,"heroStringId","heroId");if(!string.IsNullOrWhiteSpace(id)&&!id.Equals(hero,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Canonical heroStringId cannot be changed.");value["heroStringId"]=hero;}if(name=="traits"){MarkEditorFoundationTraitsCurrent(value);EnsureTraitPercentageData(value,hero);CourtService.EnsureCourtCharacterData(value,incomingProfile,hero);EnsurePersonalityPortraitData(value,ReadString(incomingProfile,"name",hero),false);}CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,hero,name+".json"),value);}Dictionary<string, object> profile=CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign,hero,"profile.json"));if(profile.Count>0)CampaignFileService.UpdateCharacterIndex(campaign,profile);ReconcileEditedNarrative(campaign,hero);DialogueService.InvalidatePromptRuntimeCache();}
        private static void ApplyEditorRecords(ReignDbConnection c, Dictionary<string, object> records, Dictionary<string, object> baseline)
        {
            if (records == null || baseline == null) return;
            foreach (var (group, table, key) in new[]
            {
                ("worldEvents", "events", "event_id"), ("knowledgeReceipts", "knowledge_receipts", "receipt_id"),
                ("conversationSessions", "conversation_sessions", "session_id"), ("conversationTurns", "conversation_turns", "turn_id"),
                ("beliefs", "beliefs", "belief_id"), ("comprehension", "comprehension", "comprehension_id"),
                ("obligations", "obligations", "obligation_id"), ("summaries", "summaries", "summary_id"),
                ("memories", "memories", "memory_id"), ("letters", "letters", "letter_id"),
                ("conceptions", "conceptions", "conception_id"), ("parentage", "parentage", "child_id")
            })
            {
                var originals = ReadDictionaryList(baseline, group).ToLookup(row => ReadString(row, key, ""), StringComparer.Ordinal);
                foreach (var row in ReadDictionaryList(records, group))
                {
                    string id = ReadString(row, key, "");
                    if (id.Length == 0 || originals[id].Count() != 1) continue;
                    var original = originals[id].Single();
                    var args = new Dictionary<string, object> { ["id"] = id };
                    var columns = row.Keys.Where(column => column != key && original.TryGetValue(column, out var previous)
                        && CanonicalJson(row[column]) != CanonicalJson(previous)
                        && System.Text.RegularExpressions.Regex.IsMatch(column, "^[A-Za-z_][A-Za-z0-9_]*$")).ToList();
                    if (columns.Count == 0) continue;
                    foreach (string column in columns) args[column] = row[column];
                    CharactersStore.UpdateDynamic(c, table, string.Join(",", columns.Select(column => column + "=$" + column)), key, args);
                }
            }
        }
        private static void ReconcileCharacterEditor(string campaign,string hero,ReignDbConnection c,string section,string job,string revision){long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();CharactersStore.InsertCharacterEditorReconciliation(c, new Dictionary<string, object>{{"job",job},{"revision",revision},{"hero",hero},{"section",section},{"ts",ts}});try{DialogueService.RebuildCategorizedSearchIndexes(c);CharactersStore.DeleteSummaryFts(c);foreach(Dictionary<string, object> row in CharactersStore.SelectSummaries(c))DialogueService.InsertSummaryFts(c,ReadString(row,"summary_id",""),ReadString(row,"summary",""),DialogueService.TextListFromJson(ReadString(row,"tags_json","[]")),DialogueService.ExtractEntityIdsFromJson(ReadString(row,"about_entities_json","[]")));CharactersStore.UpdateCharacterEditorReconciliation(c, new Dictionary<string, object>{{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"job",job}});}catch(Exception ex){CharactersStore.UpdateCharacterEditorReconciliation2(c, new Dictionary<string, object>{{"summary",ex.Message},{"result",Json.Serialize(new Dictionary<string, object>{{"error",ex.Message}})},{"ts",DateTimeOffset.UtcNow.ToUnixTimeSeconds()},{"job",job}});}}
        private static Dictionary<string, object> BuildNativeInverse(Dictionary<string, object> before,Dictionary<string, object> changes)
        {
            Dictionary<string, object> inverse=new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),profile=ReadDictionary(ReadDictionary(before,"documents"),"profile")??new Dictionary<string, object>();foreach(string key in changes.Keys)if(profile.ContainsKey(key))inverse[key]=profile[key];return inverse;
        }

        private static List<KeyValuePair<string,string>> CharacterEditorGameCacheFolders(string campaign,string hero)
        {
            List<KeyValuePair<string,string>> folders=new List<KeyValuePair<string,string>>();
            if(string.IsNullOrWhiteSpace(hero))return folders;string root=CharacterEditorPortraitCacheRoot();
            if(!string.IsNullOrWhiteSpace(root)) foreach(string scope in new[]{"_shared",campaign}.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string dir=Path.Combine(root,CampaignFileService.SafePathSegment(scope,"default"));if(!Directory.Exists(dir))continue;try{string suffix="("+hero+")";string match=Directory.GetDirectories(dir).FirstOrDefault(x=>Path.GetFileName(x).EndsWith(suffix,StringComparison.OrdinalIgnoreCase));if(!string.IsNullOrWhiteSpace(match))folders.Add(new KeyValuePair<string,string>(scope=="_shared"?"shared":"campaign",match));}catch{}
            }
            // Generated campaign portraits live with private server data; the installed module owns shared portraits.
            string campaignCache=PersistenceService.CampaignPortraitCacheDirectory(campaign);
            if(Directory.Exists(campaignCache))
            {
                try{string suffix="("+hero+")";string match=Directory.GetDirectories(campaignCache).FirstOrDefault(x=>Path.GetFileName(x).EndsWith(suffix,StringComparison.OrdinalIgnoreCase));if(!string.IsNullOrWhiteSpace(match))folders.Add(new KeyValuePair<string,string>("campaign",match));}catch{}
            }
            return folders;
        }

        internal static string CharacterEditorPortraitCacheRoot()
        {
            string modulesRoot=RelationshipsService.FindRelationshipSimulationModulesRoot();
            string installed=string.IsNullOrWhiteSpace(modulesRoot)?"":Path.Combine(modulesRoot,"ReignBeta","PortraitCache");
            if(Directory.Exists(installed))return installed;
            DirectoryInfo app=new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);string module=app.Parent?.Parent?.FullName??"";string fallback=string.IsNullOrWhiteSpace(module)?"":Path.Combine(module,"PortraitCache");return Directory.Exists(fallback)?fallback:"";
        }

        internal static string ResolveCharacterEditorPortraitPath(string campaign,string hero,string variant)
        {
            string serverDir=CampaignFileService.CharacterFile(campaign,hero,"portraits");variant=CampaignFileService.SafePathSegment(variant,"active").ToLowerInvariant();List<KeyValuePair<string,string>> locations=CharacterEditorGameCacheFolders(campaign,hero);locations.Add(new KeyValuePair<string,string>("server",serverDir));
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
            List<KeyValuePair<string,string>> locations=CharacterEditorGameCacheFolders(campaign,hero);locations.Add(new KeyValuePair<string,string>("server",CampaignFileService.CharacterFile(campaign,hero,"portraits")));return CharacterEditorPortraitInventoryFromLocations(campaign,hero,locations);
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

        internal static Dictionary<string, object> CharacterEditorPortraitUploadApi(Dictionary<string, object> payload){string campaign=ReadString(payload,"campaignId",SettingsService.LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),data=ReadString(payload,"dataUrl",""),name=CampaignFileService.SafePathSegment(ReadString(payload,"fileName","custom.png"),"custom.png");int comma=data.IndexOf(',');if(comma>=0)data=data.Substring(comma+1);byte[] bytes;try{bytes=Convert.FromBase64String(data);}catch{return new Dictionary<string, object>{{"ok",false},{"error","The uploaded image was not valid."}};}if(bytes.Length>15*1024*1024)return new Dictionary<string, object>{{"ok",false},{"error","Portrait must be 15 MB or smaller."}};string version="custom_"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)+".png",path=CampaignFileService.CharacterFile(campaign,hero,"portraits",version);lock(Program.FileLock){Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,bytes);}return new Dictionary<string, object>{{"ok",true},{"variant",Path.GetFileNameWithoutExtension(version)},{"portraits",CharacterEditorPortraitInventory(campaign,hero)}};}
        internal static Dictionary<string, object> CharacterEditorPortraitSelectApi(Dictionary<string, object> payload){string campaign=ReadString(payload,"campaignId",SettingsService.LatestCampaignId()),hero=ReadFirstString(payload,"heroStringId","heroId"),variant=CampaignFileService.SafePathSegment(ReadString(payload,"variant",""),"");string source=ResolveCharacterEditorPortraitPath(campaign,hero,variant),active=CampaignFileService.CharacterFile(campaign,hero,"portraits","portrait.png");if(!File.Exists(source))return new Dictionary<string, object>{{"ok",false},{"error","Portrait variant was not found."}};lock(Program.FileLock){if(File.Exists(active)&&!string.Equals(Path.GetFullPath(active),Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase))File.Copy(active,CampaignFileService.CharacterFile(campaign,hero,"portraits","active_"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+".png"),true);if(!string.Equals(Path.GetFullPath(active),Path.GetFullPath(source),StringComparison.OrdinalIgnoreCase)){Directory.CreateDirectory(Path.GetDirectoryName(active));File.Copy(source,active,true);}}CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,hero,"portraits","portrait.json"),new Dictionary<string, object>{{"campaignId",campaign},{"heroStringId",hero},{"activeVariant",variant},{"source",source.IndexOf("PortraitCache",StringComparison.OrdinalIgnoreCase)>=0?"game_cache":"server"},{"updatedUtc",DateTime.UtcNow.ToString("o")}});return new Dictionary<string, object>{{"ok",true},{"activeUrl","/character-editor/portrait?campaignId="+Uri.EscapeDataString(campaign)+"&heroStringId="+Uri.EscapeDataString(hero)+"&variant=active"}};}

        private static string FinanceSnapshotIndexPath(string campaignId)
        {
            return Path.Combine(CampaignFileService.CampaignDirectory(campaignId), "finance", "character-finance-index.json");
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
            CampaignFileService.WriteJsonObject(FinanceSnapshotIndexPath(campaignId), index);
            return new Dictionary<string, object>
            {
                ["captured"] = true,
                ["characterCount"] = characters.Count,
                ["clanCount"] = clans.Count,
                ["observedWorldDay"] = worldDay,
                ["storageMode"] = "single_index_atomic_replace"
            };
        }

        internal static Dictionary<string, object> NormalizeObservedWealth(Dictionary<string, object> wealth)
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

        internal static bool IsObservedWealth(Dictionary<string, object> wealth)
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
            Dictionary<string, object> index = CampaignFileService.ReadJsonObject(FinanceSnapshotIndexPath(campaignId));
            Dictionary<string, object> characters = ReadDictionary(index, "characters");
            if (characters == null || !characters.TryGetValue(heroId ?? "", out object value))
                return new Dictionary<string, object>();
            Dictionary<string, object> snapshot = value as Dictionary<string, object>;
            return snapshot == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(snapshot, StringComparer.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> ResolveWealthDocument(
            string campaignId,
            string heroId,
            Dictionary<string, object> profile)
        {
            Dictionary<string, object> stored = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, heroId, "wealth.json"));
            Dictionary<string, object> indexed = FinanceSnapshotForHero(campaignId, heroId);
            bool storedObserved = IsObservedWealth(stored);
            bool indexedObserved = IsObservedWealth(indexed);
            if (indexedObserved && (!storedObserved
                || ReadDouble(indexed, "observedWorldDay", 0d) >= ReadDouble(stored, "observedWorldDay", 0d)))
                return indexed;
            if (storedObserved) return stored;
            return UnknownWealth(profile);
        }

        internal static Dictionary<string, object> UnknownWealth(Dictionary<string, object> hero)
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
                ["clanSocialCredit"] = DialogueService.ClanSocialCredit(0, clanTier, clanRenown, clanFiefCount, isLord),
                ["canLeanOnClanReputation"] = clanTier >= 3 || clanRenown >= 200d || clanFiefCount > 0 || isLord,
                ["economicCapacityKnown"] = false,
                ["wealthEvidenceBasis"] = "not_captured",
                ["goldSemantics"] = "unknown",
                ["notes"] = "No authoritative finance snapshot has been captured from the loaded game. Unknown values are not zero and must not be interpreted as poverty."
            };
        }

        internal static List<Dictionary<string, object>> RunCharacterEditorHistoryProjectionSelfTests()
        {
            var checks = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, string summary) => checks.Add(new Dictionary<string, object>
                { ["id"] = "character_editor_" + id, ["suite"] = "character_editor", ["passed"] = passed, ["summary"] = summary });
            string campaign = "ce_history_" + Guid.NewGuid().ToString("N");
            const string hero = "editor_a", other = "editor_b", listener = "listener", player = "player";
            const long sameSecond = 120;
            try
            {
                string sharedPlayerTurn = "";
                Dictionary<string, object> Seed(string eventId, string speaker, string timeline, bool shared)
                {
                    var audience = shared ? new List<string> { speaker, player, listener } : new List<string> { speaker, player };
                    var context = new Dictionary<string, object>
                    {
                        ["sceneTurnId"] = "beat_" + eventId, ["timelineId"] = timeline,
                        ["worldDay"] = 5d, ["participants"] = audience
                    };
                    // The ordinary writer starts a canonical session when no session ID is supplied.
                    if (shared) context["conversationSessionId"] = "session_" + eventId;
                    string text = "Meet beside the orchard.";
                    var exchange = shared
                        ? DialogueService.StorePartyConversationExchange(campaign, context, speaker, player, "Player", speaker, "Where?", text, eventId, sameSecond)
                        : DialogueService.StoreConversationExchange(campaign, context, speaker, player, "Player", speaker, "Where?", text, eventId, sameSecond);
                    var turnIds = ReadStringList(exchange, "turnIds");
                    if (eventId == "source_shared") sharedPlayerTurn = turnIds[0];
                    DialogueService.StoreTurnMemoryLayers(campaign, eventId, shared ? "party_chat_turn" : "dialogue_turn", speaker, player,
                        "town", "Where?", text, new List<Dictionary<string, object>> { new Dictionary<string, object>
                            { ["memoryId"] = "result_" + eventId, ["text"] = text, ["evidenceQuote"] = text } },
                        null, null, null, null, "conversation", sameSecond, audience, continuityPayload: context);
                    return new Dictionary<string, object>
                    {
                        ["id"] = "row_" + eventId, ["sourceTurnId"] = turnIds[1],
                        ["sessionId"] = ReadString(exchange, "sessionId", ""), ["exchangeId"] = ReadString(exchange, "exchangeId", ""),
                        ["eventId"] = "displayed_event_" + eventId,
                        ["role"] = "npc", ["speakerHeroStringId"] = speaker, ["ts"] = sameSecond, ["text"] = text
                    };
                }

                var shared = Seed("source_shared", hero, "main", true);
                var sharedPlayer = new Dictionary<string, object>(shared)
                {
                    ["id"] = "row_shared_player", ["role"] = "player", ["speakerHeroStringId"] = player,
                    ["sourceTurnId"] = sharedPlayerTurn, ["text"] = "Where?"
                };
                var separateHero = Seed("source_other_hero", other, "main", false);
                var otherEvent = Seed("source_same_hero", hero, "main", false);
                var olderExact = Seed("source_older_exact", hero, "main", false);
                olderExact.Remove("sourceTurnId");
                var wrongBranch = Seed("source_other_branch", hero, "older_branch", false);
                var publicLine = Seed("source_public_with_whisper", hero, "main", true);
                var legacy = new Dictionary<string, object>
                    { ["id"] = "legacy_row", ["ts"] = sameSecond, ["eventId"] = "source_shared", ["role"] = "npc", ["text"] = "Legacy line." };

                string aEvents = CampaignFileService.CharacterFile(campaign, hero, "history", "events.jsonl");
                string aDialogue = CampaignFileService.CharacterFile(campaign, hero, "history", "dialogue.jsonl");
                string bHistory = CampaignFileService.CharacterFile(campaign, other, "history", "dialogue.jsonl");
                string malformed = "{ignored legacy bytes}  \r\n";
                string spacedOther = "  " + Json.Serialize(otherEvent) + "  \r\n";
                string spacedLegacy = "  " + Json.Serialize(legacy) + "    \n";
                Directory.CreateDirectory(Path.GetDirectoryName(aEvents));
                Directory.CreateDirectory(Path.GetDirectoryName(bHistory));
                File.WriteAllText(aEvents, Json.Serialize(sharedPlayer) + "\n" + Json.Serialize(shared)
                    + "\n" + malformed + spacedOther + spacedLegacy + Json.Serialize(separateHero));
                string guardedDialogue = Json.Serialize(wrongBranch) + "\r\n  " + Json.Serialize(publicLine);
                File.WriteAllText(aDialogue, Json.Serialize(olderExact) + "\r\n" + guardedDialogue);
                File.WriteAllText(bHistory, "  " + Json.Serialize(separateHero) + "\r\n", new UTF8Encoding(true));
                byte[] bOriginal = File.ReadAllBytes(bHistory);

                var whisper = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["sessionId"] = "private_whisper_session", ["npcId"] = hero,
                    ["playerId"] = player, ["channel"] = "private_whisper", ["timelineId"] = "main", ["ts"] = sameSecond
                };
                string whisperSession = ReadString(DialogueService.ConversationStartApi(whisper), "sessionId", "");
                if (whisperSession != "private_whisper_session")
                    throw new InvalidOperationException("The private-whisper fixture did not start its exact session.");
                using var c = ReignDatabase.OpenCampaignConnection(campaign);
                DialogueService.InsertConversationTurn(c, "private_whisper_npc", whisperSession, "source_public_with_whisper", 0,
                    "private_whisper_exchange", "npc", hero, hero, "Only you should hear this.", "private_whisper",
                    5d, sameSecond, "town", new List<string> { hero, player }, whisper);
                CharactersStore.UpdateDynamic(c, "conversation_turns", "status='superseded'", "turn_id",
                    new Dictionary<string, object> { ["id"] = "private_whisper_npc" });
                var canonical = new Dictionary<string, object> { ["id"] = "source_shared" };
                var turnKey = new Dictionary<string, object> { ["id"] = ReadString(shared, "sourceTurnId", "") };
                string Originals() => CanonicalJson(new object[]
                {
                    CharactersStore.SelectDynamic(c, "events", "event_id", canonical).Single(),
                    CharactersStore.SelectDynamic(c, "conversation_turns", "turn_id", turnKey).Single()
                });
                string originals = Originals();
                List<Dictionary<string, object>> Memories(string id) => CharactersStore.SelectEditorHistoryMemories(c,
                    new Dictionary<string, object> { ["event"] = id });
                string OtherMemories() => CanonicalJson(new[] { "source_other_hero", "source_same_hero", "source_other_branch", "source_public_with_whisper" }
                    .Select(id => Memories(id).OrderBy(row => ReadString(row, "memory_id", "")).ToList()));
                string unrelated = OtherMemories();
                bool IsSharedPlayerProjection(Dictionary<string, object> row) =>
                    ReadString(row, "source", "") == EditorHistoryProjectionSource
                    && ReadString(TryParseJsonObject(ReadString(row, "payload_json", "")), "sourceTurnId", "") == sharedPlayerTurn;

                var sameSource = Memories("source_shared").Single(row =>
                    ReadString(TryParseJsonObject(ReadString(row, "payload_json", "{}")), "observerScope", "") == "observable_source_spans");
                Check("shared_source_fixture", DialogueService.TextListFromJson(ReadString(sameSource, "known_by_json", "[]")).Contains(listener),
                    "The production conversation writer deliberately shares its exact observer projection.");

                Dictionary<string, object> HistoryEdit(Dictionary<string, object> load, string group, string id, string text = null)
                {
                    var basis = ReadDictionary(ReadDictionary(load, "historyEditBasis"), group);
                    return new Dictionary<string, object>
                    {
                        ["group"] = group, ["id"] = id, ["basis"] = ReadString(basis, id, ""),
                        [text == null ? "delete" : "text"] = text == null ? (object)true : text
                    };
                }

                var loaded = CharacterEditorLoadApi(new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = hero });
                var history = ReadDictionary(loaded, "history");
                var edits = ReadDictionaryList(history, "dialogue").Select(row => HistoryEdit(loaded, "dialogue", EditorHistoryRowId(row), "Meet beside the vineyard."))
                    .Concat(ReadDictionaryList(history, "events").Where(row =>
                        EditorHistoryRowId(row) != "row_source_same_hero" && EditorHistoryRowId(row) != "row_shared_player")
                        .Select(row => HistoryEdit(loaded, "events", EditorHistoryRowId(row), "Meet beside the vineyard."))).ToList();
                var request = new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["heroStringId"] = hero, ["section"] = "history",
                    ["revisionToken"] = ReadString(loaded, "revisionToken", ""), ["historyEdits"] = edits, ["records"] = ReadDictionary(loaded, "records")
                };
                var saved = CharacterEditorSaveApi(request);
                var issues = ReadDictionaryList(saved, "historyProjectionIssues");
                var memory = Memories("source_shared");
                Check("history_exact_shared_rebuild", ReadBool(saved, "ok", false)
                    && memory.Any(row => ReadString(row, "memory_id", "") == ReadString(sameSource, "memory_id", "")
                        && ReadString(row, "status", "") == "superseded")
                    && memory.Any(row => ReadString(row, "memory_id", "") == "result_source_shared" && ReadString(row, "status", "") == "superseded")
                    && memory.Any(row => ReadString(row, "owner_id", "") == listener && ReadString(row, "source", "") == EditorHistoryProjectionSource
                        && ReadString(row, "status", "") == "active" && ReadString(row, "summary", "").Contains("vineyard"))
                    && !memory.Any(row => ReadString(row, "owner_id", "") == other && ReadString(row, "source", "") == EditorHistoryProjectionSource),
                    "Only proven observers of the shared accepted source get corrected memory.");
                Check("history_shared_other_side_retained",
                    memory.Count(row => IsSharedPlayerProjection(row) && ReadString(row, "status", "") == "active"
                        && ReadString(row, "owner_id", "") == listener && ReadString(row, "summary", "").Contains("Where?")) == 1,
                    "Editing the NPC line carries the unchanged player line to the existing listener exactly once.");
                Check("history_unrelated_same_second", OtherMemories() == unrelated && Originals() == originals
                    && File.ReadAllBytes(bHistory).SequenceEqual(bOriginal)
                    && File.ReadAllText(aEvents).EndsWith(malformed + spacedOther + spacedLegacy + Json.Serialize(separateHero), StringComparison.Ordinal),
                    "Same-second memories, canonical event and turn originals, other-hero files and opaque or unmatched JSONL bytes are preserved.");
                var issueIds = issues.Select(row => ReadString(row, "rowId", "")).ToHashSet(StringComparer.Ordinal);
                Check("history_legacy_and_timeline_diagnostics",
                    issueIds.SetEquals(new[] { "legacy_row", "row_source_other_hero", "row_source_other_branch", "row_source_public_with_whisper" })
                    && File.ReadAllText(aDialogue).EndsWith(guardedDialogue, StringComparison.Ordinal),
                    "Legacy event IDs, wrong owners or branches, and public/private ambiguity are reported and preserved. Skipped rows "
                    + string.Join(", ", issueIds.OrderBy(id => id, StringComparer.Ordinal)) + ".");
                Check("history_legacy_exact_exchange",
                    Memories("source_older_exact").Any(row => ReadString(row, "source", "") == EditorHistoryProjectionSource
                        && ReadString(row, "summary", "").Contains("vineyard")),
                    "An older ordinary history row resolves through its exact persisted session, exchange and role.");

                var current = CharacterEditorLoadApi(new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = hero });
                request["historyEdits"] = new List<Dictionary<string, object>>
                {
                    HistoryEdit(current, "events", "row_source_shared"),
                    HistoryEdit(current, "events", "row_shared_player", "Which grove?")
                };
                request["records"] = ReadDictionary(current, "records");
                request["revisionToken"] = ReadString(current, "revisionToken", "");
                var deleted = CharacterEditorSaveApi(request);
                var afterDelete = Memories("source_shared").Where(row => ReadString(row, "status", "") == "active").ToList();
                Check("history_exact_delete", ReadBool(deleted, "ok", false) && ReadDictionaryList(deleted, "historyProjectionIssues").Count == 0
                    && afterDelete.Any(row => ReadString(row, "owner_id", "") == listener)
                    && afterDelete.All(row => IsSharedPlayerProjection(row) && ReadString(row, "summary", "").Contains("Which grove?"))
                    && OtherMemories() == unrelated && Originals() == originals,
                    "Deleting the NPC line retains the separately edited player line and the canonical originals.");

                var rollback = CharacterEditorRollbackApi(new Dictionary<string, object>
                    { ["campaignId"] = campaign, ["heroStringId"] = hero, ["revisionId"] = ReadString(deleted, "revisionId", "") });
                var listenerAfterRollback = Memories("source_shared").Where(row =>
                    ReadString(row, "status", "") == "active" && ReadString(row, "owner_id", "") == listener).ToList();
                Check("history_exact_rollback", ReadBool(rollback, "ok", false) && ReadDictionaryList(rollback, "historyProjectionIssues").Count == 0
                    && listenerAfterRollback.Count == 2 && listenerAfterRollback.Any(row => ReadString(row, "summary", "").Contains("vineyard"))
                    && listenerAfterRollback.Any(row => IsSharedPlayerProjection(row) && ReadString(row, "summary", "").Contains("Where?"))
                    && OtherMemories() == unrelated && Originals() == originals,
                    "Rollback restores both exact lines once for the original observer and preserves unrelated history.");
                var redo = CharacterEditorRollbackApi(new Dictionary<string, object>
                    { ["campaignId"] = campaign, ["heroStringId"] = hero, ["revisionId"] = ReadString(rollback, "revisionId", "") });
                Check("history_exact_redo", ReadBool(redo, "ok", false) && ReadDictionaryList(redo, "historyProjectionIssues").Count == 0
                    && Memories("source_shared").Any(row => IsSharedPlayerProjection(row) && ReadString(row, "status", "") == "active"
                        && ReadString(row, "summary", "").Contains("Which grove?"))
                    && !ReadDictionaryList(ReadEditorHistory(Path.GetDirectoryName(aEvents)), "events").Any(row => EditorHistoryRowId(row) == "row_source_shared"),
                    "Undoing a rollback reapplies exactly the history changes it made.");

                string decisions = CampaignFileService.CharacterFile(campaign, hero, "history", "decisions.jsonl");
                void AddDecision(string id, string text) => CampaignFileService.AppendJsonLineToPath(decisions,
                    new Dictionary<string, object> { ["id"] = id, ["text"] = text });
                AddDecision("edit_target", "old edit");
                AddDecision("delete_target", "old deletion");
                AddDecision("removed_target", "old removal");
                string profilePath = CampaignFileService.CharacterFile(campaign, hero, "profile.json");
                CampaignFileService.WriteJsonObject(profilePath, new Dictionary<string, object> { ["heroStringId"] = hero, ["name"] = "Editor Fixture" });
                var profileLoad = CharacterEditorLoadApi(new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = hero });
                AddDecision("late", "new dialogue decision");
                byte[] beforeProfileSave = File.ReadAllBytes(decisions);
                var newProfile = new Dictionary<string, object> { ["description"] = "Fixture profile change" };
                var profileSaved = CharacterEditorSaveApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["heroStringId"] = hero, ["section"] = "profile",
                    ["revisionToken"] = ReadString(profileLoad, "revisionToken", ""),
                    ["profileEdit"] = new Dictionary<string, object> { ["basis"] = ReadString(profileLoad, "profileEditBasis", ""), ["fields"] = newProfile },
                    ["nativeChanges"] = newProfile
                });
                Check("history_profile_only_preserves_append", ReadBool(profileSaved, "ok", false)
                    && ReadString(CampaignFileService.ReadJsonObject(profilePath), "description", "") == "Fixture profile change"
                    && File.ReadAllBytes(decisions).SequenceEqual(beforeProfileSave),
                    "Saving a profile draft after dialogue appends a decision preserves the exact history bytes.");

                var stale = CharacterEditorLoadApi(new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = hero });
                var staleEdits = new List<Dictionary<string, object>>
                {
                    HistoryEdit(stale, "decisions", "edit_target", "stale edit"),
                    HistoryEdit(stale, "decisions", "delete_target"),
                    HistoryEdit(stale, "decisions", "removed_target", "stale resurrection")
                };
                File.WriteAllText(decisions, File.ReadAllText(decisions).Replace("old edit", "live edit", StringComparison.Ordinal)
                    .Replace("old deletion", "live deletion target", StringComparison.Ordinal));
                File.WriteAllLines(decisions, File.ReadAllLines(decisions).Where(line => !line.Contains("removed_target", StringComparison.Ordinal)));
                byte[] beforeStaleAttempt = File.ReadAllBytes(decisions);
                var staleResult = CharacterEditorSaveApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaign, ["heroStringId"] = hero, ["section"] = "history",
                    ["revisionToken"] = ReadString(stale, "revisionToken", ""), ["historyEdits"] = staleEdits
                });
                var staleIssues = ReadDictionaryList(staleResult, "historyProjectionIssues");
                Check("history_rejects_stale_edit_delete_and_restore", ReadBool(staleResult, "ok", false)
                    && staleIssues.Count == 3 && staleIssues.All(row => ReadString(row, "reason", "") == "history_row_changed_after_load")
                    && File.ReadAllBytes(decisions).SequenceEqual(beforeStaleAttempt),
                    "Changed edit and delete targets, and a missing edit target, reject stale preimages without altering bytes.");

                var revisionBefore = BuildCharacterEditorSnapshot(campaign, hero, c);
                AddDecision("between_snapshots", "unrelated append");
                var revisionAfter = BuildCharacterEditorSnapshot(campaign, hero, c);
                var emptyHistoryDelta = new EditorHistoryDelta();
                revisionBefore[EditorHistoryDeltaKey] = emptyHistoryDelta.Before;
                revisionAfter[EditorHistoryDeltaKey] = emptyHistoryDelta.After;
                const string unrelatedRevision = "other_resource_with_history_append";
                CharactersStore.InsertCharacterEditorRevisions(c, new Dictionary<string, object>
                {
                    ["revision"] = unrelatedRevision, ["hero"] = hero, ["section"] = "profile", ["description"] = "Fixture with a concurrent append",
                    ["before"] = Json.Serialize(revisionBefore), ["after"] = Json.Serialize(revisionAfter), ["native"] = "{}", ["reconcile"] = "{}",
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                var unrelatedUndo = CharacterEditorRollbackApi(new Dictionary<string, object>
                    { ["campaignId"] = campaign, ["heroStringId"] = hero, ["revisionId"] = unrelatedRevision });
                var unrelatedRedo = CharacterEditorRollbackApi(new Dictionary<string, object>
                    { ["campaignId"] = campaign, ["heroStringId"] = hero, ["revisionId"] = ReadString(unrelatedUndo, "revisionId", "") });
                Check("history_empty_revision_preserves_concurrent_append", ReadBool(unrelatedUndo, "ok", false)
                    && ReadBool(unrelatedRedo, "ok", false) && ReadDictionaryList(ReadEditorHistory(Path.GetDirectoryName(decisions)), "decisions")
                        .Any(row => EditorHistoryRowId(row) == "between_snapshots"),
                    "Undo and redo use the empty history delta even when the full snapshots differ because of live history.");
            }
            catch (Exception ex)
            {
                Check("history_projection_fixture", false, ex.ToString());
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
            return checks;
        }

        internal static List<Dictionary<string, object>> RunCharacterEditorSubsystemSelfTests()
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
            add("document_contract",EditorDocumentNames.Length==17&&EditorDocumentNames.Contains("traits")&&EditorDocumentNames.Contains("hidden_history"),"The editor covers all character construction and runtime documents.");
            rows.AddRange(RunCharacterEditorHistoryProjectionSelfTests());
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
                PersistenceService.TryDeleteDirectory(portraitFixture);
            }
            string campaign="ce_"+Guid.NewGuid().ToString("N").Substring(0,12);
            try
            {
                Dictionary<string,Dictionary<string,object>> shippedProfiles=LoadCharacterProfileLibrary();
                KeyValuePair<string,Dictionary<string,object>> shippedEntry=shippedProfiles.FirstOrDefault(pair=>ReadString(pair.Value,"source","").Equals("native_sandbox",StringComparison.OrdinalIgnoreCase));
                string shippedHeroId=shippedEntry.Key??"";
                if(!string.IsNullOrWhiteSpace(shippedHeroId))
                {
                    Directory.CreateDirectory(CampaignFileService.CharacterFile(campaign,shippedHeroId,"memory"));
                    CampaignFileService.EnsureFile(CampaignFileService.CharacterFile(campaign,shippedHeroId,"memory","memories.jsonl"));
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
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,"editor_a","profile.json"),heroA);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,"editor_b","profile.json"),heroB);
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,"editor_a","wealth.json"),UnknownWealth(heroA));
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
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,"main_hero","profile.json"),new Dictionary<string,object>{{"heroStringId","main_hero"},{"name","Player"}});
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,"editor_a","traits.json"),new Dictionary<string,object>{{"foundationTraits",new Dictionary<string,object>{{"assertiveness",2},{"sociability",1},{"pragmatism",1}}}});
                CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign,"editor_b","traits.json"),new Dictionary<string,object>{{"foundationTraits",new Dictionary<string,object>{{"empathy",1},{"patience",1},{"loyalty",2}}}});
                using(ReignDbConnection connection=ReignDatabase.OpenCampaignConnection(campaign))
                {
                    RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                    long ts=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    string pair=RelationshipsService.AmbientPairKey("editor_a","editor_b");
                    CharactersStore.InsertRelationshipPairChemistry(connection, new Dictionary<string,object>{{"pair",pair},{"ts",ts}});
                    CharactersStore.InsertRelationshipPairLifecycle(connection, new Dictionary<string,object>{{"pair",pair},{"ts",ts}});
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
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
            return rows;
        }
    }
}
