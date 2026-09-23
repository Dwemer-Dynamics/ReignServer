using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using AIPortraits;
using ReignPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string FamilyStableId(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 24);
        }
        internal static bool FamilyChildhoodRetainedAtAdulthood(double experiencedAge) => experiencedAge >= 5d;
        internal static bool FamilySceneParticipantCountValid(int count) => count >= 1 && count <= 4;
        internal static string FamilyImageSafetyContract(int participantCount, string timeBlock)
        {
            return "Create one cinematic, grounded medieval household interior at " + FirstNonEmpty(timeBlock, "daytime") + " in a realistic Bannerlord visual style, composed as a widescreen scene.\n"
                + "The camera is the ruler's first-person viewpoint. The ruler is not visible. Show exactly " + FamilyNumberWords(participantCount) + " human figure" + (participantCount == 1 ? "" : "s") + " total, no more and no fewer.\n"
                + "Absolute exclusions: no additional people anywhere, no background figures, no duplicate people, no horses or other animals, and no person-shaped paintings or statues.\n"
                + "Do not render any words, letters, numbers, captions, labels, signs, watermarks, borders, or interface elements.\n"
                + "Place the people in distinct, non-overlapping left-to-right zones. Every person's face must be clearly visible, unobstructed, and the only face in that person's zone.\n";
        }

        private static string FamilyNumberWords(int value)
        {
            string[] small = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };
            if (value >= 0 && value < small.Length) return small[value];
            string[] tens = { "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety" };
            if (value >= 20 && value < 100) return tens[value / 10] + (value % 10 == 0 ? "" : "-" + small[value % 10]);
            return "adult";
        }

        private static string FamilyAgeWords(double age)
        {
            int years = Math.Max(0, (int)Math.Floor(age));
            int months = Math.Max(0, Math.Min(11, (int)Math.Round((age - years) * 12d)));
            if (months == 12) { years++; months = 0; }
            return FamilyNumberWords(years) + (years == 1 ? " year" : " years")
                + (months == 0 ? " old" : " and " + FamilyNumberWords(months) + (months == 1 ? " month" : " months") + " old");
        }

        private static string FamilyPositionWords(int index, int count)
        {
            if (count == 1) return "center";
            if (count == 2) return index == 0 ? "left" : "right";
            if (count == 3) return index == 0 ? "left" : index == 1 ? "center" : "right";
            return index == 0 ? "far-left" : index == 1 ? "left-center" : index == 2 ? "right-center" : "far-right";
        }

        private static string FamilyOrdinalWords(int index)
        {
            string[] words = { "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth" };
            return index >= 0 && index < words.Length ? words[index] : "next";
        }

        private static string FamilyAppearanceWords(Dictionary<string, object> profile)
        {
            Dictionary<string, object> visual = ReadDictionary(profile, "familyVisualAppearance");
            string ancestry = FirstNonEmpty(ReadString(visual, "ancestry", ""), "Calradian");
            string race = FirstNonEmpty(ReadString(visual, "nativeRace", ""), "human");
            string hair = FirstNonEmpty(ReadString(visual, "hairColor", ""), "naturally colored");
            string skin = FirstNonEmpty(ReadString(visual, "skinTone", ""), "natural");
            string eyes = FirstNonEmpty(ReadString(visual, "eyeColor", ""), "natural");
            return ancestry + " " + race + " ancestry, " + hair + " hair, " + skin + " skin, and " + eyes + " eyes";
        }
        private static void EnsureFamilyChambersSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS childhood_experiences (
experience_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,
session_id TEXT NOT NULL,world_day REAL NOT NULL DEFAULT 0,experienced_age REAL NOT NULL DEFAULT 0,
summary TEXT NOT NULL DEFAULT '',participants_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL DEFAULT 'active_child',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_childhood_experiences_hero ON childhood_experiences(campaign_id,timeline_id,hero_id,world_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS childhood_maturity_receipts (
receipt_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,
retained_count INTEGER NOT NULL DEFAULT 0,archived_count INTEGER NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,hero_id));");
        }

        private static Dictionary<string, object> FamilyChambersChildRespond(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            Dictionary<string, object> speaker = ReadDictionary(payload, "speaker");
            string heroId = CharacterIdFrom(speaker);
            ApplyPlayerInputAudienceToPayload(payload, heroId);
            string name = ReadString(speaker, "name", heroId);
            double age = ReadDouble(speaker, "age", 0d);
            string sessionId = FirstNonEmpty(ReadString(payload, "conversationSessionId", ""), ReadString(payload, "eventId", "family_chambers"));
            string turnId = ReadString(payload, "turnId", Guid.NewGuid().ToString("N"));
            List<Dictionary<string, object>> prior;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureFamilyChambersSchema(connection);
                prior = QuerySql(connection, @"SELECT summary,experienced_age,world_day FROM childhood_experiences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active_child'
ORDER BY world_day DESC LIMIT 12;", new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["hero"] = heroId });
            }
            var prompt = new StringBuilder();
            using (ReignDbConnection attentionConnection = OpenCampaignConnection(campaignId))
                prompt.AppendLine(BuildFamilyAttentionContext(attentionConnection, campaignId, timelineId, heroId,
                    ReadFirstString(payload, "playerHeroStringId", "playerId"), ReadDouble(payload, "worldDay", 0d)));
            prompt.AppendLine("Write only valid JSON with reply, emotion, intent, and memorySummary.");
            prompt.AppendLine("You portray one child in a safe, grounded Bannerlord family scene. Never apply adult traits, reputation, romance, sexuality, marriage, pregnancy, politics, coercion, punishment, combat, or campaign actions.");
            prompt.AppendLine("Match development exactly: infants may babble or react nonverbally; toddlers use very short speech; older children may play, study, ask questions, or converse naturally.");
            prompt.AppendLine("Do not speak for the player or another participant. The child may remember every listed childhood experience while still a child.");
            prompt.AppendLine("Player spans inside single asterisks (*...*) describe actions, never words the player spoke. Speech outside those spans is audible unless the immediately preceding action says the player whispers to a named recipient. Only that recipient hears the words; others may notice a whisper but cannot know its content. Keep spoken reply paragraphs separate from *visible action paragraphs* with a blank line.");
            prompt.AppendLine("Child: " + name + "; exact age: " + age.ToString("0.00", CultureInfo.InvariantCulture)
                + "; culture: " + ReadString(speaker, "cultureId", "") + "; mother: " + ReadString(speaker, "motherId", "") + "; father: " + ReadString(speaker, "fatherId", ""));
            prompt.AppendLine("Time-aware scene contract: " + ReadString(payload, "castleDialoguePrompt", ""));
            prompt.AppendLine("Latest player text (empty means the ruler has just arrived): " + ReadString(payload, "playerText", ""));
            prompt.AppendLine("Attributed group transcript: " + Json.Serialize(ReadDictionaryList(payload, "groupTranscript")));
            prompt.AppendLine("This child's retained experiences: " + Json.Serialize(prior));
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["requestType"] = "family_chambers_child", ["campaignId"] = campaignId,
                ["correlationId"] = EnsureCorrelationId(payload),
                ["messages"] = BuildSimplePromptEnvelope("family_chambers_child", "respond",
                    "You portray a child safely and return only valid JSON.", prompt.ToString()).Messages,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            Dictionary<string, object> llm = ChatWithLlm(request);
            if (!ReadBool(llm, "ok", false)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = ReadString(llm, "error", "Child response failed.") };
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", "")) ?? new Dictionary<string, object>();
            string reply = LimitText(SanitizeVisibleReply(ReadString(parsed, "reply", "")), 1200);
            if (string.IsNullOrWhiteSpace(reply)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Child response contained no dialogue." };
            if (ReadBool(payload, "familyVisitDocket", false) && Reign.Core.Contracts.Court.ReignRulerDocketRules.ContainsForbiddenNobleDocketStatistics(reply))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The child returned an invalid court response. The audience can be retried." };
            string memory = LimitText(FirstNonEmpty(ReadString(parsed, "memorySummary", ""), name + " was present in the Family Chambers: " + reply), 900);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string experienceId = "childhood_" + FamilyStableId(campaignId + "|" + timelineId + "|" + heroId + "|" + sessionId + "|" + turnId);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureFamilyChambersSchema(connection);
                ExecuteSql(connection, @"INSERT INTO childhood_experiences(experience_id,campaign_id,timeline_id,hero_id,session_id,world_day,experienced_age,summary,participants_json,status,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$hero,$session,$day,$age,$summary,$participants,'active_child',$ts,$ts)
ON CONFLICT(experience_id) DO UPDATE SET summary=$summary,updated_ts=$ts;", new Dictionary<string, object>
                {
                    ["id"] = experienceId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["hero"] = heroId,
                    ["session"] = sessionId, ["day"] = ReadDouble(payload, "worldDay", 0d), ["age"] = age,
                    ["summary"] = memory, ["participants"] = Json.Serialize(ReadStringList(payload, "participants")), ["ts"] = ts
                });
            }
            var childResponse = new Dictionary<string, object>
            {
                ["ok"] = true, ["mode"] = "family_chambers", ["reply"] = reply,
                ["privateAudienceHeroStringId"] = ReadBool(payload, "privatePlayerInputForSpeaker", false) ? heroId : "",
                ["emotion"] = LimitText(ReadString(parsed, "emotion", "engaged"), 40),
                ["intent"] = LimitText(ReadString(parsed, "intent", "participate"), 80),
                ["participation"] = "speak", ["relationshipSignal"] = "neutral",
                ["relationshipAssessments"] = new List<object>(), ["queuedActions"] = new List<object>(),
                ["memoryWrites"] = new List<object>(),
                ["conversationExchange"] = new Dictionary<string, object> { ["sessionId"] = sessionId, ["exchangeId"] = turnId },
                ["childhoodExperienceId"] = experienceId
            };
            ProcessFamilyReconciliationReply(payload, childResponse);
            return childResponse;
        }

        private static Dictionary<string, object> FamilyChambersMatureChildhood(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            Dictionary<string, object> hero = ReadDictionary(payload, "hero");
            string heroId = CharacterIdFrom(hero);
            if (string.IsNullOrWhiteSpace(heroId) || ReadBool(hero, "isChild", true))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "An adult hero profile is required." };
            UpsertCharacterProfile(campaignId, hero);
            Dictionary<string, object> construction = EnsureCharacterConstructed(campaignId, heroId, hero, "childhood_maturity");
            if (!IsCharacterConstructionReady(construction))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Normal adult character construction is not ready.", ["construction"] = construction };
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureFamilyChambersSchema(connection);
                MigrateFamilyChildAttentionRelation(connection, campaignId, timelineId, heroId, ReadDouble(payload, "worldDay", 0d));
                Dictionary<string, object> receipt = QuerySql(connection, "SELECT * FROM childhood_maturity_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero LIMIT 1;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["hero"] = heroId }).FirstOrDefault();
                if (receipt != null) return new Dictionary<string, object> { ["ok"] = true, ["idempotent"] = true, ["retainedCount"] = ReadInt(receipt, "retained_count", 0), ["archivedCount"] = ReadInt(receipt, "archived_count", 0) };
                List<Dictionary<string, object>> rows = QuerySql(connection, "SELECT * FROM childhood_experiences WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active_child' ORDER BY world_day;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["hero"] = heroId });
                int retained = rows.Count(row => FamilyChildhoodRetainedAtAdulthood(ReadDouble(row, "experienced_age", 0d)));
                int archived = rows.Count - retained;
                foreach (Dictionary<string, object> row in rows.Where(row => FamilyChildhoodRetainedAtAdulthood(ReadDouble(row, "experienced_age", 0d))))
                {
                    string experienceId = ReadString(row, "experience_id", "");
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO summaries(summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,participants_json,known_by_json,visibility,importance,confidence,tags_json,status,source,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$hero,'middle_term',$summary,$events,$ts,$ts,1,$participants,$known,'private',0.65,1.0,$tags,'active','childhood_migration','pending',$ts,$payload,'personal_state');",
                        new Dictionary<string, object> { ["id"] = "childhood_memory_" + experienceId, ["scope"] = "childhood:" + heroId,
                            ["hero"] = heroId, ["summary"] = ReadString(row, "summary", ""), ["events"] = Json.Serialize(new[] { experienceId }),
                            ["participants"] = ReadString(row, "participants_json", "[]"), ["known"] = Json.Serialize(new[] { heroId }),
                            ["tags"] = Json.Serialize(new[] { "childhood_memory", "retained_at_adulthood" }), ["ts"] = ts,
                            ["payload"] = Json.Serialize(new Dictionary<string, object> { ["experiencedAge"] = ReadDouble(row, "experienced_age", 0d), ["sourceExperienceId"] = experienceId }) });
                }
                ExecuteSql(connection, "UPDATE childhood_experiences SET status=CASE WHEN experienced_age>=5 THEN 'adult_retained' ELSE 'adult_archived' END,updated_ts=$ts WHERE campaign_id=$campaign AND timeline_id=$timeline AND hero_id=$hero AND status='active_child';",
                    new Dictionary<string, object> { ["ts"] = ts, ["campaign"] = campaignId, ["timeline"] = timelineId, ["hero"] = heroId });
                ExecuteSql(connection, "INSERT INTO childhood_maturity_receipts(receipt_id,campaign_id,timeline_id,hero_id,retained_count,archived_count,created_ts) VALUES($id,$campaign,$timeline,$hero,$retained,$archived,$ts);",
                    new Dictionary<string, object> { ["id"] = "childhood_maturity_" + FamilyStableId(campaignId + "|" + timelineId + "|" + heroId), ["campaign"] = campaignId, ["timeline"] = timelineId, ["hero"] = heroId, ["retained"] = retained, ["archived"] = archived, ["ts"] = ts });
                return new Dictionary<string, object> { ["ok"] = true, ["retainedCount"] = retained, ["archivedCount"] = archived };
            }
        }

        private static Dictionary<string, object> FamilyChambersSceneGenerate(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string cacheKey = ReadString(payload, "cacheKey", "");
            List<Dictionary<string, object>> profiles = ReadDictionaryList(payload, "participantProfiles").Take(4).ToList();
            if (string.IsNullOrWhiteSpace(cacheKey) || !FamilySceneParticipantCountValid(profiles.Count)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A cache key and one to four participants are required." };
            string folder = Path.Combine(CampaignDirectory(campaignId), "family_chambers", SafePathSegment(timelineId, "main"));
            string refsFolder = Path.Combine(folder, "child_references"); Directory.CreateDirectory(refsFolder);
            string imagePath = Path.Combine(folder, SafePathSegment(cacheKey, "scene") + ".png");
            if (File.Exists(imagePath)) return new Dictionary<string, object> { ["ok"] = true, ["cached"] = true, ["imageBase64"] = Convert.ToBase64String(File.ReadAllBytes(imagePath)) };
            Dictionary<string, string> suppliedReferences = ReadDictionaryList(payload, "participantReferences")
                .Where(item => !string.IsNullOrWhiteSpace(CharacterIdFrom(item)) && !string.IsNullOrWhiteSpace(ReadString(item, "imageBase64", "")))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => ReadString(group.First(), "imageBase64", ""), StringComparer.OrdinalIgnoreCase);
            List<byte[]> references = new List<byte[]>();
            List<string> referenceMappings = new List<string>();
            string timeBlock = ReadString(payload, "timeBlock", "daytime");
            bool includesChildren = profiles.Any(profile => ReadBool(profile, "isChild", false));
            var prompt = new StringBuilder(FamilyImageSafetyContract(profiles.Count, timeBlock));
            for (int i = 0; i < profiles.Count; i++)
            {
                Dictionary<string, object> profile = profiles[i]; string id = CharacterIdFrom(profile); double age = ReadDouble(profile, "age", 0d);
                bool isChild = ReadBool(profile, "isChild", false);
                prompt.AppendLine("In the " + FamilyPositionWords(i, profiles.Count) + " zone, show a "
                    + FamilyAgeWords(age) + " " + (ReadBool(profile, "isFemale", false) ? (isChild ? "girl" : "woman") : (isChild ? "boy" : "man"))
                    + " with " + FamilyAppearanceWords(profile) + ". "
                    + (isChild ? "This young person was already occupied with one calm, age-appropriate household activity when the ruler arrived."
                        : includesChildren ? "This adult has just entered beside the ruler and is quietly witnessing the young person's activity."
                        : "This adult has just entered beside the ruler and is settling into a calm household moment."));
                if (suppliedReferences.TryGetValue(id, out string supplied))
                {
                    references.Add(Convert.FromBase64String(supplied));
                    referenceMappings.Add("the " + FamilyOrdinalWords(references.Count - 1) + " reference cell depicts the person in the " + FamilyPositionWords(i, profiles.Count) + " zone");
                }
                if (isChild)
                {
                    string refPath = Path.Combine(refsFolder, SafePathSegment(id, "child") + ".png");
                    string manifestPath = refPath + ".json";
                    if (File.Exists(refPath))
                    {
                        references.Add(File.ReadAllBytes(refPath));
                        referenceMappings.Add("the " + FamilyOrdinalWords(references.Count - 1) + " reference cell depicts the person in the " + FamilyPositionWords(i, profiles.Count) + " zone");
                    }
                    Dictionary<string, object> manifest = ReadJsonObject(manifestPath) ?? new Dictionary<string, object>();
                    double oldAge = ReadDouble(manifest, "age", age);
                    if (age - oldAge >= 2d) prompt.AppendLine("Advance the person in the " + FamilyPositionWords(i, profiles.Count) + " zone from " + FamilyAgeWords(oldAge) + " to " + FamilyAgeWords(age) + " in this single image-to-image pass while preserving identity.");
                }
            }
            if (referenceMappings.Count > 0)
                prompt.AppendLine("The supplied reference contact sheet is identity guidance only: " + string.Join("; ", referenceMappings) + ". Preserve those identities without copying backgrounds, adding figures, or duplicating anyone.");
            prompt.AppendLine("The result must be a single natural scene, not a character lineup or contact sheet. No romance, sexuality, danger, punishment, politics, weapons, or combat.");
            byte[] sheet = FamilyReferenceSheet(references);
            Dictionary<string, object> imagePayload = new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
            {
                ["promptPurpose"] = "family_chambers_scene", ["prompt"] = prompt.ToString(), ["outputSize"] = "1536x864",
                ["sharedCacheOutput"] = false, ["cacheKey"] = "family_chambers_" + SafePathSegment(cacheKey, "scene"), ["heroStringId"] = "family_chambers"
            };
            if (sheet != null) imagePayload["sourceImageBase64"] = Convert.ToBase64String(sheet);
            Dictionary<string, object> generated = PortraitGenerate(imagePayload);
            if (!ReadBool(generated, "ok", false)) return generated;
            byte[] scene = NormalizeCastleScene16By9(Convert.FromBase64String(ReadString(generated, "imageBase64", "")), out _, out _);
            if (scene == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Generated scene could not be normalized." };
            WriteSharedPortraitAtomic(imagePath, scene);
            SaveFamilyChildReferences(scene, profiles, refsFolder, ReadDouble(payload, "worldDay", 0d));
            generated["imageBase64"] = Convert.ToBase64String(scene); generated["cached"] = false;
            return generated;
        }

        private static byte[] FamilyReferenceSheet(List<byte[]> images)
        {
            if (images == null || images.Count == 0) return null;
            const int cell = 320, height = 448; int width = cell * images.Count; byte[] canvas = new byte[width * height * 4];
            for (int p = 0; p < canvas.Length; p += 4) { canvas[p] = 18; canvas[p + 1] = 16; canvas[p + 2] = 14; canvas[p + 3] = 255; }
            for (int index = 0; index < images.Count; index++)
            {
                byte[] rgba = PngReencode.DecodeToRgba(images[index], out int sw, out int sh); if (rgba == null) continue;
                float scale = Math.Min((float)cell / sw, (float)height / sh); int dw = Math.Max(1, (int)(sw * scale)), dh = Math.Max(1, (int)(sh * scale));
                byte[] resized = PortraitDerivativeCore.ResizeBilinear(rgba, sw, sh, dw, dh); int ox = index * cell + (cell - dw) / 2, oy = (height - dh) / 2;
                for (int y = 0; y < dh; y++) Buffer.BlockCopy(resized, y * dw * 4, canvas, ((oy + y) * width + ox) * 4, dw * 4);
            }
            return PngEncoder.EncodeRgba(canvas, width, height);
        }

        private static void SaveFamilyChildReferences(byte[] scene, List<Dictionary<string, object>> profiles, string folder, double worldDay)
        {
            byte[] rgba = PngReencode.DecodeToRgba(scene, out int width, out int height); if (rgba == null) return;
            int slotWidth = width / profiles.Count;
            for (int i = 0; i < profiles.Count; i++)
            {
                Dictionary<string, object> profile = profiles[i]; if (!ReadBool(profile, "isChild", false)) continue;
                int x0 = i * slotWidth; int actualWidth = i == profiles.Count - 1 ? width - x0 : slotWidth;
                byte[] slot = new byte[actualWidth * height * 4];
                for (int y = 0; y < height; y++) Buffer.BlockCopy(rgba, (y * width + x0) * 4, slot, y * actualWidth * 4, actualWidth * 4);
                try
                {
                    PortraitFaceFocus face = DetectPortraitFaceFocus(slot, actualWidth, height);
                    if (face.Confidence < 0.70d || face.CandidateCount != 1) continue;
                    int fw = Math.Max(1, (int)(face.Width * actualWidth)), fh = Math.Max(1, (int)(face.Height * height));
                    int cx = (int)((face.X + face.Width / 2d) * actualWidth), cy = (int)((face.Y + face.Height / 2d) * height);
                    int crop = Math.Max(fw, fh) * 3; crop = Math.Min(crop, Math.Min(actualWidth, height));
                    int sx = Math.Max(0, Math.Min(actualWidth - crop, cx - crop / 2)); int sy = Math.Max(0, Math.Min(height - crop, cy - crop / 2));
                    byte[] cut = new byte[crop * crop * 4]; for (int y = 0; y < crop; y++) Buffer.BlockCopy(slot, ((sy + y) * actualWidth + sx) * 4, cut, y * crop * 4, crop * 4);
                    byte[] resized = PortraitDerivativeCore.ResizeBilinear(cut, crop, crop, 512, 512);
                    string id = CharacterIdFrom(profile); string path = Path.Combine(folder, SafePathSegment(id, "child") + ".png");
                    WriteSharedPortraitAtomic(path, PngEncoder.EncodeRgba(resized, 512, 512));
                    WriteSharedPortraitAtomic(path + ".json", Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object>
                    { ["heroId"] = id, ["age"] = ReadDouble(profile, "age", 0d), ["worldDay"] = worldDay, ["confidence"] = face.Confidence, ["method"] = "ultraface_fixed_participant_slot", ["updatedUtc"] = DateTime.UtcNow.ToString("O") })));
                }
                catch (Exception ex) { LogOperational("family_chambers.child_reference_skipped", new Dictionary<string, object> { ["heroId"] = CharacterIdFrom(profile), ["error"] = LimitText(ex.Message, 300) }); }
            }
        }
    }
}
