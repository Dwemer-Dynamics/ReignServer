using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunOutfitChangeContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, Func<bool> run)
            {
                try { AddSharedPortraitSelfTest(rows, "outfit_" + name, run(), "Provider-free outfit contract."); }
                catch (Exception ex) { AddSharedPortraitSelfTest(rows, "outfit_" + name, false, ex.Message); }
            }
            var player = new Dictionary<string, object> { ["heroStringId"] = "player", ["clanId"] = "clan_a", ["kingdomId"] = "realm_a", ["isRuler"] = false, ["sovereignHeroStringId"] = "other" };
            var npc = new Dictionary<string, object> { ["heroStringId"] = "npc", ["clanId"] = "clan_a", ["kingdomId"] = "realm_a", ["age"] = 25, ["isAlive"] = true };
            Check("own_clan", () => OutfitEligible(npc, player));
            npc["clanId"] = "clan_b";
            Check("same_realm_nonruler_hidden", () => !OutfitEligible(npc, player));
            player["isRuler"] = true; player["sovereignHeroStringId"] = "player";
            Check("same_realm_ruler", () => OutfitEligible(npc, player));
            npc["kingdomId"] = "realm_b";
            Check("foreign_realm_hidden", () => !OutfitEligible(npc, player));
            npc["companionOfId"] = "player";
            Check("companion", () => OutfitEligible(npc, player));
            npc["age"] = 17;
            Check("minor_cannot_use_adult_conversation_route", () => !OutfitEligible(npc, player));
            npc["age"] = 25; npc["heroStringId"] = "player";
            Check("self_hidden", () => !OutfitEligible(npc, player));
            Check("confidence_boundary", () => Enumerable.Range(0, 101).All(score => OutfitProfile(score) == (score < 40 ? "portrait" : "adultPortrait")));
            Check("prompt_preserves_identity_and_description", () => {
                string description = "Blue linen tunic, silver buttons, white cuffs.\nKeep the narrow belt.";
                string prompt = OutfitChangePrompt(description, OutfitPromptDefault);
                return prompt.Contains(Json.Serialize(description)) && prompt.Contains("Change ONLY the clothes")
                    && prompt.Contains("body size") && prompt.Contains("full-body framing") && prompt.Contains("Everything else");
            });
            Check("content_refusal_only", () => OutfitContentRefusal("content_policy_violation")
                && OutfitContentRefusal("rejected by the safety system") && !OutfitContentRefusal("HTTP 429 rate limit")
                && OutfitContentRefusal("{\"finishReason\":\"SAFETY\"}") && OutfitContentRefusal("IMAGE_SAFETY")
                && !OutfitContentRefusal("No API key is set") && !OutfitContentRefusal("Network timeout"));
            Check("openrouter_retains_refusal_not_reference", () => {
                var settings = new Dictionary<string, object> { ["openRouterApiKey"] = "fixture-only", ["portraitProvider"] = "OpenRouter" };
                var profile = ResolveImageGenerationProfile(settings, "portrait");
                var result = GenerateOpenRouterImage(settings, profile, "fixture", new byte[] { 1 }, "768x1024",
                    (a, b, c, d) => "{\"error\":{\"code\":\"content_policy_violation\",\"message\":\"Image rejected\"},\"input_references\":[\"private-reference-sentinel\"]}");
                return !result.Ok && OutfitContentRefusal(result.Error) && !result.Error.Contains("private-reference-sentinel");
            });
            Check("codex_failure_keeps_policy_distinct_from_limits", () => {
                string Failure(string kind)
                {
                    try { ReadCodexImageResult(new Dictionary<string, object> { ["type"] = "imageGeneration", ["status"] = "failed",
                        ["failure"] = new Dictionary<string, object> { ["type"] = kind } }, "unused"); }
                    catch (InvalidDataException ex) { return ex.Message; }
                    return "";
                }
                return OutfitContentRefusal(Failure("contentPolicyViolation")) && !OutfitContentRefusal(Failure("usageLimitExceeded"));
            });
            Check("codex_no_image_explicit_policy_refusal", () => {
                string refusal = CodexImageRefusalText(new Dictionary<string, object> { ["type"] = "agentMessage", ["text"] = "I cannot create that image because it violates content policies." });
                try { CompleteCodexImageTurn(new Dictionary<string, object> { ["status"] = "completed" }, null, refusal); }
                catch (InvalidDataException ex) { return OutfitContentRefusal(ex.Message)
                    && CodexImageRefusalText(new Dictionary<string, object> { ["type"] = "agentMessage", ["text"] = "The account limit was reached." }) == ""; }
                return false;
            });
            Check("normal_refusal_is_spoken_and_no_fallback", () => {
                var op = new OutfitChangeOperation { Profile = "portrait" };
                AcceptOutfitPreview(op, new PortraitImageCallResult { Ok = false, Error = "content_policy_violation" }, _ => throw new Exception("Must not prepare a refused image."));
                return op.Status == "failed" && op.NpcMessage == "I am not wearing that." && op.After == null;
            });
            Check("transport_error_is_not_character_refusal", () => {
                var op = new OutfitChangeOperation { Profile = "portrait" };
                AcceptOutfitPreview(op, new PortraitImageCallResult { Ok = false, Error = "HTTP 429 rate limit" }, bytes => bytes);
                return op.Status == "failed" && op.NpcMessage == "";
            });
            Check("cancelled_late_result_never_published", () => {
                var op = new OutfitChangeOperation { Status = "cancelled", Profile = "adultPortrait" };
                AcceptOutfitPreview(op, new PortraitImageCallResult { Ok = true, ImageBytes = new byte[] { 7 } }, _ => throw new Exception("Cancelled result must not be prepared."));
                return op.Status == "cancelled" && op.After == null;
            });
            Check("preview_only_becomes_ready", () => {
                var op = new OutfitChangeOperation { Profile = "adultPortrait" };
                AcceptOutfitPreview(op, new PortraitImageCallResult { Ok = true, ImageBytes = new byte[] { 7 } }, bytes => bytes);
                return op.Status == "ready" && op.After.SequenceEqual(new byte[] { 7 });
            });
            Check("prompt_catalog", () => DefaultPromptTemplates().ContainsKey(OutfitPromptName) && PromptMetadataByName().ContainsKey(OutfitPromptName));
            var identity = new Dictionary<string, object> { ["campaignId"] = "outfit_contract", ["timelineId"] = "main", ["heroStringId"] = "npc", ["playerHeroId"] = "player", ["operationId"] = Guid.NewGuid().ToString("N") };
            Check("cancel_before_begin_prevents_provider", () => {
                OutfitChangeApi(identity, "cancel");
                return ReadString(OutfitChangeApi(identity, "begin"), "status", "") == "cancelled";
            });
            Check("cancelled_cannot_confirm", () => !ReadBool(OutfitChangeApi(identity, "confirm"), "ok", true));
            Check("cross_hero_cannot_read_preview", () => {
                var other = new Dictionary<string, object>(identity) { ["heroStringId"] = "other_npc" };
                return !ReadBool(OutfitChangeApi(other, "status"), "ok", true);
            });
            Check("shared_cache_write_rejected", () => {
                var shared = new Dictionary<string, object>(identity) { ["campaignId"] = "_shared" };
                return !ReadBool(OutfitChangeApi(shared, "confirm"), "ok", true);
            });
            Check("path_traversal_rejected", () => !OutfitSafeId("../baseline") && !OutfitSafeId("a/b") && !OutfitSafeId(".."));
            Check("failed_commit_restores_all_files", () => {
                string root = Path.Combine(Path.GetTempPath(), "reign-outfit-contract-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string portrait = Path.Combine(root, "custom.png"), profile = Path.Combine(root, "profile.json");
                File.WriteAllBytes(portrait, new byte[] { 1 }); File.WriteAllBytes(profile, new byte[] { 2 });
                try
                {
                    int writes = 0;
                    try { CommitOutfitFiles(new Dictionary<string, byte[]> { [portrait] = new byte[] { 3 }, [profile] = new byte[] { 4 } },
                        (path, bytes) => { File.WriteAllBytes(path, bytes); if (++writes == 2) throw new IOException("injected disk failure"); }); }
                    catch (IOException) { }
                    return File.ReadAllBytes(portrait).SequenceEqual(new byte[] { 1 }) && File.ReadAllBytes(profile).SequenceEqual(new byte[] { 2 });
                }
                finally { File.Delete(portrait); File.Delete(profile); Directory.Delete(root); }
            });
            Check("confirmed_portrait_profile_and_dialogue_agree", () => {
                string root = Path.Combine(Path.GetTempPath(), "reign-outfit-commit-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string profilePath = Path.Combine(root, "profile.json");
                File.WriteAllText(profilePath, Json.Serialize(new Dictionary<string, object> { ["name"] = "Retained identity", ["age"] = 25 }));
                var op = new OutfitChangeOperation { Id = Guid.NewGuid().ToString("N"), Campaign = "isolated", Hero = "npc", Player = "player", Timeline = "main",
                    Description = "Blue linen tunic, silver buttons, fitted black boots.", StoredSourceHash = "", SourceHash = "before", After = new byte[] { 3, 4, 5 } };
                op.PortraitReceipt = OutfitContractReceipt(op.After, 768, 1024);
                try
                {
                    CommitOutfitInDirectory(op, root);
                    var profile = ReadJsonObject(profilePath);
                    var outfit = ReadJsonObject(Path.Combine(root, "portraits", "outfit.json"));
                    var formal = ReadJsonObject(Path.Combine(root, "portraits", "formal_outfit.json"));
                    var nativeRefresh = new Dictionary<string, object> { ["name"] = "Retained identity" };
                    ApplyConfirmedOutfit(outfit, nativeRefresh);
                    bool staleRejected = false;
                    try { CommitOutfitInDirectory(op, root); } catch (InvalidOperationException) { staleRejected = true; }
                    var confirmation = new Dictionary<string, object> { ["operationId"] = op.Id, ["playerHeroId"] = op.Player,
                        ["timelineId"] = op.Timeline, ["sourceSha256"] = op.SourceHash };
                    var recoveredResult = CommittedOutfitResponse(confirmation, root);
                    bool recovered = ReadString(recoveredResult, "status", "") == "committed"
                        && ReadDictionary(recoveredResult, "portraitReceipt") != null;
                    confirmation["playerHeroId"] = "other";
                    bool foreignRejected = CommittedOutfitResponse(confirmation, root) == null;
                    return File.ReadAllBytes(Path.Combine(root, "portraits", "custom.png")).SequenceEqual(op.After)
                        && ReadString(profile, "name", "") == "Retained identity" && ReadString(profile, "clothingDescription", "") == op.Description
                        && ReadString(nativeRefresh, "clothingDescription", "") == op.Description && ReadString(formal, "description", "") == op.Description
                        && ConfirmedOutfitAfterScene(outfit, 0) == op.Description
                        && ConfirmedOutfitAfterScene(outfit, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()) == ""
                        && staleRejected && recovered && foreignRejected;
                }
                finally
                {
                    foreach (string name in new[] { "custom.png", "outfit.json", "formal_outfit.json", OutfitPortraitReceiptFileName })
                    { string file = Path.Combine(root, "portraits", name); if (File.Exists(file)) File.Delete(file); }
                    if (Directory.Exists(Path.Combine(root, "portraits"))) Directory.Delete(Path.Combine(root, "portraits"));
                    File.Delete(profilePath); Directory.Delete(root);
                }
            });
            Check("custom_full_derivative_set_preserves_shared_and_other_save", () => {
                string root = Path.Combine(Path.GetTempPath(), "reign-outfit-derivatives-" + Guid.NewGuid().ToString("N"));
                string shared = Path.Combine(root, "_shared", "npc"), other = Path.Combine(root, "other-save", "npc"), current = Path.Combine(root, "current-save", "npc");
                foreach (string directory in new[] { shared, other, current }) Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(shared, "portrait.png"), new byte[] { 11, 12 });
                File.WriteAllBytes(Path.Combine(other, "custom.png"), new byte[] { 21, 22 });
                try
                {
                    byte[] master = LinuxImageCodec.Fixture(768, 1024);
                    string sourcePath = Path.Combine(current, "custom.png");
                    File.WriteAllBytes(sourcePath, master);
                    var receipt = OutfitContractReceipt(master, 768, 1024);
                    File.WriteAllText(Path.Combine(current, OutfitPortraitReceiptFileName), Json.Serialize(receipt));
                    byte[] rgba = PngReencode.DecodeToRgba(master, out int width, out int height);
                    var build = PortraitDerivativeCore.BuildRequiredFocusedV2(rgba, width, height, new FileInfo(sourcePath), PngEncoder.EncodeRgba, OutfitContractFocus());
                    if (!PortraitDerivativeCore.CommitV2(sourcePath, current, build, Encoding.UTF8.GetBytes(Json.Serialize(build.Manifest)), out string error))
                        throw new IOException(error);
                    return PortraitDerivativeCore.IsCurrentV2(build.Manifest, sourcePath, current)
                        && build.Manifest.SourceFileName == "custom.png"
                        && build.Manifest.FaceCandidateCount == 1 && build.Manifest.FocusMethod != "legacy_fallback"
                        && new[] { "thumbnail.png", "thumbnail_wide.png", "portrait_chest.png", "zoom.png", ".portrait_derivatives.json" }.All(name => File.Exists(Path.Combine(current, name)))
                        && File.ReadAllBytes(sourcePath).SequenceEqual(master)
                        && File.ReadAllBytes(Path.Combine(shared, "portrait.png")).SequenceEqual(new byte[] { 11, 12 })
                        && File.ReadAllBytes(Path.Combine(other, "custom.png")).SequenceEqual(new byte[] { 21, 22 })
                        && Directory.GetFiles(shared).Length == 1 && Directory.GetFiles(other).Length == 1;
                }
                finally
                {
                    foreach (string directory in new[] { current, shared, other })
                    {
                        foreach (string name in new[] { "custom.png", "portrait.png", "thumbnail.png", "thumbnail_wide.png", "portrait_chest.png", "zoom.png", ".portrait_derivatives.json", OutfitPortraitReceiptFileName })
                        { string file = Path.Combine(directory, name); if (File.Exists(file)) File.Delete(file); }
                        Directory.Delete(directory); Directory.Delete(Path.GetDirectoryName(directory));
                    }
                    Directory.Delete(root);
                }
            });
            return rows;
        }

        private static PortraitFaceFocus OutfitContractFocus() => new PortraitFaceFocus
            { Method = "fixture_detected_face", Model = "fixture", Confidence = 0.99, CandidateCount = 1, X = 0.4, Y = 0.08, Width = 0.2, Height = 0.14 };

        private static Dictionary<string, object> OutfitContractReceipt(byte[] master, int width, int height)
            => BuildPortraitProductReceipt(new PortraitProductValidation { Width = width, Height = height, Focus = OutfitContractFocus() },
                master, "fixture garment edit", new PortraitImageCallResult { Ok = true, ImageBytes = master }, new Dictionary<string, object> { ["kind"] = "isolated_fixture" });
    }
}
