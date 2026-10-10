using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int ConversationDrinkInputTokenBudget = 6144;

        private sealed class DrinkPromptPacket
        {
            public string Rules;
            public string Text;
            public Dictionary<string, object> Budget;
        }

        // The subsystem owns its rules, current physical state and scene projection.
        // Core allocation runs first and is never retried with less memory/history
        // to make room for drinks. Provider preflight still measures the full body.
        private static DrinkPromptPacket BuildConversationDrinkPacket(string physical,
            Dictionary<string, object> context, string hero, string player,
            int tokenBudget = ConversationDrinkInputTokenBudget)
        {
            var scene = ReadDictionary(context, "drinkScene");
            var source = ReadDictionaryList(scene, "vessels").Take(16).ToList();
            string selected = ReadString(ReadDictionary(context, "vessel"), "id", "");
            string offered = ReadString(ReadDictionary(context, "availableDrink"), "id", "");
            var ordered = source.OrderBy(v => {
                string id = ReadString(v, "id", "");
                string holder = ReadString(v, "holderHeroId", "");
                return id == selected || id == offered ? 0 : holder == hero ? 1 : holder == player ? 2 : 3;
            }).ToList();
            var retained = new List<Dictionary<string, object>>();
            string rules = "PRIVATE DRINK SUBSYSTEM — AUTHORITATIVE ENGINE RULES\n" + DrinkingOutputContract
                + "\nOmission never deletes stored inventory. Use known custody and ids where available; explicitly identify the beverage and completed portion in drinkActions when a cup record is missing. Drinking remains the speaking NPC's choice.";
            string Render() => "PRIVATE DRINK SUBSYSTEM — CURRENT ENGINE STATE\n" + physical
                + (scene == null ? "" : "\nCURRENT PHYSICAL DRINK SCENE: " + Json.Serialize(TestDict(
                    "schema", "reign-drink-scene-v1", "scope", ReadString(scene, "scope", ""),
                    "hour", ReadDouble(scene, "hour", 0), "vessels", retained,
                    "omittedVessels", source.Count - retained.Count)));
            int Tokens(string text) => EstimateContinuityTokens(Json.Serialize(new[] {
                TestDict("role", "user", "content", rules), TestDict("role", "user", "content", text) }));
            string text = Render();
            int mandatory = Tokens(text);
            if (mandatory > tokenBudget)
                throw new InvalidOperationException("Required drinking rules and physical state exceed the independent drink prompt budget.");
            // Whole optional records only; never cut IDs, revisions or evidence.
            foreach (var vessel in ordered)
            {
                retained.Add(vessel);
                string candidate = Render();
                if (Tokens(candidate) <= tokenBudget) text = candidate;
                else retained.RemoveAt(retained.Count - 1);
            }
            return new DrinkPromptPacket { Rules = rules, Text = text, Budget = TestDict(
                "schema", "reign-drink-prompt-budget-v1", "inputTokenBudget", tokenBudget,
                "inputTokenEstimate", Tokens(text), "mandatoryInputTokenEstimate", mandatory,
                "estimator", "ascii_characters_divided_by_three_plus_non_ascii_utf8_bytes_and_overhead",
                "retainedVessels", retained.Count, "omittedVessels", source.Count - retained.Count,
                "retainedVesselIds", retained.Select(v => ReadString(v, "id", "")).ToList(),
                "contentHash", PromptHash(rules + "\n\n" + text), "rulesHash", PromptHash(rules), "stateHash", PromptHash(text),
                "allocation", "independent_after_core_allocation",
                "additionalProviderCalls", 0) };
        }

        private static void AttachDrinkPacket(PromptEnvelope envelope, DrinkPromptPacket packet)
        {
            int coreTokens = EstimateContinuityTokens(Json.Serialize(envelope.Messages));
            string coreHash = PromptHash(Json.Serialize(envelope.Messages));
            // Allocation remains independent and after core allocation. Stable rules
            // precede character data; changing state extends the final live suffix.
            envelope.Messages.Insert(1, TestDict("role", "user", "content", packet.Rules));
            var last = envelope.Messages.Last();
            last["content"] = ReadString(last, "content", "") + "\n\n" + packet.Text;
            envelope.TotalChars += packet.Rules.Length + 2 + packet.Text.Length;
            envelope.Diagnostics["liveTurnHash"] = PromptHash(ReadString(last, "content", ""));
            envelope.Diagnostics["liveTurnChars"] = ReadString(last, "content", "").Length;
            envelope.Diagnostics["messageCount"] = envelope.Messages.Count;
            packet.Budget["coreInputTokenEstimate"] = coreTokens;
            packet.Budget["coreMessagesHash"] = coreHash;
            packet.Budget["totalInputTokenEstimate"] = EstimateContinuityTokens(Json.Serialize(envelope.Messages));
            envelope.Diagnostics["drinkPromptBudget"] = packet.Budget;
        }

        private static void AttachConversationDrinkPrompt(PromptEnvelope envelope, string campaign, string hero,
            Dictionary<string, object> payload, Dictionary<string, object> profile, Dictionary<string, object> characteristics)
        {
            string physical = BuildIntoxicationPrompt(campaign, hero, payload, profile, characteristics);
            AttachDrinkPacket(envelope, BuildConversationDrinkPacket(physical,
                ReadDictionary(payload, "intoxicationContext"), hero,
                ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId")));
        }

        private static List<Dictionary<string, object>> RunConversationDrinkPromptSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => rows.Add(TestDict(
                "id", "drink_prompt_" + id, "caseId", "drink_prompt_" + id, "passed", pass,
                "summary", id, "data", data));
            string physical = "AUTHORITATIVE CURRENT INTOXICATION: sober; held cup selected revision 4; offered cup offer revision 2.";
            var cups = new List<Dictionary<string, object>> {
                TestDict("id", "other", "holderHeroId", "other", "contentsVersion", 1),
                TestDict("id", "selected", "holderHeroId", "npc", "contentsVersion", 4),
                TestDict("id", "offer", "holderHeroId", "player", "contentsVersion", 2) };
            var context = TestDict("vessel", TestDict("id", "selected"), "availableDrink", TestDict("id", "offer"),
                "drinkScene", TestDict("scope", "fixture", "hour", 240d, "vessels", cups));
            string sourceBefore = Json.Serialize(context);
            var packet = BuildConversationDrinkPacket(physical, context, "npc", "player");
            Check("own_limit_and_complete_state", ReadInt(packet.Budget, "inputTokenBudget", 0) == 6144
                && ReadInt(packet.Budget, "inputTokenEstimate", int.MaxValue) <= 6144
                && ReadInt(packet.Budget, "retainedVessels", 0) == 3
                && packet.Text.Contains(physical) && packet.Rules.Contains(DrinkingOutputContract), packet.Budget);
            var envelope = CreatePromptEnvelope("dialogue", "fixture", "core rules", "core character",
                "protected memory: source-1\ncurrent commitment: source-2\nlatest player turn");
            string before = Json.Serialize(envelope.Messages);
            string diagnosticsBefore = Json.Serialize(envelope.Diagnostics);
            int coreChars = envelope.TotalChars;
            AttachDrinkPacket(envelope, packet);
            var afterCore = envelope.Messages.Where((_, i) => i != 1).Select(CloneDictionary).ToList();
            afterCore.Last()["content"] = ReadString(afterCore.Last(), "content", "").Substring(0,
                ReadString(afterCore.Last(), "content", "").Length - packet.Text.Length - 2);
            Check("core_context_and_allocations_unchanged", Json.Serialize(afterCore) == before
                && envelope.TotalChars == coreChars + packet.Rules.Length + 2 + packet.Text.Length
                && ReadString(packet.Budget, "coreMessagesHash", "") == PromptHash(before)
                && ReadString(envelope.Diagnostics, "liveTurnHash", "") == PromptHash(ReadString(envelope.Messages.Last(), "content", ""))
                && ReadInt(packet.Budget, "coreInputTokenEstimate", 0) == EstimateContinuityTokens(before), diagnosticsBefore);
            var policy = TestDict("inputAllowance", EstimateContinuityTokens(before), "outputFits", true);
            FinalizeContinuityPromptBudget(envelope, new Dictionary<string, object>(), policy);
            var preflight = ReadDictionary(envelope.Diagnostics, "continuityPreflight");
            Check("aggregate_capacity_still_enforced", !ReadBool(preflight, "fits", true)
                && ReadInt(preflight, "inputTokenEstimate", 0) == EstimateContinuityTokens(Json.Serialize(envelope.Messages))
                && ReadInt(ReadDictionary(preflight, "tokenAccounting"), "drinkSubsystem", 0) == ReadInt(packet.Budget, "inputTokenEstimate", -1));
            var crowded = CloneDictionary(context);
            var many = ReadDictionaryList(ReadDictionary(crowded, "drinkScene"), "vessels");
            many.Insert(0, TestDict("id", "oversized_optional", "holderHeroId", "other", "note", new string('酒', 9000)));
            ReadDictionary(crowded, "drinkScene")["vessels"] = many;
            var bounded = BuildConversationDrinkPacket(physical, crowded, "npc", "player");
            Check("whole_optional_records_only", ReadInt(bounded.Budget, "inputTokenEstimate", int.MaxValue) <= 6144
                && ReadInt(bounded.Budget, "omittedVessels", 0) == 1
                && !bounded.Text.Contains("oversized_optional") && bounded.Text.Contains("selected") && bounded.Text.Contains(physical)
                && Json.Serialize(context) == sourceBefore, bounded.Budget);
            bool rejected = false;
            try { BuildConversationDrinkPacket(physical, context, "npc", "player", 1); }
            catch (InvalidOperationException) { rejected = true; }
            Check("mandatory_state_never_truncated", rejected);
            Check("rules_outside_core_budget", !BuildDialogueGlobalPrefix(false, false,
                new List<Dictionary<string, object>>()).Contains(DrinkingOutputContract));
            var changedContext = CloneDictionary(context);
            ReadDictionary(changedContext, "drinkScene")["hour"] = 241d;
            var changedPacket = BuildConversationDrinkPacket(physical + " Now slightly intoxicated.", changedContext, "npc", "player");
            var changedEnvelope = CreatePromptEnvelope("dialogue", "fixture", "core rules", "core character",
                "protected memory: source-1\ncurrent commitment: source-2\nlatest player turn");
            AttachDrinkPacket(changedEnvelope, changedPacket);
            Check("changing_state_preserves_all_leading_messages", packet.Rules == changedPacket.Rules
                && packet.Text != changedPacket.Text
                && Json.Serialize(envelope.Messages.Take(3)) == Json.Serialize(changedEnvelope.Messages.Take(3))
                && ReadString(changedEnvelope.Messages.Last(), "content", "").StartsWith(ReadString(afterCore.Last(), "content", ""), StringComparison.Ordinal));
            return rows;
        }
    }
}
