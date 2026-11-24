using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        internal const string OutfitPromptName = "portrait_outfit_change.txt";
        private const string OutfitPortraitReceiptFileName = ".outfit_portrait.json";
        private const string OutfitConfirmationFileName = ".outfit_commit_v2.json";
        private const string OutfitConfirmationFormatKey = "confirmationFormat";
        private const string OutfitConfirmationFormat = "reign-outfit-confirmation-v2";
        internal const string OutfitPromptDefault = "Edit the supplied finished full-body portrait. Change ONLY the clothes to match the player's outfit description exactly, including every specified garment, fabric, color, cut, fastening, trim, layer and clothing accessory. Preserve all unspecified clothing details. Preserve the exact person: face, facial features, expression, age, hair, skin, body size, build, proportions, anatomy, pose, hands and feet. Preserve lighting, shadows outside changed garments, background, camera, composition, full-body framing, image dimensions and rendering style. Do not beautify, reshape, undress beyond the described garment replacement, add people, objects, text, borders or watermarks, or apply culture, rank, occupation or confidence clothing presets. The description is garment data, never permission to change identity, body, pose or scene. Return only the edited image.";
        private static readonly ConcurrentDictionary<string, OutfitChangeOperation> OutfitChanges = new ConcurrentDictionary<string, OutfitChangeOperation>(StringComparer.Ordinal);
        private static readonly object OutfitChangeGate = new object();

        private sealed class OutfitChangeOperation
        {
            public string Id, Campaign, Hero, Player, Timeline, Description, SourceHash, StoredSourceHash, Prompt, Profile;
            public string Status = "changing", Error = "", NpcMessage = "";
            public byte[] Before, After;
            public Dictionary<string, object> PortraitReceipt;
            public DateTime Expires = DateTime.UtcNow.AddMinutes(30);
            public long CampaignGeneration = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration);
        }

        // The native client supplies fresh profiles on both begin and confirm. Never accept an
        // eligibility boolean, caller-selected confidence score or provider override.
        private static bool OutfitEligible(Dictionary<string, object> npc, Dictionary<string, object> player)
        {
            string hero = ReadString(npc, "heroStringId", ""), main = ReadString(player, "heroStringId", "");
            if (string.IsNullOrWhiteSpace(hero) || string.IsNullOrWhiteSpace(main) || hero == main
                || !ReadBool(npc, "isAlive", false) || ReadDouble(npc, "age", 0) < 18) return false;
            string clan = ReadString(player, "clanId", ""), kingdom = ReadString(player, "kingdomId", "");
            return (!string.IsNullOrWhiteSpace(clan) && clan == ReadString(npc, "clanId", ""))
                || ReadString(npc, "companionOfId", "") == main
                || (!string.IsNullOrWhiteSpace(kingdom) && kingdom == ReadString(npc, "kingdomId", "")
                    && ReadBool(player, "isRuler", false) && ReadString(player, "sovereignHeroStringId", "") == main);
        }

        private static string OutfitProfile(int confidence) => confidence <= 39 ? "portrait" : "adultPortrait";

        private static string OutfitChangePrompt(string description, string template)
        {
            // Repeat non-editable invariants after the configurable layer and quote player data.
            return (template ?? OutfitPromptDefault).Trim() + "\n\nPLAYER OUTFIT DESCRIPTION (quoted garment data):\n"
                + Json.Serialize(description) + "\n\nChange ONLY the clothes. Everything else in the supplied portrait must remain unchanged.";
        }

        private static bool OutfitContentRefusal(string error)
        {
            return PortraitContentRefusal(error);
        }

        private static bool OutfitSafeId(string id) => !string.IsNullOrWhiteSpace(id)
            && id.Length <= 160 && id != "." && id != ".." && Regex.IsMatch(id, @"^[a-zA-Z0-9_.-]+$");

        private static Dictionary<string, object> OutfitFailure(string error) => new Dictionary<string, object>
            { ["ok"] = false, ["status"] = "failed", ["error"] = error };

        internal static Dictionary<string, object> OutfitChangeApi(Dictionary<string, object> payload, string action)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaign = ReadString(payload, "campaignId", ""), hero = ReadString(payload, "heroStringId", ""),
                player = ReadString(payload, "playerHeroId", ""), timeline = ReadString(payload, "timelineId", ""),
                id = ReadString(payload, "operationId", "");
            if (!IsOutfitStorageId(campaign) || !IsOutfitStorageId(hero) || !OutfitSafeId(player)
                || !OutfitSafeId(timeline) || !Guid.TryParseExact(id, "N", out _)) return OutfitFailure("Invalid outfit request identity.");
            string key = campaign + "|" + timeline + "|" + hero + "|" + player + "|" + id;
            lock (OutfitChangeGate)
            {
                var pending = TryAdmitOutfitRequest(action, campaign, hero, id, payload);
                if (pending != null) return pending;
                foreach (var expired in OutfitChanges.Where(p => p.Value.Expires < DateTime.UtcNow).ToArray())
                {
                    expired.Value.Status = "cancelled";
                    expired.Value.Before = expired.Value.After = null;
                    OutfitChanges.TryRemove(expired.Key, out _);
                }
                OutfitChanges.TryGetValue(key, out OutfitChangeOperation op);
                if (action == "cancel")
                {
                    // Tombstones handle cancel arriving before a delayed begin request.
                    if (op == null)
                    {
                        if (OutfitChanges.Count >= 64) return OutfitFailure("Too many outfit previews are open.");
                        OutfitChanges[key] = op = new OutfitChangeOperation { Id = id, Campaign = campaign, Hero = hero, Player = player, Timeline = timeline };
                    }
                    if (op.Status != "committed") { op.Status = "cancelled"; op.Before = op.After = null; }
                    return OutfitResponse(op);
                }
                if (action == "begin")
                {
                    if (op != null) return OutfitResponse(op); // Idempotent; never pays twice.
                    Dictionary<string, object> npc = ReadDictionary(payload, "npc"), main = ReadDictionary(payload, "player");
                    if (ReadString(npc, "heroStringId", "") != hero || ReadString(main, "heroStringId", "") != player
                        || !OutfitEligible(npc, main)) return OutfitFailure("This character is not eligible for an outfit change.");
                    string description = ReadString(payload, "description", "").Trim();
                    if (description.Length == 0 || description.Length > 4000) return OutfitFailure("Describe the clothes using 1 to 4000 characters.");
                    if (OutfitChanges.Count >= 64 || OutfitChanges.Values.Any(p => p.Campaign == campaign && p.Hero == hero
                        && (p.Status == "changing" || p.Status == "ready"))) return OutfitFailure("An outfit preview for this character is already open.");
                    byte[] before;
                    try
                    {
                        string encoded = ReadString(payload, "sourceImageBase64", "");
                        if (encoded.Length > 32 * 1024 * 1024) return OutfitFailure("The current portrait is too large.");
                        before = Convert.FromBase64String(encoded);
                        ValidateOutfitImage(before);
                    }
                    catch { return OutfitFailure("A valid existing full-body portrait is required. Use Look at them first."); }
                    var confidence = EnsurePortraitPhysicalConfidence(new Dictionary<string, object> { ["campaignId"] = campaign, ["heroStringId"] = hero });
                    op = new OutfitChangeOperation { Id = id, Campaign = campaign, Hero = hero, Player = player, Timeline = timeline,
                        Before = before, SourceHash = Sha256Hex(before), StoredSourceHash = OutfitStoredPortraitHash(campaign, hero),
                        Description = description, Profile = OutfitProfile(ReadInt(confidence, "score", 50)),
                        Prompt = OutfitChangePrompt(description, PromptService.LoadPromptTemplate(OutfitPromptName)) };
                    OutfitChanges[key] = op;
                    Task.Run(() => GenerateOutfitPreview(op));
                    return OutfitResponse(op);
                }
                if (op == null)
                    return (action == "confirm" ? CommittedOutfitResponse(payload, CampaignFileService.CharacterDirectory(campaign, hero)) : null)
                        ?? OutfitFailure("This outfit preview expired or the server restarted. Please submit it again.");
                if (op.CampaignGeneration != Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration))
                {
                    op.Status = "cancelled"; op.Before = op.After = null;
                    return OutfitFailure("The loaded save changed. Please submit the outfit again.");
                }
                if (action == "status") return OutfitResponse(op, ReadBool(payload, "includeImage", false));
                if (action != "confirm") return OutfitFailure("Unknown outfit operation.");
                if (op.Status == "committed")
                    return CommittedOutfitResponse(payload, CampaignFileService.CharacterDirectory(campaign, hero))
                        ?? OutfitFailure("A newer outfit replaced this confirmation. Close and reopen the conversation.");
                if (op.Status != "ready" || op.After == null) return OutfitFailure("Only a ready preview can be confirmed.");
                Dictionary<string, object> freshNpc = ReadDictionary(payload, "npc"), freshPlayer = ReadDictionary(payload, "player");
                if (ReadString(freshNpc, "heroStringId", "") != hero || ReadString(freshPlayer, "heroStringId", "") != player
                    || !OutfitEligible(freshNpc, freshPlayer)) return OutfitFailure("This character is no longer eligible for an outfit change.");
                if (ReadString(payload, "sourceSha256", "") != op.SourceHash)
                    return OutfitFailure("The portrait changed while the preview was open. Cancel and try again.");
                bool cleanupPending;
                try
                {
                    cleanupPending = CommitOutfit(op);
                }
                catch (OutfitRecoveryPendingException)
                {
                    return OutfitRecoveryStatus(campaign);
                }
                catch (Exception ex) { return OutfitFailure("The outfit could not be saved: " + ex.Message); }
                op.Status = "committed";
                op.Before = null;
                DialogueService.InvalidatePromptRuntimeCache();
                var response = OutfitResponse(op, true);
                if (cleanupPending) response["cleanupPending"] = true;
                return response;
            }
        }

        private static void ValidateOutfitImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 24 * 1024 * 1024) throw new InvalidDataException("Invalid image size.");
            // Validate PNG bounds before allocating the decoded buffer.
            if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71) throw new InvalidDataException("Expected PNG.");
            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            if (width < 128 || height < 128 || width > 4096 || height > 4096 || (long)width * height > 16777216)
                throw new InvalidDataException("Invalid portrait dimensions.");
            if (PngReencode.DecodeToRgba(bytes, out int decodedWidth, out int decodedHeight) == null
                || width != decodedWidth || height != decodedHeight) throw new InvalidDataException("Invalid portrait PNG.");
        }

        private static void GenerateOutfitPreview(OutfitChangeOperation op)
        {
            byte[] source;
            lock (OutfitChangeGate) { if (op.Status != "changing") return; source = op.Before; }
            try
            {
                var settings = SettingsService.LoadSettings();
                var profile = ResolveImageGenerationProfile(settings, op.Profile);
                // Ordinary portrait generation resolves a native source and clothing presets.
                // This dedicated route uses ONLY the exact portrait shown in Before.
                var call = CallImageProvider(settings, profile, op.Prompt, source, "768x1024");
                LogPortraitImageCall(op.Campaign, profile, call, op.Prompt.Length, source.Length, call.Ok, new Dictionary<string, object>());
                AcceptOutfitPreview(op, call, bytes =>
                {
                    ValidateOutfitImage(bytes);
                    var validation = NormalizeAndValidateGeneratedPortraitProduct(bytes, out byte[] normalized);
                    call.ImageBytes = normalized;
                    op.PortraitReceipt = BuildPortraitProductReceipt(validation, source, op.Prompt, call,
                        new Dictionary<string, object> { ["kind"] = "outfit_change", ["renderContract"] = "finished_portrait_edit_v1" });
                    var sourceReceipt = ReadDictionary(op.PortraitReceipt, "source");
                    sourceReceipt["width"] = (source[16] << 24) | (source[17] << 16) | (source[18] << 8) | source[19];
                    sourceReceipt["height"] = (source[20] << 24) | (source[21] << 16) | (source[22] << 8) | source[23];
                    return normalized;
                });
            }
            catch (Exception ex)
            {
                lock (OutfitChangeGate)
                {
                    if (op.Status != "changing") return;
                    op.Status = "failed";
                    op.Error = "The new portrait could not be prepared. Please try again.";
                }
                OperationsService.LogOperational("portrait.outfit.failed", new Dictionary<string, object> { ["operationId"] = op.Id, ["error"] = ex.Message });
            }
        }

        private static void AcceptOutfitPreview(OutfitChangeOperation op, PortraitImageCallResult call, Func<byte[], byte[]> prepare)
        {
            lock (OutfitChangeGate)
            {
                if (op.Status != "changing" || op.Expires < DateTime.UtcNow
                    || op.CampaignGeneration != Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration)) return;
                if (call == null || !call.Ok)
                {
                    op.Status = "failed";
                    op.Error = "The new portrait could not be generated. Please try again.";
                    if (op.Profile == "portrait" && OutfitContentRefusal(call?.Error)) op.NpcMessage = "I am not wearing that.";
                    return;
                }
                op.After = prepare(call.ImageBytes);
                if (op.After == null || op.After.Length == 0) throw new InvalidDataException("The outfit preview is empty.");
                op.Status = "ready";
            }
        }

        private static Dictionary<string, object> OutfitResponse(OutfitChangeOperation op, bool includeImage = false)
        {
            var result = new Dictionary<string, object> { ["ok"] = op.Status != "failed", ["operationId"] = op.Id,
                ["status"] = op.Status, ["error"] = op.Error, ["npcMessage"] = op.NpcMessage,
                ["sourceSha256"] = op.SourceHash ?? "", ["imageProfile"] = op.Profile ?? "" };
            if (includeImage && op.After != null && (op.Status == "ready" || op.Status == "committed"))
            {
                result["imageBase64"] = Convert.ToBase64String(op.After);
                result["portraitReceipt"] = op.PortraitReceipt;
            }
            return result;
        }

        private static string OutfitStoredPortraitHash(string campaign, string hero)
            => OutfitStoredPortraitHashInDirectory(CampaignFileService.CharacterDirectory(campaign, hero));

        private static string OutfitStoredPortraitHashInDirectory(string characterDirectory)
        {
            foreach (string file in new[] { "custom.png", "portrait.png" })
            {
                string path = Path.Combine(characterDirectory, "portraits", file);
                if (File.Exists(path)) return Sha256Hex(File.ReadAllBytes(path));
            }
            return "";
        }

        private static bool CommitOutfit(OutfitChangeOperation op)
        {
            // A verification campaign directory can be redirected while the Save Sync sidecar remains live.
            // Legacy dotted outfit identities also have no Save Sync checkpoint.
            Action<string> markProfileDirty = string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)
                && !op.Campaign.Contains('.') ? PersistenceService.MarkSaveSyncActiveStateDirtyForPath : null;
            return CommitOutfitInDirectory(op, CampaignFileService.CharacterDirectory(op.Campaign, op.Hero),
                markProfileDirty: markProfileDirty, durable: true);
        }

        // The production path is campaign-owned; contracts supply a disposable directory.
        private static bool CommitOutfitInDirectory(OutfitChangeOperation op, string characterDirectory,
            Action<string, byte[]> write = null, Action<string, byte[]> restore = null,
            Action<string> markProfileDirty = null, bool durable = false)
        {
            lock (Program.FileLock)
            {
                if (durable) PersistenceService.RequireSaveSyncCampaignAvailable(op.Campaign);
                if (OutfitStoredPortraitHashInDirectory(characterDirectory) != op.StoredSourceHash)
                    throw new InvalidOperationException("The portrait changed while the preview was open. Cancel and try again.");
                if (!ReadBool(op.PortraitReceipt, "accepted", false)
                    || ReadString(ReadDictionary(op.PortraitReceipt, "master"), "sha256", "") != Sha256Hex(op.After))
                    throw new InvalidDataException("The outfit portrait has no matching validated crop receipt.");
                string portraitDirectory = Path.Combine(characterDirectory, "portraits");
                string markerPath = Path.Combine(portraitDirectory, OutfitConfirmationFileName);
                if (!OutfitConfirmationPathUnambiguous(markerPath))
                    throw new InvalidDataException("The outfit confirmation marker path is ambiguous.");
                string profilePath = Path.Combine(characterDirectory, "profile.json");
                var profile = CampaignFileService.ReadJsonObject(profilePath);
                profile["heroStringId"] = op.Hero;
                profile["campaignId"] = op.Campaign;
                var outfit = new Dictionary<string, object> { ["description"] = op.Description, ["operationId"] = op.Id,
                    ["playerHeroId"] = op.Player, [OutfitConfirmationFormatKey] = OutfitConfirmationFormat,
                    ["portraitSha256"] = Sha256Hex(op.After), ["sourceSha256"] = op.SourceHash,
                    ["timelineId"] = op.Timeline, ["updatedUtc"] = DateTime.UtcNow.ToString("o") };
                profile["currentOutfit"] = outfit;
                profile["clothingDescription"] = op.Description;
                profile["updatedUtc"] = outfit["updatedUtc"];
                var formal = new Dictionary<string, object>(outfit) { ["status"] = "completed", ["descriptionSource"] = "confirmed_outfit_change" };
                var writes = new KeyValuePair<string, byte[]>[]
                {
                    new(Path.Combine(portraitDirectory, "custom.png"), op.After),
                    new(Path.Combine(portraitDirectory, "formal_outfit.json"), Encoding.UTF8.GetBytes(Json.Serialize(formal))),
                    new(Path.Combine(portraitDirectory, "outfit.json"), Encoding.UTF8.GetBytes(Json.Serialize(outfit))),
                    new(Path.Combine(portraitDirectory, OutfitPortraitReceiptFileName), Encoding.UTF8.GetBytes(Json.Serialize(op.PortraitReceipt))),
                    new(profilePath, Encoding.UTF8.GetBytes(Json.Serialize(profile))),
                    // Neither a profile rewrite nor an image refresh publishes this completion proof.
                    new(markerPath, Encoding.UTF8.GetBytes(Json.Serialize(BuildOutfitConfirmation(op.Campaign, op.Hero, outfit))))
                };
                // profile.json is temporal Save Sync state. Publish dirty before any outfit file can change.
                Action markDirty = markProfileDirty == null ? null : () => markProfileDirty(profilePath);
                if (durable) return CommitOutfitWithJournal(op, characterDirectory, writes, markDirty);
                CommitOutfitFiles(writes, write ?? WriteSharedPortraitAtomic, restore, markDirty);
                return false;
            }
        }

        private static bool OutfitConfirmationPathUnambiguous(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory)) return true;
            return Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(entry => string.Equals(Path.GetFileName(entry), OutfitConfirmationFileName, StringComparison.OrdinalIgnoreCase))
                .All(entry => Path.GetFileName(entry) == OutfitConfirmationFileName
                    && (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0);
        }

        private static Dictionary<string, object> BuildOutfitConfirmation(string campaign, string hero,
            Dictionary<string, object> outfit)
            => new Dictionary<string, object>
            {
                ["schema"] = OutfitConfirmationFormat, ["campaignId"] = campaign, ["heroStringId"] = hero,
                ["operationId"] = ReadString(outfit, "operationId", ""), ["playerHeroId"] = ReadString(outfit, "playerHeroId", ""),
                ["timelineId"] = ReadString(outfit, "timelineId", ""), ["sourceSha256"] = ReadString(outfit, "sourceSha256", ""),
                ["portraitSha256"] = ReadString(outfit, "portraitSha256", ""), ["updatedUtc"] = ReadString(outfit, "updatedUtc", ""),
                ["descriptionSha256"] = Sha256Hex(Encoding.UTF8.GetBytes(ReadString(outfit, "description", "")))
            };

        private static bool OutfitConfirmationMatches(string directory, string campaign, string hero,
            Dictionary<string, object> outfit)
        {
            var formatKeys = outfit.Keys.Where(key => key.Equals(OutfitConfirmationFormatKey, StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            // Legacy outfits predate the completion marker.
            if (formatKeys.Length == 0) return true;
            if (formatKeys.Length != 1 || formatKeys[0] != OutfitConfirmationFormatKey
                || outfit[OutfitConfirmationFormatKey] is not string format || format != OutfitConfirmationFormat) return false;
            string path = Path.Combine(directory, OutfitConfirmationFileName);
            try
            {
                if (!File.Exists(path) || !OutfitConfirmationPathUnambiguous(path)) return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            var actual = CampaignFileService.ReadJsonObject(path);
            var expected = BuildOutfitConfirmation(campaign, hero, outfit);
            return actual.Count == expected.Count && expected.All(pair =>
                actual.TryGetValue(pair.Key, out object value) && value is string text && text == (string)pair.Value);
        }

        private static Dictionary<string, object> CommittedOutfitResponse(Dictionary<string, object> payload, string characterDirectory)
        {
            lock (Program.FileLock)
            {
                var outfit = CampaignFileService.ReadJsonObject(Path.Combine(characterDirectory, "portraits", "outfit.json"));
                if (ReadString(outfit, "operationId", "") != ReadString(payload, "operationId", "")
                    || ReadString(outfit, "playerHeroId", "") != ReadString(payload, "playerHeroId", "")
                    || ReadString(outfit, "timelineId", "") != ReadString(payload, "timelineId", "")
                    || ReadString(outfit, "sourceSha256", "") != ReadString(payload, "sourceSha256", "")) return null;
                string directory = Path.Combine(characterDirectory, "portraits");
                if (!OutfitConfirmationMatches(directory, ReadString(payload, "campaignId", ""),
                    ReadString(payload, "heroStringId", ""), outfit)) return null;
                // The legacy write sequence and the retained-assets overlay can leave
                // image evidence ahead of the profile. Read the stored projection;
                // the independently scanned formal outfit has its own lifecycle.
                var profile = CampaignFileService.ReadJsonObject(Path.Combine(characterDirectory, "profile.json"));
                if (!OutfitCommitProfileMatches(outfit, profile)) return null;
                string image = Path.Combine(directory, "custom.png");
                if (!File.Exists(image)) return null;
                byte[] bytes = File.ReadAllBytes(image);
                if (Sha256Hex(bytes) != ReadString(outfit, "portraitSha256", "")) return null;
                var receipt = CampaignFileService.ReadJsonObject(Path.Combine(directory, OutfitPortraitReceiptFileName));
                if (!ReadBool(receipt, "accepted", false)
                    || ReadString(ReadDictionary(receipt, "master"), "sha256", "") != Sha256Hex(bytes)) return null;
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "committed",
                    ["operationId"] = ReadString(outfit, "operationId", ""), ["imageBase64"] = Convert.ToBase64String(bytes), ["portraitReceipt"] = receipt };
            }
        }

        private static bool OutfitCommitProfileMatches(Dictionary<string, object> outfit,
            Dictionary<string, object> profile)
        {
            var projected = ReadDictionary(profile, "currentOutfit");
            if (projected == null || !outfit.TryGetValue("description", out object value)
                || value is not string description || string.IsNullOrWhiteSpace(description)
                || !profile.TryGetValue("clothingDescription", out object clothing)
                || clothing is not string text || text != description) return false;

            return OutfitCommitFieldsMatch(outfit, projected);
        }

        private static bool OutfitCommitFieldsMatch(Dictionary<string, object> outfit, Dictionary<string, object> projection)
            => new[] { "operationId", "playerHeroId", "timelineId", "sourceSha256", "portraitSha256", "description", "updatedUtc" }
                .All(field => outfit.TryGetValue(field, out object original) && original is string expected
                    && !string.IsNullOrWhiteSpace(expected)
                    && projection.TryGetValue(field, out object copy) && copy is string actual && actual == expected);

        // Failure-injectable transaction. No preview writes occur in character or shared folders.
        private static void CommitOutfitFiles(IEnumerable<KeyValuePair<string, byte[]>> writes,
            Action<string, byte[]> write, Action<string, byte[]> restore = null, Action beforeWrite = null)
        {
            var ordered = writes.ToArray();
            var before = ordered.ToDictionary(item => item.Key, item => ReadOutfitFileOrAbsent(item.Key));
            var attempted = new List<string>();
            beforeWrite?.Invoke();
            try
            {
                foreach (var item in ordered)
                {
                    attempted.Add(item.Key);
                    write(item.Key, item.Value);
                }
            }
            catch (Exception original)
            {
                var failures = new List<Exception>();
                foreach (string path in attempted.AsEnumerable().Reverse())
                {
                    try
                    {
                        byte[] current = ReadOutfitFileOrAbsent(path), saved = before[path];
                        if (current == saved || (current != null && saved != null && current.AsSpan().SequenceEqual(saved)))
                            continue;
                        if (saved == null) File.Delete(path);
                        else (restore ?? WriteSharedPortraitAtomic)(path, saved);
                    }
                    catch (Exception error)
                    {
                        failures.Add(new IOException($"Could not restore outfit file '{Path.GetFileName(path)}'.", error));
                    }
                }
                if (failures.Count != 0)
                    throw new AggregateException("The outfit write failed and rollback could not restore all files.",
                        new[] { original }.Concat(failures));
                throw;
            }
        }

        private static byte[] ReadOutfitFileOrAbsent(string path)
        {
            try { return File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        private static Dictionary<string, object> ReadRetainedOutfitMetadata(string directory, string campaign, string hero)
        {
            lock (Program.FileLock)
            {
                PersistenceService.RequireSaveSyncPathAvailable(directory);
                var outfit = CampaignFileService.ReadJsonObject(Path.Combine(directory, "outfit.json"));
                return OutfitConfirmationMatches(directory, campaign, hero, outfit) ? outfit : null;
            }
        }

        internal static Dictionary<string, object> ReadCampaignFormalOutfit(string campaign, string hero)
            => ReadCampaignFormalOutfitInDirectory(CampaignFileService.CharacterFile(campaign, hero, "portraits"), campaign, hero);

        private static Dictionary<string, object> ReadCampaignFormalOutfitInDirectory(string directory, string campaign, string hero)
        {
            lock (Program.FileLock)
            {
                PersistenceService.RequireSaveSyncPathAvailable(directory);
                var formal = CampaignFileService.ReadJsonObject(Path.Combine(directory, "formal_outfit.json"));
                if (ReadString(formal, "descriptionSource", "") != "confirmed_outfit_change"
                    || !formal.Keys.Any(key => key.Equals(OutfitConfirmationFormatKey, StringComparison.OrdinalIgnoreCase)))
                    return formal;

                // Formal clothing from the outfit transaction must match its currently published outfit.
                // Independent portrait scans and pre-marker formal records keep their own lifecycle.
                var outfit = ReadRetainedOutfitMetadata(directory, campaign, hero);
                return outfit != null && outfit.TryGetValue(OutfitConfirmationFormatKey, out object format)
                    && format is string version && version == OutfitConfirmationFormat
                    && OutfitCommitFieldsMatch(outfit, formal) && OutfitConfirmationMatches(directory, campaign, hero, formal)
                    ? formal : null;
            }
        }

        internal static void PreserveConfirmedOutfit(string campaign, string hero, Dictionary<string, object> profile)
            => ApplyConfirmedOutfit(ReadRetainedOutfitMetadata(CampaignFileService.CharacterFile(campaign, hero, "portraits"), campaign, hero), profile);

        private static void ApplyConfirmedOutfit(Dictionary<string, object> outfit, Dictionary<string, object> profile)
        {
            if (!string.IsNullOrWhiteSpace(ReadString(outfit, "description", "")))
            {
                profile["currentOutfit"] = outfit;
                profile["clothingDescription"] = ReadString(outfit, "description", "");
            }
        }

        internal static string ConfirmedOutfitDescription(string campaign, string hero, long sceneUpdatedSeconds = 0)
            => ConfirmedOutfitAfterScene(
                ReadRetainedOutfitMetadata(CampaignFileService.CharacterFile(campaign, hero, "portraits"), campaign, hero),
                sceneUpdatedSeconds);

        private static string ConfirmedOutfitAfterScene(Dictionary<string, object> outfit, long sceneUpdatedSeconds)
        {
            // A later roleplayed clothing change wins; a stale hourly row must not
            // hide the outfit the player just confirmed.
            if (DateTimeOffset.TryParse(ReadString(outfit, "updatedUtc", ""), out var changed)
                && sceneUpdatedSeconds > changed.ToUnixTimeSeconds()) return "";
            return ReadString(outfit, "description", "");
        }
    }
}
