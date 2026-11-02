using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunChatViewContractTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, Action action)
            {
                try { action(); results.Add(TestDict("caseId", "chat_view_" + id, "passed", true, "summary", id)); }
                catch (Exception ex) { results.Add(TestDict("caseId", "chat_view_" + id, "passed", false, "summary", ex.Message)); }
            }
            void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
            bool Reject(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } catch (InvalidOperationException) { return true; } }
            RunChatViewAppearanceChecks(Check, Require);
            RunChatViewWardrobeChecks(Check, Require);
            var frame = TestDict("actors", new[] { TestDict("id", "player", "name", "Player"), TestDict("id", "ira", "name", "Ira"), TestDict("id", "rhagaea", "name", "Rhagaea") },
                "expectedNpcIds", new[] { "ira", "rhagaea" }, "scene", "A quiet camp at dusk.");
            Dictionary<string, object> Turn(string id, string role, string speaker, string text, string exchange = "exchange_4") =>
                TestDict("turn_id", id, "exchange_id", exchange, "role", role, "speaker_id", speaker, "speaker_name", speaker,
                    "text", text, "frame_json", Json.Serialize(frame));
            var turns = new List<Dictionary<string, object>> { Turn("p4", "player", "player", "I offer her the sealed letter."),
                Turn("n4i", "npc", "ira", "Ira takes the letter with both hands and smiles."),
                Turn("n4r", "npc", "rhagaea", "Rhagaea watches the exchange quietly.") };
            var window = ChatViewCompleteExchange(turns, "player");
            Dictionary<string, object> Scene() => TestDict("selectedTurnId", "n4i", "characterIds", new[] { "player", "ira", "rhagaea" },
                "caption", "A letter changes hands at dusk.", "description", "At dusk in camp, Ira receives the player's sealed letter with both hands while Rhagaea watches.",
                "evidence", new[] { TestDict("turnId", "n4i", "quote", "Ira takes the letter with both hands and smiles.") });
            Check("complete_exchange_retains_every_reply", () => Require(window.Count == 3 && ReadString(window[2], "speakerId", "") == "rhagaea", "A group reply was omitted."));
            Check("unfinished_group_rejected", () => Require(ChatViewCompleteExchange(turns.Take(2).ToList(), "player").Count == 0, "A partial group exchange was accepted."));
            Check("opening_without_player_exchange_rejected", () => Require(ChatViewCompleteExchange(turns.Skip(1).ToList(), "player").Count == 0, "An NPC opening was counted as a player exchange."));
            Check("missing_snapshot_rejected", () => { var copy = turns.Select(CloneDictionary).ToList(); copy[0]["frame_json"] = "{}"; Require(ChatViewCompleteExchange(copy, "player").Count == 0, "Current roster substituted for missing historical presence."); });
            Check("valid_source_locked_instant", () => ValidateChatViewScene(Scene(), window));
            Check("absent_character_rejected", () => { var scene = Scene(); scene["characterIds"] = new[] { "ira", "absent" }; Require(Reject(() => ValidateChatViewScene(scene, window)), "Absent actor reached image stage."); });
            Check("duplicate_character_rejected", () => { var scene = Scene(); scene["characterIds"] = new[] { "ira", "ira" }; Require(Reject(() => ValidateChatViewScene(scene, window)), "Identity duplicated."); });
            Check("invented_quote_rejected", () => { var scene = Scene(); scene["evidence"] = new[] { TestDict("turnId", "n4i", "quote", "Ira draws a sword.") }; Require(Reject(() => ValidateChatViewScene(scene, window)), "Invented action quote accepted."); });
            Check("other_exchange_cannot_supply_action", () => { var other = CloneDictionary(window[0]); other["turnId"] = "old"; other["exchangeId"] = "exchange_1"; var scene = Scene(); scene["evidence"] = new[] { TestDict("turnId", "old", "quote", "sealed letter") }; Require(Reject(() => ValidateChatViewScene(scene, window.Concat(new[] { other }).ToList())), "Different instants were merged."); });
            Check("selected_turn_must_be_in_window", () => { var scene = Scene(); scene["selectedTurnId"] = "outside"; Require(Reject(() => ValidateChatViewScene(scene, window)), "Outside moment accepted."); });
            Check("selected_moment_needs_direct_evidence", () => { var scene = Scene(); scene["evidence"] = new[] { TestDict("turnId", "p4", "quote", "sealed letter") }; Require(Reject(() => ValidateChatViewScene(scene, window)), "Selected instant had no direct evidence."); });
            Check("caption_and_prompt_bounds", () => { var scene = Scene(); scene["description"] = new string('x', 6001); Require(Reject(() => ValidateChatViewScene(scene, window)), "Unbounded image brief accepted."); });
            foreach (var item in new[] {
                ("AtlasCloud", AtlasModelSeedreamEdit, "4992x3328", ""),
                ("AtlasCloud", AtlasModelSeedreamV5ProEdit, "2496x1664", ""),
                ("AtlasCloud", AtlasModelWan25ImageEdit, "1560x1040", ""),
                ("AtlasCloud", AtlasModelGrokImagineImageEdit, "2496x1664", "2k"),
                ("OpenRouter", "google/gemini-3-pro-image-preview", "5056x3392", "4K"),
                ("OpenRouter", "bytedance-seed/seedream-4.5", "4992x3328", "4K"),
                ("OpenRouter", "bytedance-seed/seedream-5-0-pro", "2496x1664", "2K"),
                ("NanoGPT", "gpt-image-1.5", "1536x1024", "") })
            {
                Check("maximum_" + item.Item2.Replace('/', '_'), () => {
                    var profile = new ImageGenerationProfile { Provider = item.Item1, AtlasModel = item.Item2, OpenRouterModel = item.Item2, NanoGptModel = item.Item2 };
                    var cap = ChatViewImageCapability(profile);
                    Require(ReadString(cap, "size", "") == item.Item3 && ReadString(cap, "resolution", "") == item.Item4, "Wrong selected-model maximum.");
                    if (item.Item1 == "AtlasCloud") { var body = TestDict("aspect_ratio", "auto", "resolution", "1k"); ApplyChatViewAtlasSize(body, profile); Require(item.Item4.Length > 0 ? ReadString(body, "aspect_ratio", "") == "3:2" && ReadString(body, "resolution", "") == "2k" : ReadString(body, "size", "") == item.Item3.Replace('x', '*'), "Atlas payload retained a legacy ratio or cap."); }
                    if (item.Item1 == "OpenRouter") { var body = BuildOpenRouterImagePayload(item.Item2, "scene", new byte[] { 1 }, item.Item3, true); Require(ReadString(body, "aspect_ratio", "") == "3:2" && ReadString(body, "resolution", "") == item.Item4, "OpenRouter View kept 16:9 or a 2K cap."); }
                });
            }
            Check("unknown_capability_fails_before_provider", () => Require(Reject(() => ChatViewImageCapability(new ImageGenerationProfile { Provider = "NanoGPT", NanoGptModel = "unknown" })), "Unknown model guessed a maximum."));
            Check("runtime_managed_maximum_is_not_an_api_guarantee", () => Require(!ReadBool(ChatViewImageCapability(new ImageGenerationProfile { Provider = "Codex" }), "maximumVerified", true)
                && CodexImageFraming("maximum-native-3:2").Contains("highest native"), "Runtime maximum was falsely certified."));
            Check("exact_image_retains_source_bytes", () => { byte[] png = ChatViewTestPng(384, 256, 33); var output = NormalizeChatViewImage(png, TestDict("size", "384x256"), out _, out _); Require(ReferenceEquals(png, output), "Exact 3:2 was resampled."); });
            Check("jpeg_result_converted_without_resize", () => {
                byte[] png = NormalizeChatViewImage(LinuxImageCodec.Fixture(384, 256, true), TestDict("size", "384x256"), out int sw, out int sh);
                Require(PngReencode.DecodeToRgba(png, out int w, out int h) != null && sw == 384 && sh == 256 && w == sw && h == sh, "A provider JPEG was rejected or resized.");
            });
            Check("nominal_ratio_rounding_trims_without_resize", () => { byte[] output = NormalizeChatViewImage(ChatViewTestPng(384, 258, 77), TestDict("size", "384x258"), out int sw, out int sh); var rgba = PngReencode.DecodeToRgba(output, out int w, out int h); Require(sw == 384 && sh == 258 && w == 384 && h == 256 && rgba[0] == 77, "Rounded bucket was stretched or pixels changed."); });
            Check("wrong_aspect_refuses_destructive_crop", () => Require(Reject(() => NormalizeChatViewImage(ChatViewTestPng(384, 216, 11), TestDict("size", "384x216"), out _, out _)), "16:9 was silently cropped."));
            Check("lower_resolution_not_upscaled", () => Require(Reject(() => NormalizeChatViewImage(ChatViewTestPng(384, 256, 11), TestDict("size", "1536x1024"), out _, out _)), "Small output passed as maximum."));
            Check("decoded_pixel_bomb_rejected", () => { byte[] png = ChatViewTestPng(3, 2, 1); png[16] = 127; Require(Reject(() => NormalizeChatViewImage(png, TestDict(), out _, out _)), "Oversized PNG reached decode."); });
            Check("full_cast_reference_cells_beyond_four", () => {
                var sources = Enumerable.Range(0, 5).Select(i => ChatViewTestPng(256, 384, (byte)(30 + i * 20))).ToList();
                byte[] rgba = PngReencode.DecodeToRgba(ComposeChatViewReferences(sources), out int w, out int h);
                Require(w == 3072 && h == 2048 && rgba[(1536 * w + 384) * 4] == 110, "The fifth identity was lost or merged.");
            });
            Check("invalid_reference_never_omitted", () => Require(Reject(() => ComposeChatViewReferences(new[] { new byte[] { 1, 2 } })), "Invalid reference silently disappeared."));
            Check("prompt_contract_distinguishes_enactment_and_style", () => Require(ChatViewSelectionPrompt.Contains("four complete exchanges") && ChatViewSelectionPrompt.Contains("corrections/retractions")
                && ChatViewSelectionPrompt.Contains("exact supporting substring") && ChatViewPhotoPrompt.Contains("documentary photograph") && !ChatViewPhotoPrompt.Contains("explicit"), "Prompt grounding/schema or photo layer drifted."));
            Check("transcript_guard_rejects_gray_speech_backing", () => {
                var document = new System.Xml.XmlDocument();
                document.LoadXml("<ListPanel><Widget Sprite='BlankWhiteSquare' Color='#171717FF'/></ListPanel>");
                Require(!VerificationTranscriptBackingsValid(document.DocumentElement), "A speech backing escaped the transparent transcript guard.");
            });
            Check("transcript_guard_rejects_unbounded_scene_owner", () => {
                var document = new System.Xml.XmlDocument();
                document.LoadXml("<ListPanel><Widget Id='ChatViewImageEntry' IsVisible='@IsViewLine' SuggestedWidth='1672'><Children><Widget Sprite='BlankWhiteSquare' Color='#070808FF'/></Children></Widget></ListPanel>");
                Require(!VerificationTranscriptBackingsValid(document.DocumentElement), "A full-width backing disguised as a scene card passed.");
            });
            RunChatViewPersistenceTests(results);
            return results;
        }

        private static void RunChatViewPersistenceTests(List<Dictionary<string, object>> results)
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("View persistence contracts require isolated Verification Lab storage.");
            string campaign = "chat_view_" + Guid.NewGuid().ToString("N");
            void Check(string name, bool passed) => results.Add(TestDict("caseId", "chat_view_db_" + name, "passed", passed, "summary", name));
            try
            {
                using var c = OpenCampaignConnection(campaign);
                ReadMemoryPrecisionState(c);
                EnsureChatViewSchema(c);
                ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('view_session',$campaign,'npc0','player','open',1,1,'[""player"",""npc0""]');", TestDict("campaign", campaign));
                var ids = Enumerable.Range(0, 10).Select(i => "npc" + i).ToList();
                var people = ids.Concat(new[] { "player" }).Select(id => TestDict("heroStringId", id, "name", id)).ToList();
                var payload = TestDict("timelineId", "main", "sceneParticipants", people, "activeHeroIds", ids);
                var participants = ids.Concat(new[] { "player" }).ToList();
                for (int exchange = 1; exchange <= 6; exchange++)
                {
                    string e = "e" + exchange;
                    InsertConversationTurn(c, e + "_p", "view_session", e, exchange * 20, e, "player", "player", "Player", "I offer a letter.", "party_chat", 4, 100, "camp", participants, payload);
                    for (int i = 0; i < (exchange == 6 ? 9 : 10); i++)
                        InsertConversationTurn(c, e + "_n" + i, "view_session", e, exchange * 20 + i + 1, e, "npc", ids[i], ids[i], "They receive the letter and nod.", "party_chat", 4, 100, "camp", participants, payload);
                }
                var request = TestDict("campaignId", campaign, "sessionId", "view_session", "playerId", "player", "timelineId", "main", "worldDay", 5d);
                var window = ReadChatViewWindow(c, request);
                Check("four_complete_groups_beyond_ui_tail", window.Count == 44 && window.Select(t => ReadString(t, "exchangeId", "")).Distinct().SequenceEqual(new[] { "e2", "e3", "e4", "e5" }));
                request["throughExchangeId"] = "e3";
                Check("click_boundary_excludes_later_replies", ReadChatViewWindow(c, request).Count == 33);
                request["playerId"] = "foreign";
                bool rejected = false; try { ReadChatViewWindow(c, request); } catch (InvalidOperationException) { rejected = true; }
                Check("foreign_player_cannot_read_session", rejected);
                request["playerId"] = "player"; request["timelineId"] = "foreign_branch";
                rejected = false; try { ReadChatViewWindow(c, request); } catch (InvalidOperationException) { rejected = true; }
                Check("foreign_timeline_cannot_read_scene", rejected);
                request["timelineId"] = "main"; request["worldDay"] = 3d;
                rejected = false; try { ReadChatViewWindow(c, request); } catch (InvalidOperationException) { rejected = true; }
                Check("future_scene_hidden", rejected);

                var raw = ChatViewTestPng(384, 256, 71);
                var job = TestDict("jobId", new string('a', 64), "campaignId", campaign, "timelineId", "main", "sessionId", "view_session", "playerId", "player", "createdTs", 100L,
                    "status", "publishing", "originalSha256", Sha256Hex(raw), "identity", TestDict("capability", TestDict("size", "384x256"), "window", window));
                string target = ChatViewImagePath(campaign, "main", ReadString(job, "jobId", ""));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                WriteSharedPortraitAtomic(target + ".original.png", raw);
                SaveChatViewJob(c, job);
                var restored = ChatViewResponse(campaign, job, true);
                Check("publication_recovers_without_providers", ReadString(restored, "status", "") == "ready" && ReadString(restored, "imageSha256", "") == Sha256Hex(raw));
                ExecuteSql(c, "INSERT INTO chat_view_requests(request_id,session_id,timeline_id,player_id,job_id) VALUES($request,'view_session','main','player',$job);", TestDict("request", new string('b', 32), "job", ReadString(job, "jobId", "")));
                request["requestId"] = new string('b', 32); request["worldDay"] = 5d;
                Check("transport_retry_uses_admitted_job", ReadString(ChatViewApi(request, "start"), "jobId", "") == ReadString(job, "jobId", ""));
                Check("same_timeline_archive_visible", ChatViewJobVisible(c, job, request));
                request["timelineId"] = "unbound_branch";
                Check("foreign_timeline_archive_hidden", !ChatViewJobVisible(c, job, request));
                request["timelineId"] = "main";
                string pendingKey = campaign + "|pending_contract";
                ChatViewWorkers.TryAdd(pendingKey, new System.Threading.Tasks.TaskCompletionSource<bool>().Task);
                try { Check("save_sync_waits_for_pending_image", ReadString(SaveSyncRegisterCore(TestDict("campaignId", campaign, "savePointId", "pending")), "result", "") == "chat_view_pending"); }
                finally { ChatViewWorkers.TryRemove(pendingKey, out _); }
                job["status"] = "generating";
                Check("interrupted_provider_not_replayed", ReadString(ChatViewResponse(campaign, job, false), "status", "") == "uncertain");
                c.Dispose();
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "view_before");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "view_before");
                var point = TestDict("savePointId", "view_before", "timelineId", "main", "campaignTimeDays", 5d,
                    "worldHistorySequence", 1L, "worldHistoryHeadEventId", "view_head");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var recovered = OpenCampaignConnection(campaign))
                {
                    request["timelineId"] = "restored_view";
                    Check("restored_archive_requires_verified_binding", !ChatViewJobVisible(recovered, job, request));
                    BindRecoveredMemorySources(recovered, "main", "restored_view", 1, "view_head");
                    Check("verified_restore_preserves_memory_image", ChatViewJobVisible(recovered, job, request));
                    Check("verified_restore_preserves_complete_window", ReadChatViewWindow(recovered, request).Count == 33);
                }
            }
            catch (Exception ex) { results.Add(TestDict("caseId", "chat_view_db_fixture", "passed", false, "summary", ex.ToString())); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }

        private static void RunChatViewAppearanceChecks(Action<string, Action> check, Action<bool, string> require)
        {
            string root = Path.Combine(Path.GetTempPath(), "reign-view-reference-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(root)) throw new InvalidOperationException("Reference fixture root already exists.");
            string campaign = Path.Combine(root, "campaign"), shared = Path.Combine(root, "shared"), character = Path.Combine(root, "character");
            foreach (string folder in new[] { campaign, shared, character }) Directory.CreateDirectory(folder);
            void Put(string folder, string name) => File.WriteAllBytes(Path.Combine(folder, name), new byte[] { 1 });
            try
            {
                Put(shared, "portrait.png"); Put(character, "portrait.png");
                string Pick(bool player = false) => SelectChatViewPortraitPath(new[] { campaign }, new[] { shared }, character, player);
                check("shared_visible_master_beats_stale_character_record", () => require(Pick() == Path.Combine(shared, "portrait.png"), "The hidden character-record portrait replaced chat's shared master."));
                check("player_never_uses_another_campaign_shared_identity", () => require(Pick(true) == Path.Combine(character, "portrait.png"), "A shared main_hero image crossed campaign identities."));
                check("unavailable_shared_library_does_not_guess_an_older_character_portrait", () => {
                    bool rejected = false;
                    try { SelectChatViewPortraitPath(new[] { campaign }, Array.Empty<string>(), character, false, false); }
                    catch (InvalidOperationException) { rejected = true; }
                    require(rejected, "An unavailable UI library silently selected an older character record.");
                });
                Put(shared, "custom.png");
                check("shared_custom_beats_shared_generated", () => require(Pick() == Path.Combine(shared, "custom.png"), "The shared custom master was ignored."));
                Put(campaign, "portrait.png");
                check("campaign_master_beats_shared_and_character", () => require(Pick() == Path.Combine(campaign, "portrait.png"), "The current campaign portrait was ignored."));
                Put(campaign, "custom.png");
                check("campaign_custom_is_visible_authority", () => require(Pick() == Path.Combine(campaign, "custom.png"), "The campaign custom portrait was ignored."));
                foreach (string file in Directory.GetFiles(campaign).Concat(Directory.GetFiles(shared))) File.Delete(file);
                Put(character, "custom.png");
                check("character_custom_is_last_fallback", () => require(Pick() == Path.Combine(character, "custom.png"), "Character fallback disagrees with the client custom-first policy."));
                File.Delete(Path.Combine(character, "custom.png")); File.Delete(Path.Combine(character, "portrait.png")); Put(character, "source.png"); Put(shared, "thumbnail.png");
                check("native_source_and_thumbnail_are_not_portrait_substitutes", () => require(!File.Exists(Pick()), "An unfinished source/thumbnail substituted for the portrait."));

                var original = TestDict("actors", new[] { TestDict("id", "lord_1_14", "name", "Rhagaea", "age", 36.94d, "isChild", false,
                    "clothing", "Imperial Jeweled Band, Red Dress", "appearance", TestDict("firstView", "Native red dress", "source", "hero_equipment")) },
                    "scene", "Numeric ages and native clothing must not redesign reference portraits.", "locationId", "town_ES5");
                var window = new List<Dictionary<string, object>> { TestDict("turnId", "n1", "exchangeId", "e1", "role", "npc", "speakerId", "lord_1_14", "speakerName", "Rhagaea", "text", "Rhagaea lights the kindling.", "frame", original) };
                string before = Json.Serialize(window), projected = Json.Serialize(ChatViewSelectionWindow(window));
                check("selector_omits_native_age_outfit_and_appearance", () => require(!projected.Contains("36.94") && !projected.Contains("Red Dress") && !projected.Contains("hero_equipment") && !projected.Contains("isChild"), "Native visual metadata reached the selector."));
                check("selector_preserves_source_identity_and_action", () => require(projected.Contains("n1") && projected.Contains("lord_1_14") && projected.Contains("Rhagaea lights the kindling."), "Source provenance/action was lost."));
                check("visual_projection_preserves_backend_eligibility_metadata", () => require(before == Json.Serialize(window) && ReadDictionaryList(original, "actors")[0].ContainsKey("isChild"), "The original eligibility/source frame was mutated."));
                check("image_frame_has_no_numeric_age_or_native_wardrobe", () => {
                    string frame = Json.Serialize(ChatViewVisualFrame(original, new[] { "lord_1_14" }));
                    require(!frame.Contains("36.94") && !frame.Contains("Red Dress") && frame.Contains(ChatViewReferenceContract), "Image metadata could redesign the subject.");
                });
                check("image_frame_retains_only_selected_cast", () => require(ReadDictionaryList(ChatViewVisualFrame(original, new[] { "someone_else" }), "actors").Count == 0, "Unselected actor reached the image frame."));
                check("effective_legacy_prompt_does_not_override_portrait_clothes", () => {
                    string text = ChatViewEffectivePrompt(ChatViewPhotoPromptName, ChatViewPhotoPrompt);
                    require(!text.Contains("outdated portrait clothing") && text.Contains("APPARENT AGE") && text.Contains("Do not make anyone younger or older"), "The legacy appearance override remained active.");
                });
                check("custom_style_survives_fixed_appearance_authority", () => {
                    string text = ChatViewEffectivePrompt(ChatViewPhotoPromptName, "Overcast natural light. Respect described clothing and accepted scene appearance over outdated portrait clothing.");
                    require(text.Contains("Overcast natural light.") && !text.Contains("outdated portrait clothing") && text.Contains("IMMUTABLE PORTRAIT IDENTITY") && text.Contains("VALIDATED CLOTHING"), "Custom style or fixed identity authority was lost.");
                });
                File.WriteAllText(Path.Combine(root, ChatViewSelectionPromptName), ChatViewSelectionPrompt);
                File.WriteAllText(Path.Combine(root, ChatViewPhotoPromptName), ChatViewPhotoPrompt);
                MigrateChatViewPromptDefaults(root);
                check("shipped_legacy_defaults_migrate_without_old_outfit_rule", () => require(File.ReadAllText(Path.Combine(root, ChatViewSelectionPromptName)) == ChatViewCurrentSelectionPrompt && File.ReadAllText(Path.Combine(root, ChatViewPhotoPromptName)) == ChatViewCurrentPhotoPrompt, "Saved shipped defaults were not migrated."));
                File.WriteAllText(Path.Combine(root, ChatViewPhotoPromptName), "My custom documentary lighting.");
                MigrateChatViewPromptDefaults(root); MigrateChatViewPromptDefaults(root);
                check("prompt_migration_is_idempotent_and_preserves_user_edits", () => require(File.ReadAllText(Path.Combine(root, ChatViewPhotoPromptName)) == "My custom documentary lighting.", "User-edited prompt was overwritten."));
            }
            finally
            {
                string absolute = Path.GetFullPath(root);
                if (Path.GetDirectoryName(absolute) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                    || !Path.GetFileName(absolute).StartsWith("reign-view-reference-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Reference fixture cleanup escaped its owned temporary root.");
                if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
            }
        }

        private static byte[] ChatViewTestPng(int width, int height, byte red)
        {
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = red; rgba[i + 1] = 40; rgba[i + 2] = 50; rgba[i + 3] = 255; }
            return PngEncoder.EncodeRgba(rgba, width, height);
        }
    }
}
