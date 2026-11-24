using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private const string OutfitRecoveryFixtureCampaign = "outfit_recovery_selftest";
        private const string OutfitRecoveryFixtureHero = "hero";
        private const string OutfitRecoveryFixtureFlag = "--run-outfit-recovery-fixture";

        private static string OutfitRecoveryFixtureHome => Path.Combine(Program.DataDir, "tests", "outfit-recovery-fixtures");

        internal static bool TryRunOutfitRecoveryFixtureCommand(string[] args)
        {
            if (!args.Contains(OutfitRecoveryFixtureFlag, StringComparer.Ordinal)) return false;
            if (Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || Environment.GetEnvironmentVariable("REIGN_DB_NAME") != ReignPostgreSqlOptions.ValidationDatabaseName
                || args.Length != 3 || args[0] != OutfitRecoveryFixtureFlag
                || !Guid.TryParseExact(args[1], "N", out Guid id) || args[1] != id.ToString("N"))
                throw new InvalidOperationException("An outfit restart fixture needs canonical validation ownership.");

            string fixtureRoot = Path.Combine(OutfitRecoveryFixtureHome, args[1]);
            string owner = Path.Combine(fixtureRoot, "fixture-owner");
            if (!RequireOutfitPhysicalPath(OutfitRecoveryFixtureHome, owner, directory: false) || File.ReadAllText(owner) != args[1])
                throw new InvalidOperationException("The outfit restart fixture has no exact owner marker.");

            string root = Path.Combine(fixtureRoot, "campaigns");
            string journalRoot = Path.Combine(OutfitCampaignJournalRoot(root, OutfitRecoveryFixtureCampaign), OutfitRecoveryFixtureHero);
            string characterRoot = OutfitCharacterRoot(root, OutfitRecoveryFixtureCampaign, OutfitRecoveryFixtureHero);
            using var lease = AcquireOutfitRuntimeOwnership(fixtureRoot);
            if (args[2] == "recover")
            {
                Program.CampaignsRootOverride.Value = root;
                DiscoverOutfitPublicationsAtStartup();
                var initial = OutfitRecoveryStatus(OutfitRecoveryFixtureCampaign);
                if (ReadString(initial, "journalKind", "") != "outfit")
                    throw new InvalidDataException("Startup did not discover an outfit ownership fence.");
                try
                {
                    CampaignFileService.CharacterDirectory(OutfitRecoveryFixtureCampaign, OutfitRecoveryFixtureHero);
                    throw new InvalidDataException("The discovered outfit journal did not fence campaign reads.");
                }
                catch (PersistenceService.SaveSyncCampaignFencedException ex)
                    when (ReadString(ex.Status, "journalKind", "") == "outfit") { }
                string sibling = OutfitRecoveryFixtureCampaign.ToUpperInvariant();
                if (OutfitRecoveryStatus(sibling) != null)
                    throw new InvalidDataException("A distinct on-disk Linux campaign was incorrectly fenced.");
                string portraitPath = Path.Combine(characterRoot, "portraits");
                try
                {
                    ReadCampaignFormalOutfitInDirectory(portraitPath, OutfitRecoveryFixtureCampaign, OutfitRecoveryFixtureHero);
                    throw new InvalidDataException("A pre-resolved formal outfit reader passed the fence.");
                }
                catch (PersistenceService.SaveSyncCampaignFencedException) { }

                var wrong = TryAdmitOutfitRequest("confirm", OutfitRecoveryFixtureCampaign, OutfitRecoveryFixtureHero, args[1],
                    TestDict("playerHeroId", "other", "timelineId", "main", "sourceSha256", Sha256Hex(new byte[] { 17 })));
                if (ReadString(wrong, "status", "") != "recovery_required" || !File.Exists(Path.Combine(journalRoot, OutfitFileJournalName)))
                    throw new InvalidDataException("A mismatched confirmation settled an owned outfit journal.");
                RecoverOutfitPublicationsAtStartup();
                Environment.ExitCode = OutfitRecoveryStatus(OutfitRecoveryFixtureCampaign) == null ? 0 : 87;
                return true;
            }

            if (!int.TryParse(args[2], out int prefix) || prefix < 0 || prefix > OutfitFileRoles.Length + 1)
                throw new InvalidDataException("The outfit test publication boundary was invalid.");
            var writes = OutfitFileRoles.Select((role, i) => new KeyValuePair<string, byte[]>(
                OutfitRolePath(characterRoot, role), new byte[] { (byte)(80 + i), 127 })).ToArray();
            var journal = StageOutfitFileJournal(journalRoot, characterRoot, OutfitRecoveryFixtureCampaign,
                OutfitRecoveryFixtureHero, args[1], "player", "main", Sha256Hex(new byte[] { 17 }), writes);
            for (int i = 0; i < Math.Min(prefix, writes.Length); i++)
                PersistenceService.WriteRecoveryFile(writes[i].Key, writes[i].Value, mode: journal.Entries[i].BeforeMode);
            if (prefix > writes.Length) CommitOutfitFileJournal(journalRoot, characterRoot, journal);
            // Terminate without running recovery or managed lock disposal. The OS owns the
            // release; a separate invocation has to reconstruct the result from disk.
            Environment.Exit(86);
            return true;
        }

        private static void RunOutfitRestartChild(string id, string phase, int expectedExit)
        {
            string executable = Environment.ProcessPath;
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(PortraitsService).Assembly.Location);
            start.ArgumentList.Add(OutfitRecoveryFixtureFlag);
            start.ArgumentList.Add(id);
            start.ArgumentList.Add(phase);
            RunOutfitFixtureProcess(start, expectedExit);
        }

        private static void RunOutfitFixtureProcess(System.Diagnostics.ProcessStartInfo start, int expectedExit)
        {
            using var child = System.Diagnostics.Process.Start(start);
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(30000))
            {
                child.Kill();
                child.WaitForExit(5000);
                throw new IOException("The isolated outfit restart child did not exit.");
            }
            if (child.ExitCode != expectedExit)
                throw new IOException("Outfit restart child returned " + child.ExitCode + ": " + output.Result + error.Result);
        }

        private static bool VerifyOutfitProcessRecovery(int prefix, bool tamper = false, bool omitOptionalBefore = false)
        {
            if (Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || Environment.GetEnvironmentVariable("REIGN_DB_NAME") != ReignPostgreSqlOptions.ValidationDatabaseName)
                throw new InvalidOperationException("Outfit process verification requires the isolated canonical validation database.");
            string id = Guid.NewGuid().ToString("N");
            string fixtureRoot = Path.Combine(OutfitRecoveryFixtureHome, id);
            Directory.CreateDirectory(fixtureRoot);
            string root = Path.Combine(fixtureRoot, "campaigns");
            string characterRoot = OutfitCharacterRoot(root, OutfitRecoveryFixtureCampaign, OutfitRecoveryFixtureHero);
            string[] files = OutfitFileRoles.Select(role => OutfitRolePath(characterRoot, role)).ToArray();
            string active = Path.Combine(OutfitCampaignJournalRoot(root, OutfitRecoveryFixtureCampaign), OutfitRecoveryFixtureHero, OutfitFileJournalName);
            var before = files.Select((_, i) => i % 3 == 0 ? null : i % 3 == 1 ? Array.Empty<byte>() : new byte[] { (byte)i }).ToArray();
            try
            {
                File.WriteAllText(Path.Combine(fixtureRoot, "fixture-owner"), id);
                Directory.CreateDirectory(Path.Combine(root, OutfitRecoveryFixtureCampaign.ToUpperInvariant()));
                for (int i = 0; i < files.Length; i++)
                    if (before[i] != null) PersistenceService.WriteRecoveryFile(files[i], before[i],
                        mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                RunOutfitRestartChild(id, prefix.ToString(System.Globalization.CultureInfo.InvariantCulture), 86);
                bool offlineFenced;
                try { RequireOfflineOutfitCampaignAvailable(root, OutfitRecoveryFixtureCampaign); offlineFenced = false; }
                catch (InvalidOperationException) { offlineFenced = true; }
                if (!offlineFenced) return false;

                if (tamper) PersistenceService.WriteRecoveryFile(files[0], new byte[] { 6, 7, 8 });
                if (omitOptionalBefore)
                {
                    var corrupt = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(active));
                    corrupt["Entries"][0].AsObject().Remove("BeforeSha256");
                    File.WriteAllText(active, corrupt.ToJsonString());
                }
                bool refused = tamper || omitOptionalBefore;
                bool committed = prefix > files.Length;
                string[] hashes = files.Select(path => OutfitHashOrAbsent(ReadOutfitFileOrAbsent(path))).ToArray();
                RunOutfitRestartChild(id, "recover", refused ? 87 : 0);
                if (refused)
                    return File.Exists(active) && hashes.SequenceEqual(files.Select(path => OutfitHashOrAbsent(ReadOutfitFileOrAbsent(path))));
                if (File.Exists(active)) return false;
                RequireOfflineOutfitCampaignAvailable(root, OutfitRecoveryFixtureCampaign);
                for (int i = 0; i < files.Length; i++)
                {
                    byte[] expected = committed ? new byte[] { (byte)(80 + i), 127 } : before[i];
                    if (OutfitHashOrAbsent(ReadOutfitFileOrAbsent(files[i])) != OutfitHashOrAbsent(expected)) return false;
                    if (before[i] != null && OperatingSystem.IsLinux() && File.GetUnixFileMode(files[i])
                        != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)) return false;
                }
                return true;
            }
            finally
            {
                if (RequireOutfitPhysicalPath(OutfitRecoveryFixtureHome, fixtureRoot, directory: true))
                    Directory.Delete(fixtureRoot, recursive: true);
            }
        }

        private static bool VerifyOutfitRuntimeOwnershipAndCancellation()
        {
            string id = Guid.NewGuid().ToString("N");
            string fixtureRoot = Path.Combine(OutfitRecoveryFixtureHome, id);
            Directory.CreateDirectory(fixtureRoot);
            try
            {
                File.WriteAllText(Path.Combine(fixtureRoot, "fixture-owner"), id);
                using (AcquireOutfitRuntimeOwnership(fixtureRoot))
                {
                    RunOutfitRestartChild(id, "0", 1);
                    string executable = Environment.ProcessPath;
                    var cancel = new System.Diagnostics.ProcessStartInfo(executable)
                    {
                        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    cancel.Environment["REIGN_DATA_ROOT"] = fixtureRoot;
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        cancel.ArgumentList.Add(typeof(PortraitsService).Assembly.Location);
                    cancel.ArgumentList.Add("--cancel-verification");
                    RunOutfitFixtureProcess(cancel, 0);
                }
                return File.Exists(Path.Combine(fixtureRoot, "tests", "verification", "cancel.request"))
                    && !Directory.Exists(Path.Combine(fixtureRoot, "campaigns"));
            }
            finally
            {
                if (RequireOutfitPhysicalPath(OutfitRecoveryFixtureHome, fixtureRoot, directory: true))
                    Directory.Delete(fixtureRoot, recursive: true);
            }
        }

        internal static List<Dictionary<string, object>> RunOutfitChangeContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string name, Func<bool> run)
            {
                try { AddSharedPortraitSelfTest(rows, "outfit_" + name, run(), "Provider-free outfit contract."); }
                catch (Exception ex) { AddSharedPortraitSelfTest(rows, "outfit_" + name, false, ex.Message); }
            }
            if (Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1"
                && Environment.GetEnvironmentVariable("REIGN_DB_NAME") == ReignPostgreSqlOptions.ValidationDatabaseName)
            {
                Check("interrupted_publication_recovers_each_prefix", () =>
                    Enumerable.Range(0, OutfitFileRoles.Length + 1).All(prefix => VerifyOutfitProcessRecovery(prefix)));
                Check("durable_commit_survives_hard_exit_before_cleanup", () => VerifyOutfitProcessRecovery(OutfitFileRoles.Length + 1));
                Check("restart_refuses_unowned_live_bytes", () => VerifyOutfitProcessRecovery(1, tamper: true));
                Check("restart_rejects_omitted_authoritative_absence", () => VerifyOutfitProcessRecovery(1, omitOptionalBefore: true));
                Check("runtime_owner_excludes_campaign_mutators_but_allows_cancel", VerifyOutfitRuntimeOwnershipAndCancellation);
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
            Check("prompt_catalog", () => PromptService.DefaultPromptTemplates().ContainsKey(OutfitPromptName) && PromptService.PromptMetadataByName().ContainsKey(OutfitPromptName));
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
                    var profile = CampaignFileService.ReadJsonObject(profilePath);
                    var outfit = CampaignFileService.ReadJsonObject(Path.Combine(root, "portraits", "outfit.json"));
                    var formal = CampaignFileService.ReadJsonObject(Path.Combine(root, "portraits", "formal_outfit.json"));
                    var nativeRefresh = new Dictionary<string, object> { ["name"] = "Retained identity" };
                    ApplyConfirmedOutfit(outfit, nativeRefresh);
                    bool staleRejected = false;
                    try { CommitOutfitInDirectory(op, root); } catch (InvalidOperationException) { staleRejected = true; }
                    var confirmation = new Dictionary<string, object> { ["campaignId"] = op.Campaign, ["heroStringId"] = op.Hero,
                        ["operationId"] = op.Id, ["playerHeroId"] = op.Player, ["timelineId"] = op.Timeline, ["sourceSha256"] = op.SourceHash };
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
                    foreach (string name in new[] { "custom.png", "outfit.json", "formal_outfit.json", OutfitPortraitReceiptFileName, OutfitConfirmationFileName })
                    { string file = Path.Combine(root, "portraits", name); if (File.Exists(file)) File.Delete(file); }
                    if (Directory.Exists(Path.Combine(root, "portraits"))) Directory.Delete(Path.Combine(root, "portraits"));
                    File.Delete(profilePath); Directory.Delete(root);
                }
            });
            Check("confirmation_requires_final_marker_after_reseal", VerifyOutfitConfirmationPrefixes);
            Check("confirmation_marker_preserves_legacy_and_refresh", VerifyOutfitConfirmationCompatibility);
            Check("confirmation_requires_matching_restored_profile", VerifyOutfitConfirmationProfileReadback);
            Check("retained_projection_requires_final_marker", VerifyRetainedOutfitProjectionPrefixes);
            Check("retained_projection_preserves_refresh_and_formal_scans", VerifyRetainedOutfitProjectionCompatibility);
            Check("admitted_render_snapshot_preserves_committed_outfit_across_restore", VerifyAdmittedPortraitRenderSnapshot);
            Check("confirmation_marker_failure_rolls_back", VerifyOutfitConfirmationRollback);
            Check("confirmation_denied_marker_preserves_preimages", VerifyOutfitDeniedMarker);
            Check("confirmation_rollback_continues_after_restore_error", VerifyOutfitRollbackContinues);
            Check("profile_checkpoint_invalidated_before_any_outfit_write", VerifyOutfitProfileInvalidation);
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

        private static bool VerifyOutfitConfirmationPrefixes()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                string portraits = Path.Combine(root, "portraits");
                string profile = Path.Combine(root, "profile.json");
                string outfitPath = Path.Combine(portraits, "outfit.json");
                string markerPath = Path.Combine(portraits, OutfitConfirmationFileName);
                var order = new List<string>();
                bool everyIncompletePrefixDenied = CommittedOutfitResponse(request, root) == null;
                bool resealed = false;
                CommitOutfitInDirectory(op, root, (path, bytes) =>
                {
                    WriteSharedPortraitAtomic(path, bytes);
                    order.Add(Path.GetFileName(path));
                    if (order.Count == 4)
                    {
                        // The same projection applied by ordinary CharacterProfiles upsert.
                        var native = new Dictionary<string, object> { ["name"] = "Fresh native profile" };
                        ApplyConfirmedOutfit(CampaignFileService.ReadJsonObject(outfitPath), native);
                        WriteSharedPortraitAtomic(profile, Encoding.UTF8.GetBytes(Json.Serialize(native)));
                        resealed = ReadString(CampaignFileService.ReadJsonObject(profile), "clothingDescription", "") == op.Description;
                    }
                    if (path != markerPath)
                        everyIncompletePrefixDenied &= CommittedOutfitResponse(request, root) == null;
                });

                var confirmed = CommittedOutfitResponse(request, root);
                byte[] marker = File.ReadAllBytes(markerPath);
                File.Delete(markerPath);
                string alternate = Path.Combine(portraits, OutfitConfirmationFileName.ToUpperInvariant());
                File.WriteAllBytes(alternate, marker);
                bool alternateCaseDenied = CommittedOutfitResponse(request, root) == null;
                return order.SequenceEqual(new[] { "custom.png", "formal_outfit.json", "outfit.json",
                        OutfitPortraitReceiptFileName, "profile.json", OutfitConfirmationFileName })
                    && everyIncompletePrefixDenied && resealed && alternateCaseDenied
                    && ReadString(confirmed, "status", "") == "committed"
                    && ReadString(confirmed, "imageBase64", "") == Convert.ToBase64String(op.After);
            });

        private static bool VerifyOutfitConfirmationCompatibility()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                CommitOutfitInDirectory(op, root);
                string portraits = Path.Combine(root, "portraits");
                string profilePath = Path.Combine(root, "profile.json");
                string outfitPath = Path.Combine(portraits, "outfit.json");
                string markerPath = Path.Combine(portraits, OutfitConfirmationFileName);
                byte[] marker = File.ReadAllBytes(markerPath);
                byte[] versioned = File.ReadAllBytes(outfitPath);

                var profile = CampaignFileService.ReadJsonObject(profilePath);
                profile["name"] = "Independent profile edit";
                WriteSharedPortraitAtomic(profilePath, Encoding.UTF8.GetBytes(Json.Serialize(profile)));
                bool unrelatedProfileAccepted = CommittedOutfitResponse(request, root) != null;

                var sameImageReceipt = OutfitContractReceipt(op.After, 768, 1024);
                sameImageReceipt["fixtureOnlyAnnotation"] = "A new validated crop receipt";
                ReplaceRefreshedCharacterOverrideInDirectory(portraits, op.After, sameImageReceipt, WriteSharedPortraitAtomic);
                bool sameImageRefreshAccepted = ReadString(ReadDictionary(CommittedOutfitResponse(request, root), "portraitReceipt"),
                    "fixtureOnlyAnnotation", "") == "A new validated crop receipt"
                    && File.ReadAllBytes(markerPath).SequenceEqual(marker);

                var outfit = CampaignFileService.ReadJsonObject(outfitPath);
                outfit[OutfitConfirmationFormatKey] = "future-format";
                WriteSharedPortraitAtomic(outfitPath, Encoding.UTF8.GetBytes(Json.Serialize(outfit)));
                bool unknownFormatDenied = CommittedOutfitResponse(request, root) == null;
                outfit.Remove(OutfitConfirmationFormatKey);
                WriteSharedPortraitAtomic(outfitPath, Encoding.UTF8.GetBytes(Json.Serialize(outfit)));
                File.Delete(markerPath);
                bool legacyAccepted = CommittedOutfitResponse(request, root) != null;

                WriteSharedPortraitAtomic(outfitPath, versioned);
                WriteSharedPortraitAtomic(markerPath, marker);
                byte[] changedImage = { 7, 8, 9 };
                ReplaceRefreshedCharacterOverrideInDirectory(portraits, changedImage,
                    OutfitContractReceipt(changedImage, 768, 1024), WriteSharedPortraitAtomic);
                var refreshedProfile = new Dictionary<string, object>();
                ApplyConfirmedOutfit(CampaignFileService.ReadJsonObject(outfitPath), refreshedProfile);
                return unrelatedProfileAccepted && sameImageRefreshAccepted && unknownFormatDenied && legacyAccepted
                    && CommittedOutfitResponse(request, root) == null
                    && ReadString(refreshedProfile, "clothingDescription", "") == op.Description
                    && File.ReadAllBytes(markerPath).SequenceEqual(marker);
            });

        private static bool VerifyOutfitConfirmationProfileReadback()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                string profilePath = Path.Combine(root, "profile.json");
                byte[] priorProfile = Encoding.UTF8.GetBytes(Json.Serialize(
                    new Dictionary<string, object> { ["name"] = "Earlier checkpoint profile" }));
                WriteSharedPortraitAtomic(profilePath, priorProfile);
                CommitOutfitInDirectory(op, root);

                string portraits = Path.Combine(root, "portraits");
                string outfitPath = Path.Combine(portraits, "outfit.json");
                string markerPath = Path.Combine(portraits, OutfitConfirmationFileName);
                byte[] completedProfile = File.ReadAllBytes(profilePath);
                byte[] completedOutfit = File.ReadAllBytes(outfitPath);
                byte[] marker = File.ReadAllBytes(markerPath);
                byte[] image = File.ReadAllBytes(Path.Combine(portraits, "custom.png"));
                byte[] crop = File.ReadAllBytes(Path.Combine(portraits, OutfitPortraitReceiptFileName));
                bool completeV2Accepted = CommittedOutfitResponse(request, root) != null;

                // This has the same split as the retained portrait overlay: all
                // appearance files are complete, but the snapshot profile is older.
                WriteSharedPortraitAtomic(profilePath, priorProfile);
                bool restoredV2MismatchRejected = CommittedOutfitResponse(request, root) == null;
                bool rejectedWithoutWriting = File.ReadAllBytes(profilePath).SequenceEqual(priorProfile)
                    && File.ReadAllBytes(outfitPath).SequenceEqual(completedOutfit)
                    && File.ReadAllBytes(markerPath).SequenceEqual(marker)
                    && File.ReadAllBytes(Path.Combine(portraits, "custom.png")).SequenceEqual(image)
                    && File.ReadAllBytes(Path.Combine(portraits, OutfitPortraitReceiptFileName)).SequenceEqual(crop);

                var legacy = CampaignFileService.ReadJsonObject(outfitPath);
                legacy.Remove(OutfitConfirmationFormatKey);
                WriteSharedPortraitAtomic(outfitPath, Encoding.UTF8.GetBytes(Json.Serialize(legacy)));
                File.Delete(markerPath);
                bool legacyTearRejected = CommittedOutfitResponse(request, root) == null;
                WriteSharedPortraitAtomic(profilePath, completedProfile);
                bool completeLegacyAccepted = CommittedOutfitResponse(request, root) != null;

                bool wrongTupleRejected = true;
                foreach (string field in new[] { "operationId", "playerHeroId", "timelineId", "sourceSha256", "portraitSha256", "description", "updatedUtc" })
                {
                    var mismatched = CampaignFileService.ReadJsonObject(profilePath);
                    ReadDictionary(mismatched, "currentOutfit")[field] = "different";
                    WriteSharedPortraitAtomic(profilePath, Encoding.UTF8.GetBytes(Json.Serialize(mismatched)));
                    wrongTupleRejected &= CommittedOutfitResponse(request, root) == null;
                    WriteSharedPortraitAtomic(profilePath, completedProfile);
                }
                var differentClothing = CampaignFileService.ReadJsonObject(profilePath);
                differentClothing["clothingDescription"] = "Different clothing.";
                WriteSharedPortraitAtomic(profilePath, Encoding.UTF8.GetBytes(Json.Serialize(differentClothing)));
                bool wrongClothingRejected = CommittedOutfitResponse(request, root) == null;

                var incomplete = new Dictionary<string, object>(legacy);
                incomplete.Remove("updatedUtc");
                var incompleteProfile = new Dictionary<string, object>();
                ApplyConfirmedOutfit(incomplete, incompleteProfile);
                WriteSharedPortraitAtomic(outfitPath, Encoding.UTF8.GetBytes(Json.Serialize(incomplete)));
                WriteSharedPortraitAtomic(profilePath, Encoding.UTF8.GetBytes(Json.Serialize(incompleteProfile)));
                bool bothMissingTimestampRejected = CommittedOutfitResponse(request, root) == null;

                // A successful native profile projection and an independent formal scan
                // are still compatible with a complete legacy receipt.
                var native = new Dictionary<string, object> { ["name"] = "Fresh native profile" };
                ApplyConfirmedOutfit(legacy, native);
                WriteSharedPortraitAtomic(outfitPath, Encoding.UTF8.GetBytes(Json.Serialize(legacy)));
                WriteSharedPortraitAtomic(profilePath, Encoding.UTF8.GetBytes(Json.Serialize(native)));
                string formalPath = Path.Combine(portraits, "formal_outfit.json");
                var rescanned = new Dictionary<string, object> { ["status"] = "completed",
                    ["imageSha256"] = Sha256Hex(image), ["description"] = "Independent formal vision description." };
                byte[] formal = Encoding.UTF8.GetBytes(Json.Serialize(rescanned));
                WriteSharedPortraitAtomic(formalPath, formal);
                return completeV2Accepted && restoredV2MismatchRejected && rejectedWithoutWriting
                    && legacyTearRejected && completeLegacyAccepted && wrongTupleRejected && wrongClothingRejected
                    && bothMissingTimestampRejected && CommittedOutfitResponse(request, root) != null
                    && File.ReadAllBytes(formalPath).SequenceEqual(formal) && !File.Exists(markerPath);
            });

        private static bool VerifyRetainedOutfitProjectionPrefixes()
            => WithOutfitConfirmationFixture((root, op, _) =>
            {
                string portraits = Path.Combine(root, "portraits");
                void WriteThenCheck(OutfitChangeOperation attempt, string priorDescription)
                {
                    CommitOutfitInDirectory(attempt, root, (path, bytes) =>
                    {
                        WriteSharedPortraitAtomic(path, bytes);
                        string name = Path.GetFileName(path);
                        bool committed = name == OutfitConfirmationFileName;
                        var profile = new Dictionary<string, object>();
                        var outfit = ReadRetainedOutfitMetadata(portraits, attempt.Campaign, attempt.Hero);
                        ApplyConfirmedOutfit(outfit, profile);
                        string expectedOutfit = committed ? attempt.Description
                            : name is "custom.png" or "formal_outfit.json" ? priorDescription : "";
                        string expectedFormal = committed ? attempt.Description
                            : name == "custom.png" ? priorDescription : "";
                        if (ReadString(profile, "clothingDescription", "") != expectedOutfit
                            || ConfirmedOutfitAfterScene(outfit, 0) != expectedOutfit
                            || ReadString(ReadCampaignFormalOutfitInDirectory(portraits, attempt.Campaign, attempt.Hero),
                                "description", "") != expectedFormal)
                            throw new InvalidDataException($"Outfit projection disagreed with completion after '{name}'.");
                    });
                }

                WriteThenCheck(op, "");
                WriteThenCheck(MakeLaterOutfitFixture(op), op.Description);
                return true;
            });

        private static bool VerifyRetainedOutfitProjectionCompatibility()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                CommitOutfitInDirectory(op, root);
                string portraits = Path.Combine(root, "portraits");
                string formalPath = Path.Combine(portraits, "formal_outfit.json");
                string outfitPath = Path.Combine(portraits, "outfit.json");
                string markerPath = Path.Combine(portraits, OutfitConfirmationFileName);
                string profilePath = Path.Combine(root, "profile.json");
                byte[] marker = File.ReadAllBytes(markerPath);
                var outfit = CampaignFileService.ReadJsonObject(outfitPath);
                var formal = CampaignFileService.ReadJsonObject(formalPath);

                string FormalDescription() => ReadString(ReadCampaignFormalOutfitInDirectory(portraits, op.Campaign, op.Hero), "description", "");
                void WriteRecord(string path, Dictionary<string, object> record)
                    => WriteSharedPortraitAtomic(path, Encoding.UTF8.GetBytes(Json.Serialize(record)));

                WriteRecord(profilePath, new Dictionary<string, object> { ["clothingDescription"] = "Earlier temporal outfit." });
                byte[] refreshed = { 7, 8, 9 };
                ReplaceRefreshedCharacterOverrideInDirectory(portraits, refreshed,
                    OutfitContractReceipt(refreshed, 768, 1024), WriteSharedPortraitAtomic);
                var retained = ReadRetainedOutfitMetadata(portraits, op.Campaign, op.Hero);
                var projection = CampaignFileService.ReadJsonObject(profilePath);
                ApplyConfirmedOutfit(retained, projection);
                bool retainedAcrossRefreshAndRestore = ReadString(projection, "clothingDescription", "") == op.Description
                    && ConfirmedOutfitAfterScene(retained, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()) == ""
                    && FormalDescription() == op.Description && CommittedOutfitResponse(request, root) == null
                    && File.ReadAllBytes(markerPath).SequenceEqual(marker);

                var mismatched = new Dictionary<string, object>(formal) { ["operationId"] = Guid.NewGuid().ToString("N") };
                WriteRecord(formalPath, mismatched);
                bool mismatchWithheld = FormalDescription() == ""
                    && ReadRetainedOutfitMetadata(portraits, op.Campaign, "foreign_hero") == null;

                outfit.Remove(OutfitConfirmationFormatKey);
                WriteRecord(outfitPath, outfit);
                WriteRecord(formalPath, formal);
                bool versionedFormalRequiresVersionedOutfit = FormalDescription() == "";

                formal.Remove(OutfitConfirmationFormatKey);
                File.Delete(markerPath);
                WriteRecord(formalPath, formal);
                bool legacyAccepted = FormalDescription() == op.Description
                    && ConfirmedOutfitAfterScene(ReadRetainedOutfitMetadata(portraits, op.Campaign, op.Hero), 0) == op.Description;

                WriteRecord(formalPath, new Dictionary<string, object>
                    { ["status"] = "completed", ["description"] = "Independent formal portrait scan." });
                return retainedAcrossRefreshAndRestore && mismatchWithheld && versionedFormalRequiresVersionedOutfit
                    && legacyAccepted && FormalDescription() == "Independent formal portrait scan.";
            });

        private static bool VerifyAdmittedPortraitRenderSnapshot()
            => WithOutfitConfirmationFixture((fixture, op, request) =>
            {
                string root = Path.Combine(fixture, "campaigns");
                string characterRoot = OutfitCharacterRoot(root, op.Campaign, op.Hero);
                string portraits = Path.Combine(characterRoot, "portraits");
                Directory.CreateDirectory(portraits);
                try
                {
                    CampaignPortraitSnapshot Read() => ReadCampaignPortraitSnapshot(root, op.Campaign, op.Hero);
                    bool RejectsIncomplete()
                    {
                        try { Read(); return false; }
                        catch (InvalidDataException) { return true; }
                    }

                    if (Read() != null) return false;
                    CommitOutfitInDirectory(op, characterRoot);
                    WriteSharedPortraitAtomic(Path.Combine(characterRoot, "profile.json"), Encoding.UTF8.GetBytes("{}"));
                    var restored = Read();
                    if (restored?.Custom != true || !restored.Image.SequenceEqual(op.After)
                        || CommittedOutfitResponse(request, characterRoot) != null) return false;

                    string confirmation = Path.Combine(portraits, OutfitConfirmationFileName);
                    byte[] accepted = File.ReadAllBytes(confirmation);
                    File.Delete(confirmation);
                    if (!RejectsIncomplete()) return false;
                    WriteSharedPortraitAtomic(confirmation, accepted);

                    byte[] refreshed = { 7, 8, 9 };
                    var receipt = OutfitContractReceipt(refreshed, 768, 1024);
                    string masterPath = Path.Combine(portraits, "portrait.png");
                    PublishPortraitRefresh(CapturePortraitRefreshBaseline(portraits), refreshed, receipt, publication =>
                    {
                        WriteSharedPortraitAtomic(masterPath, refreshed);
                        var metadata = TestDict("productReceipt", receipt, PortraitRefreshPublicationKey, publication);
                        WriteSharedPortraitAtomic(Path.Combine(portraits, "portrait.json"), Encoding.UTF8.GetBytes(Json.Serialize(metadata)));
                        return masterPath;
                    });
                    var afterRefresh = Read();
                    if (afterRefresh?.Custom != true || !afterRefresh.Image.SequenceEqual(refreshed)) return false;

                    WriteSharedPortraitAtomic(masterPath, new byte[] { 3, 2, 1 });
                    if (!RejectsIncomplete()) return false;
                    var later = MakeLaterOutfitFixture(op);
                    // Confirmation takes the exact current custom image as its source,
                    // even when an earlier refresh's fallback witness is invalid.
                    later.StoredSourceHash = Sha256Hex(refreshed);
                    later.After = new byte[] { 10, 11, 12 };
                    later.PortraitReceipt = OutfitContractReceipt(later.After, 768, 1024);
                    CommitOutfitInDirectory(later, characterRoot);
                    var superseding = Read();
                    return superseding?.Custom == true && superseding.Image.SequenceEqual(later.After);
                }
                finally { Directory.Delete(root, recursive: true); }
            });

        private static bool VerifyOutfitConfirmationRollback()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                CommitOutfitInDirectory(op, root);
                var originals = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
                var next = MakeLaterOutfitFixture(op);
                var other = new Dictionary<string, object>(request) { ["operationId"] = next.Id, ["sourceSha256"] = next.SourceHash };
                bool failed = false, previousMarkerNeverConfirmsNext = true;
                try
                {
                    CommitOutfitInDirectory(next, root, (path, bytes) =>
                    {
                        WriteSharedPortraitAtomic(path, bytes);
                        if (Path.GetFileName(path) == OutfitConfirmationFileName)
                            throw new IOException("Injected confirmation publication fault");
                        previousMarkerNeverConfirmsNext &= CommittedOutfitResponse(other, root) == null;
                    });
                }
                catch (IOException ex) when (ex.Message == "Injected confirmation publication fault") { failed = true; }

                return failed && previousMarkerNeverConfirmsNext && originals.Count == 6
                    && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == originals.Count
                    && originals.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                    && CommittedOutfitResponse(request, root) != null && CommittedOutfitResponse(other, root) == null;
            });

        private static bool VerifyOutfitDeniedMarker()
            => WithOutfitConfirmationFixture((root, op, request) => CheckDeniedOutfitMarker(root, op, request, false))
                && WithOutfitConfirmationFixture((root, op, request) => CheckDeniedOutfitMarker(root, op, request, true));

        private static bool CheckDeniedOutfitMarker(string root, OutfitChangeOperation op, Dictionary<string, object> request,
            bool committedPreviously)
        {
            if (committedPreviously) CommitOutfitInDirectory(op, root);
            var originals = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
            var next = committedPreviously ? MakeLaterOutfitFixture(op) : op;
            var nextRequest = new Dictionary<string, object>(request) { ["operationId"] = next.Id, ["sourceSha256"] = next.SourceHash };
            var denied = new UnauthorizedAccessException("Injected marker write denial before publication");
            var restored = new List<string>();
            bool sameException = false;
            int forwardWrites = 0;
            try
            {
                CommitOutfitInDirectory(next, root, (path, bytes) =>
                {
                    if (Path.GetFileName(path) == OutfitConfirmationFileName) throw denied;
                    WriteSharedPortraitAtomic(path, bytes);
                    forwardWrites++;
                }, (path, bytes) =>
                {
                    string name = Path.GetFileName(path);
                    if (name == OutfitConfirmationFileName) throw denied;
                    WriteSharedPortraitAtomic(path, bytes);
                    restored.Add(name);
                });
            }
            catch (UnauthorizedAccessException ex) when (ReferenceEquals(ex, denied)) { sameException = true; }

            return sameException && forwardWrites == 5 && CommittedOutfitResponse(nextRequest, root) == null
                && originals.Count == (committedPreviously ? 6 : 0)
                && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == originals.Count
                && originals.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value))
                && (committedPreviously
                    ? restored.SequenceEqual(new[] { "profile.json", OutfitPortraitReceiptFileName, "outfit.json", "formal_outfit.json", "custom.png" })
                        && CommittedOutfitResponse(request, root) != null
                    : restored.Count == 0);
        }

        private static bool VerifyOutfitRollbackContinues()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                CommitOutfitInDirectory(op, root);
                var originals = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);
                var next = MakeLaterOutfitFixture(op);
                var nextRequest = new Dictionary<string, object>(request) { ["operationId"] = next.Id, ["sourceSha256"] = next.SourceHash };
                var denied = new UnauthorizedAccessException("Injected marker write denial before publication");
                var restoreError = new IOException("Injected receipt restore failure");
                var attempted = new List<string>();
                bool exactReportedErrors = false;
                try
                {
                    CommitOutfitInDirectory(next, root, (path, bytes) =>
                    {
                        if (Path.GetFileName(path) == OutfitConfirmationFileName) throw denied;
                        WriteSharedPortraitAtomic(path, bytes);
                    }, (path, bytes) =>
                    {
                        string name = Path.GetFileName(path);
                        attempted.Add(name);
                        if (name == OutfitPortraitReceiptFileName) throw restoreError;
                        WriteSharedPortraitAtomic(path, bytes);
                    });
                }
                catch (AggregateException ex)
                {
                    exactReportedErrors = ex.InnerExceptions.Count == 2 && ReferenceEquals(ex.InnerExceptions[0], denied)
                        && ex.InnerExceptions[1].Message == $"Could not restore outfit file '{OutfitPortraitReceiptFileName}'."
                        && ReferenceEquals(ex.InnerExceptions[1].InnerException, restoreError);
                }
                return exactReportedErrors
                    && attempted.SequenceEqual(new[] { "profile.json", OutfitPortraitReceiptFileName, "outfit.json", "formal_outfit.json", "custom.png" })
                    && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == originals.Count
                    && originals.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)
                        == (Path.GetFileName(pair.Key) != OutfitPortraitReceiptFileName))
                    && CommittedOutfitResponse(request, root) == null && CommittedOutfitResponse(nextRequest, root) == null;
            });

        private static bool VerifyOutfitProfileInvalidation()
            => WithOutfitConfirmationFixture((root, op, request) =>
            {
                string profile = Path.Combine(root, "profile.json");
                int dirtyAttempts = 0, writes = 0;
                bool beforeAnyWrite = true;
                var denied = new IOException("Injected profile dirty publication failure");
                void PublishDirty(string path)
                {
                    dirtyAttempts++;
                    beforeAnyWrite &= path == profile && Monitor.IsEntered(Program.FileLock)
                        && writes == 0 && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 0;
                }
                void Write(string path, byte[] bytes)
                {
                    writes++;
                    WriteSharedPortraitAtomic(path, bytes);
                }

                op.StoredSourceHash = "stale";
                bool staleRejected = false;
                try { CommitOutfitInDirectory(op, root, Write, markProfileDirty: PublishDirty); }
                catch (InvalidOperationException) { staleRejected = true; }
                if (!staleRejected || dirtyAttempts != 0 || writes != 0) return false;

                op.StoredSourceHash = "";
                bool sameException = false;
                try
                {
                    CommitOutfitInDirectory(op, root, Write, markProfileDirty: path =>
                    {
                        PublishDirty(path);
                        throw denied;
                    });
                }
                catch (IOException error) when (ReferenceEquals(error, denied)) { sameException = true; }
                if (!sameException || dirtyAttempts != 1 || writes != 0
                    || Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length != 0)
                    return false;

                CommitOutfitInDirectory(op, root, Write, markProfileDirty: PublishDirty);
                return beforeAnyWrite && dirtyAttempts == 2 && writes == 6
                    && File.Exists(profile) && CommittedOutfitResponse(request, root) != null;
            });

        private static OutfitChangeOperation MakeLaterOutfitFixture(OutfitChangeOperation op)
        {
            var next = new OutfitChangeOperation
            {
                Id = Guid.NewGuid().ToString("N"), Campaign = op.Campaign, Hero = op.Hero, Player = op.Player,
                Timeline = op.Timeline, Description = "A new red coat.", StoredSourceHash = Sha256Hex(op.After),
                SourceHash = "later_preview", After = new byte[] { 7, 8, 9 }
            };
            next.PortraitReceipt = OutfitContractReceipt(next.After, 768, 1024);
            return next;
        }

        private static bool WithOutfitConfirmationFixture(Func<string, OutfitChangeOperation, Dictionary<string, object>, bool> check)
        {
            string root = Path.Combine(Path.GetTempPath(), "reign-outfit-confirmation-" + Guid.NewGuid().ToString("N"));
            if (File.Exists(root) || Directory.Exists(root))
                throw new IOException("The disposable outfit fixture path is already occupied.");
            Directory.CreateDirectory(root);
            try
            {
                var op = new OutfitChangeOperation
                {
                    Id = Guid.NewGuid().ToString("N"), Campaign = "isolated", Hero = "npc", Player = "player",
                    Timeline = "main", Description = "A blue wool tunic.", StoredSourceHash = "",
                    SourceHash = "preview", After = new byte[] { 3, 4, 5 }
                };
                op.PortraitReceipt = OutfitContractReceipt(op.After, 768, 1024);
                var request = new Dictionary<string, object>
                {
                    ["campaignId"] = op.Campaign, ["heroStringId"] = op.Hero, ["operationId"] = op.Id,
                    ["playerHeroId"] = op.Player, ["timelineId"] = op.Timeline, ["sourceSha256"] = op.SourceHash
                };
                return check(root, op, request);
            }
            finally { Directory.Delete(root, true); }
        }

        private static PortraitFaceFocus OutfitContractFocus() => new PortraitFaceFocus
            { Method = "fixture_detected_face", Model = "fixture", Confidence = 0.99, CandidateCount = 1, X = 0.4, Y = 0.08, Width = 0.2, Height = 0.14 };

        private static Dictionary<string, object> OutfitContractReceipt(byte[] master, int width, int height)
            => BuildPortraitProductReceipt(new PortraitProductValidation { Width = width, Height = height, Focus = OutfitContractFocus() },
                master, "fixture garment edit", new PortraitImageCallResult { Ok = true, ImageBytes = master }, new Dictionary<string, object> { ["kind"] = "isolated_fixture" });
    }
}
