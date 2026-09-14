using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPromptCompositionSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, passed, data) => results.Add(new Dictionary<string, object> {
                ["caseId"] = id, ["passed"] = passed, ["summary"] = id, ["data"] = data
            });
            Func<Action, bool> rejects = action => { try { action(); return false; } catch (InvalidOperationException) { return true; } };
            foreach (string[] layers in new[] { new[] { "a" }, new[] { "b" }, new[] { "a", "b" } })
            {
                var composer = new PromptRuleComposer();
                composer.Define("shared", "Shared", "SHARED_RULE_SENTINEL", "fixture");
                composer.Define("a", "A", "Only A", "fixture", "shared");
                composer.Define("b", "B", "Only B", "fixture", "shared");
                foreach (string layer in layers) composer.Require(layer, "fixture");
                var metadata = new Dictionary<string, object>();
                string prompt = composer.Render(metadata);
                add("rule_dependency_" + string.Join("_", layers),
                    prompt.Split(new[] { "SHARED_RULE_SENTINEL" }, StringSplitOptions.None).Length == 2
                    && layers.All(layer => prompt.Contains("Only " + layer.ToUpperInvariant())), metadata);
            }
            add("rule_missing_dependency_fails_closed", rejects(() => {
                var c = new PromptRuleComposer(); c.Define("a", "A", "A", "test", "missing"); c.Require("a", "test"); c.Render();
            }), null);
            add("rule_conflicting_definition_fails_closed", rejects(() => {
                var c = new PromptRuleComposer(); c.Define("a", "A", "A", "test"); c.Define("a", "A", "Different", "test");
            }), null);
            add("rule_cycle_fails_closed", rejects(() => {
                var c = new PromptRuleComposer(); c.Define("a", "A", "A", "test", "b"); c.Define("b", "B", "B", "test", "a"); c.Require("a", "test"); c.Render();
            }), null);
            add("required_template_empty_fails_closed", rejects(() => new PromptRuleComposer().Add("WORLD TONE", " ")), null);

            var matrix = new List<Dictionary<string, object>>();
            foreach (bool eventMode in new[] { false, true })
            foreach (bool? noble in new bool?[] { false, true, null })
            foreach (bool room in new[] { false, true })
            foreach (bool office in new[] { false, true })
            {
                var metadata = new Dictionary<string, object>();
                string prompt = BuildDialogueGlobalPrefix(eventMode, noble, new List<Dictionary<string, object>>(),
                    office ? "AUTHORITATIVE_OFFICE_FIXTURE" : "", room, metadata);
                var rules = ReadDictionaryList(metadata, "rules");
                bool valid = rules.Count == rules.Select(row => ReadString(row, "ruleId", "")).Distinct().Count()
                    && prompt.Contains(NormalizePromptSegment(ScopeConversationTone(LoadPromptTemplate("world_tone.txt"))))
                    && (noble == false ? !prompt.Contains("NOBLE PROMPT") : prompt.Contains(NormalizePromptSegment(ScopeConversationTone(LoadPromptTemplate("noble_prompt.txt")))))
                    && prompt.Contains("Plausibility is not evidence") && prompt.Contains("successful receipt")
                    && prompt.IndexOf("WORLD TONE", StringComparison.Ordinal) < prompt.IndexOf("AUTHORITATIVE FACT BOUNDARY", StringComparison.Ordinal)
                    && (eventMode || prompt.Contains("Always include socialSignals"))
                    && prompt.Contains("ACTION ROUTING BOUNDARY") && prompt.Contains("IDENTITY INTRODUCTION RULES")
                    && prompt.Contains("DYNAMIC CHARACTERISTICS POLICY") && prompt.Contains("MEMORY WRITE RULES")
                    && prompt.Contains("CONVERSATION NATURALNESS") && prompt.Contains("separate outstanding request")
                    && prompt.Contains("SCENE STATE OUTPUT") && prompt.Contains("OUTPUT SCHEMA")
                    && (room ? prompt.Contains("Do not emit hourly clothing overrides") : prompt.Contains(NormalizePromptSegment(SceneStateOutputContract)))
                    && (!office || prompt.Contains("AUTHORITATIVE_OFFICE_FIXTURE"))
                    && (noble.HasValue || prompt.Contains("Noble station guidance is conditional"));
                matrix.Add(new Dictionary<string, object> { ["event"] = eventMode, ["noble"] = noble,
                    ["roomAttire"] = room, ["office"] = office, ["passed"] = valid, ["characters"] = prompt.Length });
            }
            add("production_rule_applicability_matrix", matrix.All(row => ReadBool(row, "passed", false)), matrix);
            add("unknown_native_station_retains_conditional_rules", NativeNoblePromptApplicability(new Dictionary<string, object>()) == null
                && NativeNoblePromptApplicability(new Dictionary<string, object> { ["isLord"] = false }) == false, null);
            string prefix = BuildDialogueGlobalPrefix(false, true, new List<Dictionary<string, object>>());
            var fresh = CreatePromptEnvelope("dialogue", "noble", prefix, "PERSONALITY_SENTINEL", "new conversation");
            var next = CreatePromptEnvelope("dialogue", "noble", prefix, "PERSONALITY_SENTINEL", "next conversation turn");
            add("standing_rules_present_on_fresh_and_continued_requests", fresh.Messages[0]["content"].Equals(next.Messages[0]["content"])
                && ReadString(fresh.Messages[0], "content", "").Contains("ACTION GATE RULES")
                && ReadString(next.Messages[0], "content", "").Contains("MEMORY WRITE RULES"), fresh.Diagnostics);

            foreach (bool noble in new[] { false, true })
            {
                string id = noble ? "prompt-noble" : "prompt-commoner";
                var profile = new Dictionary<string, object> { ["heroStringId"] = id, ["name"] = "Fixture Speaker", ["isLord"] = noble, ["age"] = 35 };
                if (!noble) profile["encounteredResident"] = new Dictionary<string, object> {
                    ["schema"] = "reign-encountered-resident-v1", ["residentId"] = id, ["occupation"] = "Merchant" };
                var character = new Dictionary<string, object> { ["traits"] = new Dictionary<string, object> {
                    ["basePersonalitySummary"] = "PERSONALITY_COVERAGE_SENTINEL: cautious and protective of family." } };
                var empty = new Dictionary<string, object>();
                foreach (string mode in new[] { "dialogue", "party_chat", "social_event" })
                {
                    var payload = new Dictionary<string, object> { ["campaignId"] = "verify_prompt_composition", ["heroStringId"] = id,
                        ["hero"] = profile, ["playerName"] = "Ruler", ["playerText"] = "What do you mean? I do not understand.",
                        ["sceneContext"] = "An audience with the ruler.", ["mode"] = mode == "social_event" ? "settlement_home" : mode };
                    var snapshot = new Dictionary<string, object> { ["profile"] = profile, ["characteristics"] = character,
                        ["state"] = empty, ["relationships"] = empty, ["memorySummary"] = empty };
                    var transcriptFixture = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["turnId"] = "shared-history-beat", ["exchangeId"] = "shared-history-beat",
                            ["role"] = "player", ["speaker"] = "Ruler", ["speakerHeroStringId"] = "ruler", ["text"] = "PLAYER_HISTORY_SENTINEL" },
                        new Dictionary<string, object> { ["turnId"] = "shared-history-beat", ["exchangeId"] = "shared-history-beat",
                            ["role"] = "npc", ["speaker"] = "Fixture Speaker", ["speakerHeroStringId"] = id, ["text"] = "NPC_HISTORY_SENTINEL" }
                    };
                    var rows = CanonicalizePromptTranscript(transcriptFixture.Concat(transcriptFixture), 30);
                    snapshot["priorLines"] = rows;
                    var messages = BuildTestMessages(empty, payload, snapshot,
                        new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                        mode, out string text, out var diagnostics);
                    add("production_envelope_" + id + "_" + mode, messages.Count == 3
                        && text.Contains("PERSONALITY_COVERAGE_SENTINEL") && text.Contains("AUTHORITATIVE FACT BOUNDARY")
                        && text.Contains("ACTION ROUTING BOUNDARY") && text.Contains("What do you mean? I do not understand.")
                        && text.Contains("CURRENT CONVERSATION GUIDANCE") && text.Contains("Confusion alone is not refusal or evasion")
                        && !text.Contains("LEGACY CUSTOM PROMPT EXTENSION")
                        && text.Split(new[] { "PLAYER_HISTORY_SENTINEL" }, StringSplitOptions.None).Length == 2
                        && text.Split(new[] { "NPC_HISTORY_SENTINEL" }, StringSplitOptions.None).Length == 2
                        && (noble ? text.Contains("NOBLE PROMPT") : text.Contains("COMMONER ROLE - CONTINUING CHARACTER LAYER") && !text.Contains("NOBLE PROMPT")),
                        new Dictionary<string, object> { ["messageCount"] = messages.Count, ["messageCharacters"] = messages.Sum(row => ReadString(row, "content", "").Length),
                            ["diagnostics"] = diagnostics });
                }
                var letter = BuildCorrespondencePromptEnvelope("verify_prompt_composition", id, "Fixture Speaker", "ruler", "Ruler", 1,
                    "RECEIVED_LETTER_HISTORY_SENTINEL", profile, character, empty,
                    "Fixture Speaker previously wrote: NPC_LETTER_HISTORY_SENTINEL");
                add("production_correspondence_" + id, ReadDictionary(letter.Diagnostics, "composition") != null
                    && ReadString(letter.Messages[0], "content", "").Contains("REBELLION DECISION CONTRACT")
                    && ReadString(letter.Messages.Last(), "content", "").Contains("RECEIVED_LETTER_HISTORY_SENTINEL")
                    && ReadString(letter.Messages.Last(), "content", "").Contains("NPC_LETTER_HISTORY_SENTINEL")
                    && !ReadString(letter.Messages.Last(), "content", "").Contains("REBELLION DECISION CONTRACT"), letter.Diagnostics);
            }

            var historical = new Dictionary<string, object> { ["version"] = 2, ["gold"] = 10000,
                ["partyInventoryValue"] = 0, ["visibleWealthTier"] = "destitute", ["personalWealthTier"] = "wealthy", ["clanGold"] = 602877 };
            var facts = PromptWealthFacts(historical);
            add("legacy_no_party_is_not_visible_destitution", !facts.ContainsKey("visibleWealthTier") && !facts.ContainsKey("partyInventoryValue")
                && ReadString(facts, "personalWealthTier", "") == WealthTier(10000)
                && ReadString(historical, "visibleWealthTier", "") == "destitute", facts);
            var richDressPoorCash = PromptWealthFacts(new Dictionary<string, object> {
                ["version"] = 3, ["gold"] = 0, ["personalWealthTier"] = WealthTier(0), ["clanGold"] = 500000,
                ["partyInventoryValue"] = null, ["partyInventoryState"] = "not_applicable" });
            add("poor_cash_rich_clan_and_dress_remain_independent", ReadInt(richDressPoorCash, "gold", -1) == 0
                && ReadInt(richDressPoorCash, "clanGold", 0) == 500000 && !richDressPoorCash.ContainsKey("visibleWealthTier")
                && ReadString(richDressPoorCash, "interpretation", "").Contains("rich clothing does not prove available cash"), richDressPoorCash);
            var unknownFunds = PromptWealthFacts(new Dictionary<string, object> { ["economicCapacityKnown"] = false,
                ["gold"] = 0, ["clanGold"] = 0, ["personalWealthTier"] = "destitute" });
            add("unknown_economic_capacity_does_not_become_zero_cash", !unknownFunds.ContainsKey("gold") && !unknownFunds.ContainsKey("personalWealthTier")
                && !unknownFunds.ContainsKey("clanGold"), unknownFunds);
            add("null_legacy_cash_does_not_become_destitute", !PromptWealthFacts(new Dictionary<string, object> {
                ["version"] = 2, ["gold"] = null, ["dataState"] = "unknown" }).ContainsKey("personalWealthTier"), null);

            const string legacyLetter = "World day: {worldDay}\nSender: {senderName}\nRecipient: {recipientName}\n\nConcise character foundation:\n{characterFoundation}\n\nPrivate directional relationship context:\n{relationshipContext}\n\nRelevant memory:\n{memoryContext}\n\nLetter received or reason for considering correspondence:\n{receivedLetter}\n\nDecide whether this character would write. If so, compose the letter in their own written voice. Treat distance and delayed delivery as real.";
            add("retirement_reviewed_installed_hashes_are_explicit",
                RetiredPromptHashes["dialogue_user_template.txt"].Contains("2C827E012C791377E33E3E3EE149871EBD897CC621775B4D478D3EDB7C176A12")
                && RetiredPromptHashes["event_user_template.txt"].Contains("41A1301B66744EC1B273A16188839192AF010B413E8730247836EE912D84CB84"), null);
            string directory = Path.Combine(Path.GetTempPath(), "reign-prompt-retirement-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string known = Path.Combine(directory, "correspondence_user_template.txt");
            string unknown = Path.Combine(directory, "dialogue_user_template.txt");
            try
            {
                File.WriteAllText(known, legacyLetter, Encoding.UTF8);
                File.WriteAllText(unknown, "UNREVIEWED_CUSTOM_RULE", Encoding.UTF8);
                bool blocked = rejects(() => RetireLegacyPromptTemplates(directory));
                add("retirement_unknown_customization_preserves_entire_set", blocked && File.Exists(known) && File.Exists(unknown)
                    && File.ReadAllText(unknown) == "UNREVIEWED_CUSTOM_RULE", null);
                File.Delete(unknown);
                RetireLegacyPromptTemplates(directory); RetireLegacyPromptTemplates(directory);
                var archives = Directory.GetFiles(Path.Combine(directory, "retired-v9"));
                add("retirement_archives_exact_original_and_is_idempotent", !File.Exists(known) && archives.Length == 1
                    && File.ReadAllText(archives[0], Encoding.UTF8) == legacyLetter
                    && !PromptFileNames.Contains("correspondence_user_template.txt")
                    && !DefaultPromptTemplates().ContainsKey("dialogue_user_template.txt")
                    && !IsPromptFileNameAllowed("dialogue_user_template.txt"), null);
            }
            finally
            {
                foreach (string path in Directory.GetFiles(directory)) File.Delete(path);
                string archive = Path.Combine(directory, "retired-v9");
                if (Directory.Exists(archive)) { foreach (string path in Directory.GetFiles(archive)) File.Delete(path); Directory.Delete(archive); }
                Directory.Delete(directory);
            }

            string original = new string('S', 17000) + "\napi_key=fixture-secret-123\nBearer fixture-bearer-123\nEND_OF_COMPLETE_PROMPT";
            var record = BuildPromptEvidenceRecord("fixture", "same-correlation", "dialogue", "fixture-model", new Dictionary<string, object> {
                ["messages"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "system", ["content"] = original } }
            }, new Dictionary<string, object>(), "fixture-provider-key");
            var assembled = new StringBuilder(); int pageOffset = 0; bool more;
            do {
                var page = PromptEvidencePage(record, 0, pageOffset, 4000);
                assembled.Append(ReadString(page, "content", "")); pageOffset = ReadInt(page, "nextOffset", 0); more = ReadBool(page, "hasMore", false);
            } while (more);
            string recovered = assembled.ToString();
            add("complete_prompt_redaction_and_pagination", recovered.EndsWith("END_OF_COMPLETE_PROMPT") && recovered.Length > 16000
                && !recovered.Contains("fixture-secret-123") && !recovered.Contains("fixture-bearer-123")
                && PromptContentHash(recovered) == ReadString(ReadDictionaryList(record, "messages")[0], "contentSha256", "")
                && !ReadBool(PromptEvidencePage(record, 1, 0, 4000), "ok", true)
                && !ReadBool(PromptEvidencePage(record, 0, -1, 4000), "ok", true), new Dictionary<string, object> { ["recoveredCharacters"] = recovered.Length });
            var unicode = BuildPromptEvidenceRecord("fixture", "unicode", "dialogue", "fixture-model", new Dictionary<string, object> {
                ["messages"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "user", ["content"] = "ab\U0001F451cd" } }
            }, new Dictionary<string, object>(), "");
            var firstPage = PromptEvidencePage(unicode, 0, 0, 3);
            var nextPage = PromptEvidencePage(unicode, 0, ReadInt(firstPage, "nextOffset", 0), 4);
            add("prompt_pages_preserve_unicode_characters", ReadString(firstPage, "content", "") + ReadString(nextPage, "content", "") == "ab\U0001F451cd"
                && !ReadBool(PromptEvidencePage(unicode, 0, 3, 4), "ok", true), null);
            return results;
        }
    }
}
