using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AIPortraits;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunTavernHouseContractTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, Action test)
            {
                try { test(); results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = true, ["caseId"] = "tavern_house_" + name, ["summary"] = name }); }
                catch (Exception ex) { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = false, ["caseId"] = "tavern_house_" + name, ["summary"] = ex.Message }); }
            }
            void Require(bool value, string failure) { if (!value) throw new InvalidOperationException(failure); }
            bool Rejects(Action test) { try { test(); return false; } catch (InvalidDataException) { return true; } catch (OverflowException) { return true; } }
            Dictionary<string, object> Hero(string id, string name, double age = 23) => new Dictionary<string, object> {
                ["heroStringId"] = id, ["name"] = name, ["age"] = age, ["isAvailable"] = true };
            var roster = new List<Dictionary<string, object>> { Hero("madam", "Aelia Varro", 29), Hero("worker-a", "Mira Cassia"), Hero("worker-b", "Tavian Corvus") };
            Dictionary<string, object> Charge(string id, object gold, string evidence) => new Dictionary<string, object> { ["heroId"] = id, ["gold"] = gold, ["evidence"] = evidence };
            Check("exact_visible_terms", () => {
                const string speech = "I offer Mira Cassia for 120 denars. Tavian Corvus is free of charge. I offer Aelia Varro for 450 denars.";
                var charges = ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> {
                    Charge("worker-a", 120, "I offer Mira Cassia for 120 denars."), Charge("worker-b", 0, "Tavian Corvus is free of charge."), Charge("madam", 450, "I offer Aelia Varro for 450 denars.") }, roster, speech);
                Require(charges.Count == 3 && charges.Sum(x => ReadInt(x, "gold", 0)) == 570, "Visible individual prices did not produce the exact total.");
                Require(ReadString(charges[0], "displayName", "") == "Mira Cassia", "Display name was not resolved from the native identity.");
            });
            Check("unproven_and_changed_terms_rejected", () => {
                var valid = Charge("worker-a", 120, "Mira Cassia for 120 denars.");
                Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { valid }, roster, "I refuse.")), "Old terms survived a current refusal.");
                foreach (object price in new object[] { -1, 12.5, 0, 12, 1200 })
                    Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { Charge("worker-a", price, "Mira Cassia for 120 denars.") }, roster, "Mira Cassia for 120 denars.")), "A mismatched or invalid price passed.");
                foreach (string uncertain in new[] { "Perhaps Mira Cassia for 120 denars.", "Mira Cassia for 120 denars?", "Mira Cassia for 120 denars if her sister agrees.", "I cannot offer Mira Cassia for 120 denars." })
                    Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { Charge("worker-a", 120, uncertain) }, roster, uncertain)), "Conditional/questioned/refused evidence passed: " + uncertain);
                Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { valid, valid }, roster, "Mira Cassia for 120 denars.")), "Duplicate participant passed.");
                Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { Charge("unknown", 120, "Mira Cassia for 120 denars.") }, roster, "Mira Cassia for 120 denars.")), "Unknown identity passed.");
                var childRoster = new List<Dictionary<string, object>> { Hero("worker-a", "Mira Cassia", 17) };
                Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { valid }, childRoster, "Mira Cassia for 120 denars.")), "Minor passed quote validation.");
                var dead = Hero("worker-a", "Mira Cassia"); dead["isAlive"] = false;
                Require(Rejects(() => ValidateTavernHouseQuoteCharges(new List<Dictionary<string, object>> { valid }, new List<Dictionary<string, object>> { dead }, "Mira Cassia for 120 denars.")), "A deceased participant passed native availability validation.");
                Require(Rejects(() => ValidateTavernHouseQuoteCharges(Enumerable.Repeat(valid, 5).ToList(), roster, "Mira Cassia for 120 denars.")), "A fifth participant was silently dropped.");
            });
            Check("receipt_replay_is_exact", () => {
                var a = new Dictionary<string, object> { ["visitId"] = "visit", ["agreementId"] = "quote", ["paidGold"] = 120, ["startedDay"] = 42d, ["participantHeroIds"] = new[] { "worker-a" } };
                var b = new Dictionary<string, object>(a); Require(TavernHouseReceiptMatches(a, b), "Exact receipt retry failed.");
                b["paidGold"] = 0; Require(!TavernHouseReceiptMatches(a, b), "Changed payment reused a receipt.");
                b = new Dictionary<string, object>(a); b["participantHeroIds"] = new[] { "madam" }; Require(!TavernHouseReceiptMatches(a, b), "Participant substitution reused a receipt.");
                b = new Dictionary<string, object>(a); b["startedDay"] = 43d; Require(!TavernHouseReceiptMatches(a, b), "Changed campaign day reused a receipt.");
            });
            Check("confirm_returns_immutable_exact_pending_turn_and_validates_current_day", () => {
                var receipt = new Dictionary<string, object> { ["visitId"] = "paid", ["startedDay"] = 10d, ["paidGold"] = 120, ["participantHeroIds"] = new[] { "worker-a", "worker-b" } };
                var state = new Dictionary<string, object> { ["campaignId"] = "campaign", ["timelineId"] = "timeline", ["townId"] = "town", ["conversationSessionId"] = "venue",
                    ["status"] = "visiting", ["playerHeroStringId"] = "player", ["transcriptRevision"] = 3, ["receipt"] = receipt, ["transcript"] = new List<Dictionary<string, object>>() };
                var chat = new Dictionary<string, object> { ["conversationSessionId"] = "venue_visit", ["sceneTurnId"] = "original-beat", ["playerTurnId"] = "original-beat",
                    ["speakerHeroStringId"] = "worker-b", ["playerText"] = "Tell me about your travels." };
                string hash = TavernHouseHash(Json.Serialize(new[] { "Tell me about your travels.", "worker-b", "original-beat" }));
                DispatchTavernHouseDialogue(state, "original-beat_1", hash, chat, () => null, _ => null, () => { });
                state = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(state)); receipt = ReadDictionary(state, "receipt");
                string saved = Json.Serialize(ReadDictionary(state, "pendingDialogue"));
                var first = BuildTavernHouseConfirmationResponse(state, receipt, false); var retry = BuildTavernHouseConfirmationResponse(state, receipt, true);
                var metadata = ReadDictionary(first, "pendingTurn");
                Require(Json.Serialize(metadata) == Json.Serialize(ReadDictionary(retry, "pendingTurn")), "First and repeated confirmation returned different recovery identities.");
                Require(new HashSet<string>(metadata.Keys).SetEquals(new[] { "requestId", "playerTurnId", "sceneTurnId", "speakerHeroStringId", "playerText", "playerRecordedInTranscript" }), "Confirmation exposed internal or unrelated pending request data.");
                Require(ReadString(metadata, "requestId", "") == "original-beat_1" && ReadString(metadata, "playerTurnId", "") == "original-beat"
                    && ReadString(metadata, "sceneTurnId", "") == "original-beat" && ReadString(metadata, "speakerHeroStringId", "") == "worker-b"
                    && ReadString(metadata, "playerText", "") == "Tell me about your travels." && !ReadBool(metadata, "playerRecordedInTranscript", true), "Recovery changed the pending request rather than resuming it.");
                metadata["requestId"] = "caller-change";
                Require(Json.Serialize(ReadDictionary(state, "pendingDialogue")) == saved, "Editing response metadata mutated the durable request marker.");
                state["transcript"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "player", ["phase"] = "visit", ["heroId"] = "player", ["playerTurnId"] = "other-beat" } };
                Require(!ReadBool(ReadDictionary(BuildTavernHouseConfirmationResponse(state, receipt, true), "pendingTurn"), "playerRecordedInTranscript", true), "A different input ID suppressed recovery of the pending player line.");
                ReadDictionaryList(state, "transcript").Single()["playerTurnId"] = "original-beat";
                Require(ReadBool(ReadDictionary(BuildTavernHouseConfirmationResponse(state, receipt, true), "pendingTurn"), "playerRecordedInTranscript", false), "The exact recorded player turn was duplicated after reload.");
                foreach (string key in new[] { "campaignId", "timelineId", "townId", "conversationSessionId", "phaseSessionId", "speakerId", "sceneTurnId", "requestHash" })
                {
                    var other = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(state)); ReadDictionary(other, "pendingDialogue")[key] = "other";
                    Require(Rejects(() => BuildTavernHouseConfirmationResponse(other, ReadDictionary(other, "receipt"), true)), "A mismatched pending scope/identity was returned: " + key);
                }
                state["transcriptRevision"] = 4;
                Require(Rejects(() => BuildTavernHouseConfirmationResponse(state, receipt, true)), "A stale pending revision was returned as resumable.");
                Require(ValidateTavernHouseConfirmationDay(new Dictionary<string, object> { ["worldDay"] = 13d }, 10d) == 13d && ReadDouble(receipt, "startedDay", 0) == 10d, "Delayed confirmation reused or changed the original payment day.");
                foreach (double invalid in new[] { 9d, double.NaN, double.PositiveInfinity, -1d })
                    Require(Rejects(() => ValidateTavernHouseConfirmationDay(new Dictionary<string, object> { ["worldDay"] = invalid }, 10d)), "An invalid or invented native confirmation day passed.");
                Require(Rejects(() => ValidateTavernHouseConfirmationDay(new Dictionary<string, object>(), 10d)), "A missing current native day fell back to the payment day.");
            });
            Check("permanent_recruitment_requires_own_current_consent", () => {
                Require(TavernHouseHasPermanentRecruitmentConsent("I will join your clan as a companion for 100 denars."), "Clear paid permanent consent was rejected.");
                Require(TavernHouseHasPermanentRecruitmentConsent("I agree. I join your clan as your companion, freely."), "Clear free permanent consent was rejected.");
                foreach (string text in new[] {
                    "I will join you in the bedroom for 100 denars.", "I will join your clan if my sister agrees.",
                    "Perhaps I will join your clan as a companion.", "I would join your clan as a companion.",
                    "I will not join your clan as a companion.", "I join your clan. No, I changed my mind.",
                    "*I join your clan as a companion.*", "Mira will join your clan as a companion for 100 denars.",
                    "You said you would join my clan. I agree to spend time together." })
                    Require(!TavernHouseHasPermanentRecruitmentConsent(text), "Visit/conditional/refused/other-person consent passed: " + text);
            });
            Check("dialogue_membership_is_private_and_receipt_bound", () => {
                var state = new Dictionary<string, object> { ["status"] = "negotiating", ["madamId"] = "madam", ["roster"] = roster };
                Require(TavernHousePresentProfiles(state).Select(CharacterIdFrom).SequenceEqual(new[] { "madam" }), "Workers overheard the private negotiation.");
                state["status"] = "visiting"; state["receipt"] = new Dictionary<string, object> { ["participantHeroIds"] = new[] { "worker-b", "worker-a" } };
                Require(TavernHousePresentProfiles(state).Select(CharacterIdFrom).SequenceEqual(new[] { "worker-b", "worker-a" }), "A worker was omitted or the unselected madam was present.");
                state["status"] = "closed"; Require(Rejects(() => TavernHousePresentProfiles(state)), "A closed visit remained usable.");
            });
            Check("initial_madam_greeting_uses_server_owned_opening_and_replay", () => {
                var state = new Dictionary<string, object> { ["status"] = "negotiating", ["madamId"] = "madam", ["transcriptRevision"] = 0, ["transcript"] = new List<Dictionary<string, object>>() };
                var chat = new Dictionary<string, object> { ["speakerHeroStringId"] = "madam", ["playerText"] = "", ["sceneContext"] = "Private visit." };
                Require(ApplyTavernHouseOpening(state, chat) && ReadBool(chat, "approachOpening", false) && ReadString(chat, "turnType", "") == "npc_approach_opening" && ReadBool(chat, "suppressPlayerTranscript", false), "Initial greeting failed the existing NPC-opening input contract.");
                Require(ReadString(chat, "playerText", "") == "" && ReadDictionaryList(state, "transcript").Count == 0, "The greeting fabricated a player utterance.");
                chat["speakerHeroStringId"] = "worker-a"; Require(Rejects(() => ApplyTavernHouseOpening(state, chat)), "An unselected worker opened the negotiation.");
                chat["speakerHeroStringId"] = "madam";
                state["transcript"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["phase"] = "negotiation", ["role"] = "npc", ["text"] = "Welcome." } };
                state["transcriptRevision"] = 1;
                Require(Rejects(() => ApplyTavernHouseOpening(state, chat)), "A completed greeting allowed another blank opening.");
                var cached = new Dictionary<string, object> { ["requestHash"] = "opening-hash", ["visiting"] = false, ["termsPending"] = false,
                    ["response"] = new Dictionary<string, object> { ["ok"] = true, ["reply"] = "Welcome.", ["transcriptRevision"] = 1 } };
                var replay = ReplayTavernHouseCompletedTurn(state, cached, "opening-hash", _ => { throw new InvalidOperationException("Opening was generated twice."); }, () => { });
                Require(ReadString(replay, "reply", "") == "Welcome." && ReadDictionaryList(state, "transcript").Count == 1, "Retry did not reuse the completed greeting.");
                chat["playerText"] = "Hello."; chat["approachOpening"] = true; chat["turnType"] = "npc_approach_opening"; chat["castleOpening"] = true;
                Require(!ApplyTavernHouseOpening(state, chat) && !ReadBool(chat, "approachOpening", true) && ReadString(chat, "turnType", "") == "player_reply" && !chat.ContainsKey("castleOpening"), "Caller flags promoted a later message to an opening.");
                state["status"] = "visiting"; state["transcript"] = new List<Dictionary<string, object>>(); chat["playerText"] = "";
                Require(Rejects(() => ApplyTavernHouseOpening(state, chat)), "A paid visit bypassed the initial-madam-only opening rule.");
            });
            Check("pending_terms_disk_failure_and_replay", () => {
                string fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "reign_tavern_contract_" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(fixture);
                try
                {
                    string path = Path.Combine(fixture, "session.json");
                    var state = new Dictionary<string, object> { ["schema"] = "reign-tavern-house-session-v1", ["status"] = "negotiating", ["transcriptRevision"] = 1,
                        ["transcript"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "npc", ["text"] = "A completed reply." } } };
                    var response = new Dictionary<string, object> { ["ok"] = true, ["reply"] = "A completed reply.", ["transcriptRevision"] = 1 };
                    var pending = new Dictionary<string, object> { ["requestHash"] = "hash", ["termsPending"] = true, ["visiting"] = false, ["response"] = response };
                    state["responses"] = new List<Dictionary<string, object>> { pending }; SaveTavernHouseSession(path, state);
                    int extractions = 0;
                    Action<Dictionary<string, object>> extract = result => { extractions++; result["quote"] = new Dictionary<string, object> { ["agreementId"] = "same-offer" }; };
                    bool diskFailure = false;
                    try { ReplayTavernHouseCompletedTurn(state, pending, "hash", extract, () => { throw new IOException("Injected unavailable volume."); }); }
                    catch (IOException) { diskFailure = true; }
                    Require(diskFailure, "The injected write failure was swallowed.");
                    state = ReadTavernHouseSession(path); pending = ReadDictionaryList(state, "responses").Single();
                    Require(ReadBool(pending, "termsPending", false) && ReadDictionaryList(state, "transcript").Count == 1, "Disk failure destroyed the completed dialogue checkpoint.");
                    var retried = ReplayTavernHouseCompletedTurn(state, pending, "hash", extract, () => SaveTavernHouseSession(path, state));
                    ReplayTavernHouseCompletedTurn(state, pending, "hash", extract, () => SaveTavernHouseSession(path, state));
                    Require(extractions == 2 && ReadDictionaryList(state, "transcript").Count == 1 && ReadString(retried, "reply", "") == "A completed reply.", "Recovery repeated an NPC turn or extracted already-completed terms again.");
                    Require(ReadString(ReplayTavernHouseCompletedTurn(state, pending, "different", extract, () => { }), "code", "") == "receipt_conflict", "Reusing a turn ID with different input was accepted.");
                    state["transcriptRevision"] = 2;
                    var historical = ReplayTavernHouseCompletedTurn(state, pending, "hash", extract, () => { });
                    Require(!historical.ContainsKey("quote") && ReadBool(historical, "historicalReplay", false), "Historical replay revived a superseded offer.");
                    File.WriteAllText(path, "{damaged"); Require(Rejects(() => ReadTavernHouseSession(path)), "Corrupt saved state silently created an empty/new session.");
                }
                finally
                {
                    string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fixture.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fixture).StartsWith("reign_tavern_contract_", StringComparison.Ordinal))
                        throw new InvalidDataException("Fixture cleanup escaped its exact temporary directory.");
                    Directory.Delete(fixture, true);
                }
            });
            Check("first_venue_checkpoint_failure_recovers_exact_exchange_without_provider_replay", () => {
                string fixture = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "reign_tavern_dispatch_" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(fixture);
                try
                {
                    string path = Path.Combine(fixture, "session.json");
                    var state = new Dictionary<string, object> { ["schema"] = "reign-tavern-house-session-v1", ["status"] = "negotiating", ["campaignId"] = "campaign",
                        ["timelineId"] = "timeline", ["conversationSessionId"] = "venue", ["transcriptRevision"] = 0, ["transcript"] = new List<Dictionary<string, object>>() };
                    var chat = new Dictionary<string, object> { ["campaignId"] = "campaign", ["timelineId"] = "timeline", ["conversationSessionId"] = "venue_negotiation",
                        ["sceneTurnId"] = "beat", ["speakerHeroStringId"] = "madam", ["playerText"] = "" };
                    SaveTavernHouseSession(path, state);
                    int providerCalls = 0;
                    Func<Dictionary<string, object>> provider = () => { providerCalls++; return new Dictionary<string, object> { ["ok"] = true, ["reply"] = "Welcome." }; };
                    try { DispatchTavernHouseDialogue(state, "request", "hash", chat, provider, _ => null, () => { throw new IOException("Marker volume unavailable."); }); }
                    catch (IOException) { }
                    Require(providerCalls == 0, "Dialogue dispatched before its durable marker was saved.");
                    state = ReadTavernHouseSession(path);
                    var response = DispatchTavernHouseDialogue(state, "request", "hash", chat, provider, _ => null, () => SaveTavernHouseSession(path, state));
                    Require(providerCalls == 1 && ReadString(response, "reply", "") == "Welcome.", "Initial provider dispatch failed.");
                    // The generic exchange is committed, but the first completed venue write fails.
                    var exchange = new Dictionary<string, object> { ["text"] = "Welcome.", ["payload_json"] = Json.Serialize(BuildConversationTurnStoragePayload(chat)) };
                    state.Remove("pendingDialogue");
                    try { Action failingCompletion = () => { throw new IOException("Completed venue checkpoint unavailable."); }; failingCompletion(); }
                    catch (IOException) { }
                    state = ReadTavernHouseSession(path);
                    response = DispatchTavernHouseDialogue(state, "request", "hash", chat, provider, pending => RecoverTavernHouseDialogueRow(state, pending, exchange), () => SaveTavernHouseSession(path, state));
                    Require(providerCalls == 1 && ReadString(response, "reply", "") == "Welcome." && ReadBool(response, "recoveredDialogue", false), "Recovery repeated the provider or changed the committed reply.");
                    Require(ReadDictionaryList(state, "transcript").Count == 0, "Recovery fabricated a player turn before the completed response is checkpointed.");
                    var unknown = DispatchTavernHouseDialogue(state, "request", "hash", chat, provider, _ => null, () => SaveTavernHouseSession(path, state));
                    Require(providerCalls == 1 && ReadString(unknown, "code", "") == "uncertain_dialogue", "An uncertain provider outcome was repeated.");
                    Require(ReadString(DispatchTavernHouseDialogue(state, "request", "changed", chat, provider, _ => exchange, () => { }), "code", "") == "pending_dialogue", "Different request content reused an in-flight marker.");
                    foreach (string key in new[] { "campaignId", "timelineId", "conversationSessionId", "sceneTurnId", "speakerHeroStringId" })
                    {
                        var other = new Dictionary<string, object>(chat); other[key] = "other";
                        exchange["payload_json"] = Json.Serialize(BuildConversationTurnStoragePayload(other));
                        Require(Rejects(() => RecoverTavernHouseDialogueRow(state, ReadDictionary(state, "pendingDialogue"), exchange)), "Recovery accepted another exchange identity: " + key);
                    }
                }
                finally
                {
                    string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!fixture.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fixture).StartsWith("reign_tavern_dispatch_", StringComparison.Ordinal))
                        throw new InvalidDataException("Dispatch fixture cleanup escaped its exact temporary directory.");
                    Directory.Delete(fixture, true);
                }
            });
            Check("definite_dialogue_failure_retries_without_repaying_and_uncertainty_stays_pending", () => {
                var chat = new Dictionary<string, object> { ["conversationSessionId"] = "venue_visit", ["sceneTurnId"] = "beat", ["speakerHeroStringId"] = "worker-a", ["playerText"] = "Hello." };
                foreach (string stage in new[] { "llm", "character_construction" })
                {
                    var state = new Dictionary<string, object> { ["status"] = "visiting", ["transcriptRevision"] = 3,
                        ["receipt"] = new Dictionary<string, object> { ["visitId"] = "paid-visit", ["paidGold"] = 120 } };
                    string durable = ""; int calls = 0;
                    Action save = () => durable = Json.Serialize(state);
                    var failed = new Dictionary<string, object> { ["ok"] = false, ["stage"] = stage, ["llm"] = new Dictionary<string, object> { ["ok"] = false, ["error"] = "Provider unavailable." } };
                    var result = DispatchTavernHouseDialogue(state, "request", "hash", chat, () => { calls++; return failed; }, _ => null, save);
                    state = Json.Deserialize<Dictionary<string, object>>(durable);
                    Require(ReadBool(result, "retryable", false) && !state.ContainsKey("pendingDialogue") && ReadInt(ReadDictionary(state, "receipt"), "paidGold", 0) == 120, "A known pre-completion failure stranded or changed the paid visit: " + stage);
                    result = DispatchTavernHouseDialogue(state, "request", "hash", chat, () => { calls++; return new Dictionary<string, object> { ["ok"] = true, ["reply"] = "Welcome." }; }, _ => null, save);
                    Require(calls == 2 && ReadBool(result, "ok", false) && ReadString(ReadDictionary(state, "receipt"), "visitId", "") == "paid-visit", "The same paid visit could not retry a definite failed response.");
                }
                foreach (var ambiguous in new[] {
                    new Dictionary<string, object> { ["ok"] = false },
                    new Dictionary<string, object> { ["ok"] = false, ["stage"] = "llm" },
                    new Dictionary<string, object> { ["ok"] = false, ["stage"] = "llm", ["llm"] = new Dictionary<string, object> { ["ok"] = true } },
                    new Dictionary<string, object> { ["ok"] = false, ["stage"] = "character_construction", ["reply"] = "An existing reply." },
                    new Dictionary<string, object> { ["ok"] = false, ["stage"] = "character_construction", ["conversationExchange"] = new Dictionary<string, object> { ["sessionId"] = "saved" } } })
                    Require(!TavernHouseDefiniteDialogueFailure(ambiguous), "An unknown or completed outcome was classified as safe to repeat.");
                var uncertain = new Dictionary<string, object> { ["status"] = "visiting" }; string checkpoint = ""; int attempts = 0;
                Action persist = () => checkpoint = Json.Serialize(uncertain);
                try { DispatchTavernHouseDialogue(uncertain, "request", "hash", chat, () => { attempts++; throw new IOException("Unknown provider outcome."); }, _ => null, persist); }
                catch (IOException) { }
                uncertain = Json.Deserialize<Dictionary<string, object>>(checkpoint);
                var blocked = DispatchTavernHouseDialogue(uncertain, "request", "hash", chat, () => { attempts++; return null; }, _ => null, persist);
                Require(attempts == 1 && ReadString(blocked, "code", "") == "uncertain_dialogue", "A thrown or unknown outcome lost its durable marker and repeated the provider.");
            });
            Check("authored_native_foundation_enters_normal_narrative", () => {
                var hero = Hero("tavern-native", "Aelia Varro", 25);
                const string biography = "Aelia kept accounts before choosing work at the tavern house. Warm and observant, she remembers small kindnesses.";
                hero["nativeEncyclopediaText"] = biography;
                hero["traits"] = new Dictionary<string, object> { ["honor"] = -1, ["calculating"] = 1, ["mercy"] = 0, ["valor"] = 1, ["generosity"] = -1 };
                hero["skills"] = new Dictionary<string, object> { ["charm"] = 210, ["roguery"] = 205 };
                var traits = BuildTraitDocument(hero); var facts = NarrativeReadableFacts(hero);
                Require(ReadInt(ReadDictionary(traits, "visibleBannerlordTraits"), "honor", 0) == -1 && ReadInt(ReadDictionary(traits, "nativeSkills"), "charm", 0) == 210, "Authored native traits/skills were replaced.");
                Require(ReadString(facts, "nativeEncyclopediaText", "") == biography && ReadString(traits, "nativeDescriptionEvidence", "") == biography, "Authored biography/personality was lost before narrative construction.");
                Require(ReadDictionary(traits, "courtVirtues")?.ContainsKey("judgment") == true, "Normal derived Judgment is unavailable.");
            });
            Check("reference_membership_player_and_selected_only", () => {
                var state = new Dictionary<string, object> { ["player"] = Hero("player", "Player"), ["playerHeroStringId"] = "player", ["roster"] = roster,
                    ["receipt"] = new Dictionary<string, object> { ["participantHeroIds"] = new[] { "worker-b", "worker-a" } } };
                var refs = TavernHouseSceneProfiles(state);
                Require(refs.Select(CharacterIdFrom).SequenceEqual(new[] { "player", "worker-b", "worker-a" }), "Reference identities/order leaked the unselected madam or omitted the player.");
                ReadDictionary(state, "receipt")["participantHeroIds"] = new[] { "worker-a", "missing" };
                Require(Rejects(() => TavernHouseSceneProfiles(state)), "A missing reference identity was silently omitted.");
            });
            Check("native_tavern_full_outfit_selector_is_identity_bound", () => {
                var snapshot = new Dictionary<string, object> { ["schema"] = "reign-native-portrait-snapshot-v1", ["campaignId"] = "campaign", ["heroStringId"] = "worker-a", ["portraitSourceProfile"] = ResidentOutfitRenderContract };
                var payload = new Dictionary<string, object> { ["campaignId"] = "campaign", ["heroStringId"] = "worker-a", ["nativeCharacterSnapshot"] = snapshot };
                Require(PreserveResidentClothing(payload), "Explicit tavern source selector failed to preserve the worn outfit.");
                Require(!snapshot.ContainsKey("encounteredResident"), "The full-outfit selector requires fabricated resident metadata.");
                Require(BuildResidentPortraitPrompt(payload).Contains("Retain exactly the clothes"), "Selected portrait prompt changes the native outfit.");
                snapshot["portraitSourceProfile"] = "unknown"; Require(!PreserveResidentClothing(payload), "An unknown selector changed portrait behavior.");
                snapshot["portraitSourceProfile"] = ResidentOutfitRenderContract; snapshot["heroStringId"] = "other";
                Require(!PreserveResidentClothing(payload), "A different identity supplied the outfit selector.");
                snapshot["heroStringId"] = "worker-a"; snapshot["campaignId"] = "other";
                Require(!PreserveResidentClothing(payload), "A different campaign supplied the outfit selector.");
                snapshot["campaignId"] = "campaign"; payload["promptPurpose"] = "tavern_house_scene";
                Require(!PreserveResidentClothing(payload), "A scene request inherited single-character portrait framing.");
            });
            Check("tavern_clothing_prompt_routes_all_adult_staff_to_adult_portraits", () => {
                var tavern = new Dictionary<string, object> { ["schema"] = 1, ["heroStringId"] = "worker-a",
                    ["castId"] = "reign_tavern_town_EW2_01", ["madam"] = false };
                var snapshot = new Dictionary<string, object> { ["schema"] = "reign-native-portrait-snapshot-v1",
                    ["campaignId"] = "campaign", ["heroStringId"] = "worker-a", ["age"] = 25,
                    ["portraitSourceProfile"] = ResidentOutfitRenderContract, ["tavernHouse"] = tavern };
                var payload = new Dictionary<string, object> { ["campaignId"] = "campaign", ["heroStringId"] = "worker-a",
                    ["nativeCharacterSnapshot"] = snapshot, ["promptPurpose"] = "portrait", ["gender"] = "male",
                    ["ageYears"] = 25, ["physicalConfidence"] = new Dictionary<string, object> { ["score"] = 10 } };
                Require(IsTavernHousePortrait(payload) && PreserveResidentClothing(payload)
                    && UsesAdultPortraitClothingEdit(payload), "An adult male worker did not enter the Tavern clothing edit.");
                string workerPrompt = BuildAdultPortraitClothingPrompt(payload);
                Require(workerPrompt.Contains("TAVERN HOUSE worker") && workerPrompt.Contains("Change only the clothing")
                    && !workerPrompt.Contains("[TAVERN ROLE]") && !workerPrompt.Contains("Culturally appropriate"),
                    "The common clothing prompt was not expanded or replaced by a culture prompt.");
                tavern["madam"] = true;
                Require(BuildAdultPortraitClothingPrompt(payload).Contains("TAVERN HOUSE madam"), "The same prompt did not identify the madam.");
                var settings = new Dictionary<string, object> { ["adultPortraitProvider"] = "OpenRouter",
                    ["adultPortraitOpenRouterImageModel"] = "selected-adult-portrait-model", ["portraitProvider"] = "NanoGPT" };
                var profile = ResolveImageGenerationProfile(settings, "adultPortrait");
                Require(profile.Name == "adultPortrait" && profile.Provider == "OpenRouter"
                    && profile.OpenRouterModel == "selected-adult-portrait-model", "Tavern clothing bypassed the configured adult portrait profile.");
                snapshot["age"] = 17;
                Require(!UsesAdultPortraitClothingEdit(payload), "A minor entered the Tavern clothing edit.");
                snapshot["age"] = 25; tavern["heroStringId"] = "other";
                Require(!UsesAdultPortraitClothingEdit(payload), "An unrelated Tavern identity entered the clothing edit.");
                tavern["heroStringId"] = "worker-a"; payload["promptPurpose"] = "tavern_house_scene";
                Require(!UsesAdultPortraitClothingEdit(payload), "A scene request entered the portrait clothing edit.");
            });
            Check("reference_sheet_preserves_every_cell", () => {
                byte[] Solid(byte r, byte g, byte b) { var pixels = new byte[8 * 16 * 4]; for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255; } return PngEncoder.EncodeRgba(pixels, 8, 16); }
                byte[] red = Solid(255, 0, 0), blue = Solid(0, 0, 255);
                byte[] rgba = PngReencode.DecodeToRgba(TavernHouseReferenceSheet(new List<byte[]> { red, blue }), out int w, out int h);
                Require(w == 640 && h == 448, "Sheet geometry is incorrect.");
                Require(rgba[(224 * w + 160) * 4] == 255 && rgba[(224 * w + 480) * 4 + 2] == 255, "Reference source order changed or a cell was empty.");
                Require(Rejects(() => TavernHouseReferenceSheet(new List<byte[]> { red })), "Sheet omitted the player's companion.");
                byte[] square = NormalizeTavernHouseSquare(red); PngReencode.DecodeToRgba(square, out w, out h);
                Require(w == 1024 && h == 1024, "Scene did not fit the registered square aperture.");
            });
            Check("editable_prompt_pack_and_fixed_boundary", () => {
                var defaults = TavernHousePromptDefaults(); Require(defaults.Count == 5 && defaults.Values.All(x => !string.IsNullOrWhiteSpace(x)), "An editable prompt is missing.");
                Require(defaults["tavern_house_look_again_summary.txt"].Contains("complete attributed conversation"), "Summary default truncates or ignores full conversation.");
                Require(defaults["tavern_house_look_again_image.txt"].Contains("{summary}") && defaults["tavern_house_arrival_image.txt"].Contains("{participants}"), "Image prompt lost summary/identity placeholders.");
                Require(defaults["tavern_house_portrait_clothing.txt"].Contains("[TAVERN ROLE]"), "Tavern clothing prompt lost its role token.");
                var meta = PromptMetadataByName()["tavern_house_portrait_clothing.txt"];
                Require(ReadBool(meta, "visible", false) && ReadString(meta, "category", "") == "image"
                    && ReadString(meta, "label", "") == "Tavern House: Portrait Clothing",
                    "Tavern clothing prompt is not visible in the WebUI image prompt library.");
                Require(TavernHouseImageBoundary.Contains("Everyone remains clothed") && TavernHouseImageBoundary.Contains("include the player visibly") && TavernHouseImageBoundary.Contains("Never depict sexual acts"), "Runtime scene boundary or player identity is missing.");
            });
            Check("look_again_uses_configured_adult_event_profile_with_safe_scene_contract", () => {
                var state = new Dictionary<string, object> { ["campaignId"] = "campaign", ["timelineId"] = "timeline" };
                var settings = new Dictionary<string, object> { ["adultSceneryProvider"] = "OpenRouter", ["adultSceneryOpenRouterImageModel"] = "selected-adult-event-model",
                    ["sceneryProvider"] = "NanoGPT", ["sceneryNanoGptImageModel"] = "selected-normal-event-model" };
                foreach (string kind in new[] { "arrival", "look_again" })
                {
                    var request = BuildTavernHouseImageRequest(state, kind, "scene-key", "A quiet clothed conversation.", new byte[] { 1, 2 });
                    var profile = ResolveImageGenerationProfile(settings, ReadString(request, "promptPurpose", ""));
                    if (kind == "look_again") Require(profile.Name == "adultScenery" && profile.Provider == "OpenRouter" && profile.OpenRouterModel == "selected-adult-event-model", "Look Again bypassed the configured adult event provider or model.");
                    else Require(profile.Name == "scenery" && profile.Provider == "NanoGPT" && profile.NanoGptModel == "selected-normal-event-model", "Arrival bypassed the normal event provider or model.");
                    Require(ReadString(request, "prompt", "").EndsWith(TavernHouseImageBoundary, StringComparison.Ordinal), "Changing image profiles removed the fixed non-explicit scene contract.");
                    Require(ReadString(request, "sceneInterface", "") == "tavern_house" && ReadString(request, "cacheKey", "") == "tavern_scene_scene-key" && ReadString(request, "outputSize", "") == "1024x1024", "Image routing lost the tavern interface, cache namespace, or scene aperture.");
                }
                Require(Rejects(() => BuildTavernHouseImageRequest(state, "unknown", "key", "", new byte[] { 1 })), "An unknown scene kind silently chose a provider profile.");
            });
            results.AddRange(RunTavernHouseRosterTests());
            results.AddRange(RunPreparedTavernPortraitSourceTests());
            return results;
        }
    }
}
