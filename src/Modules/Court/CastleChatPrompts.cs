using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        internal static readonly string[] CastleChatCultures =
            { "aserai", "battania", "empire", "khuzait", "nord", "sturgia", "vlandia" };
        internal static readonly string[] CastleChatRooms =
        {
            "castle_gardens", "noble_solar", "library", "training_yard", "inner_courtyard",
            "small_dining_chamber", "stable_courtyard", "portrait_gallery", "main_hall", "chapel",
            "throne_room", "baths", "battlements", "guest_bedrooms", "royal_bedroom"
        };

        private static string[] CastleChatPromptFileNames()
        {
            return (from culture in CastleChatCultures
                    from room in CastleChatRooms
                    from kind in new[] { "image", "dialogue" }
                    select "castle_chat_" + culture + "_" + room + "_" + kind + ".txt")
                .Concat(new[]
                {
                    "castle_chat_foreign_keep_image.txt",
                    "castle_chat_foreign_keep_dialogue.txt"
                }).ToArray();
        }

        internal static Dictionary<string, string> CastleChatPromptDefaults()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string name in CastleChatPromptFileNames())
            {
                string resource = assembly.GetManifestResourceNames().FirstOrDefault(x =>
                    x.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
                if (resource == null) { result[name] = string.Empty; continue; }
                using (Stream stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true)) result[name] = reader.ReadToEnd();
            }
            return result;
        }

        private static Dictionary<string, object> CastleChatPromptsApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string culture = ReadString(payload, "culture", "generic").Trim().ToLowerInvariant();
            string room = ReadString(payload, "room", "main_hall").Trim().ToLowerInvariant();
            if (!CastleChatCultures.Contains(culture)) culture = "empire";
            if (!CastleChatRooms.Contains(room)) room = "main_hall";
            string imageName = "castle_chat_" + culture + "_" + room + "_image.txt";
            string dialogueName = "castle_chat_" + culture + "_" + room + "_dialogue.txt";
            string ownershipMode = ReadString(payload, "ownershipMode", "player_ruled").Trim().ToLowerInvariant();
            if (!string.Equals(ownershipMode, "foreign", StringComparison.Ordinal)) ownershipMode = "player_ruled";

            if (ReadBool(payload, "reset", false))
            {
                Dictionary<string, string> defaults = CastleChatPromptDefaults();
                File.WriteAllText(PromptPath(imageName), defaults[imageName], Encoding.UTF8);
                File.WriteAllText(PromptPath(dialogueName), defaults[dialogueName], Encoding.UTF8);
                InvalidatePromptRuntimeCache();
            }
            else if (payload.ContainsKey("imagePrompt") || payload.ContainsKey("dialoguePrompt"))
            {
                string image = ReadString(payload, "imagePrompt", File.Exists(PromptPath(imageName)) ? File.ReadAllText(PromptPath(imageName)) : string.Empty);
                string dialogue = ReadString(payload, "dialoguePrompt", File.Exists(PromptPath(dialogueName)) ? File.ReadAllText(PromptPath(dialogueName)) : string.Empty);
                if (string.IsNullOrWhiteSpace(image) || string.IsNullOrWhiteSpace(dialogue))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Both Castle Chat prompts are required." };
                File.WriteAllText(PromptPath(imageName), image, Encoding.UTF8);
                File.WriteAllText(PromptPath(dialogueName), dialogue, Encoding.UTF8);
                InvalidatePromptRuntimeCache();
            }

            EnsurePromptTemplates();
            string resolvedImage = File.ReadAllText(PromptPath(imageName), Encoding.UTF8);
            string resolvedDialogue = File.ReadAllText(PromptPath(dialogueName), Encoding.UTF8);
            if (string.Equals(ownershipMode, "foreign", StringComparison.Ordinal))
            {
                resolvedImage = ComposeForeignCastleChatPrompt(resolvedImage,
                    File.ReadAllText(PromptPath("castle_chat_foreign_keep_image.txt"), Encoding.UTF8), payload);
                resolvedDialogue = ComposeForeignCastleChatPrompt(resolvedDialogue,
                    File.ReadAllText(PromptPath("castle_chat_foreign_keep_dialogue.txt"), Encoding.UTF8), payload);
            }
            string revision;
            using (SHA256 sha = SHA256.Create()) revision = BitConverter.ToString(sha.ComputeHash(
                Encoding.UTF8.GetBytes(resolvedImage + "\n---\n" + resolvedDialogue))).Replace("-", "").ToLowerInvariant();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["culture"] = culture, ["room"] = room, ["ownershipMode"] = ownershipMode,
                ["imagePrompt"] = resolvedImage, ["dialoguePrompt"] = resolvedDialogue,
                ["revision"] = revision,
                ["requiredRuntimeFields"] = string.Equals(ownershipMode, "foreign", StringComparison.Ordinal)
                    ? new[] { "settlement", "timeBlock", "orderedParticipants", "settlementOwnerName", "realmRulerName", "playerName" }
                    : new[] { "settlement", "timeBlock", "orderedParticipants" }
            };
        }

        internal static string ComposeForeignCastleChatPrompt(
            string roomPrompt,
            string foreignLayer,
            IDictionary<string, object> runtime)
        {
            runtime = runtime ?? new Dictionary<string, object>();
            string Value(string key, string fallback)
            {
                if (!runtime.TryGetValue(key, out object raw) || raw == null) return fallback;
                string value = Convert.ToString(raw)?.Trim();
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }

            string resolvedLayer = (foreignLayer ?? string.Empty)
                .Replace("{{settlement_name}}", Value("settlementName", "this settlement"))
                .Replace("{{settlement_owner_name}}", Value("settlementOwnerName", "the settlement's ruling clan"))
                .Replace("{{realm_ruler_name}}", Value("realmRulerName", "the realm's ruler"))
                .Replace("{{player_name}}", Value("playerName", "the visiting player"));
            return (roomPrompt ?? string.Empty).TrimEnd() + "\n\n" + resolvedLayer.Trim();
        }
    }
}
