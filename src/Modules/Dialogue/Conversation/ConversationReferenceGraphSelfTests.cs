using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunConversationReferenceGraphSelfTests()
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value))
                throw new InvalidOperationException("Reference graph contracts require isolated validation.");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict("caseId", "reference_graph_" + id,
                "passed", passed, "summary", id, "data", data));
            string campaign = "reference_graph_test_" + Guid.NewGuid().ToString("N"), oldMode = ConversationReferenceValidationMode.Value;
            const string snapshotName = "reference_graph_roundtrip";
            bool snapshotCreated = false;
            string linkedLetterId = "", letterOriginEvent = "";
            const string original = "I will open the north gate only if you bring the red flag. The orchard visit is a separate promise.";
            try
            {
                ConversationReferenceValidationMode.Value = "off";
                string disabledPrefix = BuildDialogueGlobalPrefix(false, false, new List<Dictionary<string, object>>());
                ConversationReferenceValidationMode.Value = "capture";
                foreach (string renderMode in new[] { "off", "enabled" })
                {
                    ConversationReferenceValidationMode.Value = renderMode;
                    foreach (var expected in new[] {
                        (Kind: "accepted_exchange", Canonical: true), (Kind: "targeted_historical_exchange", Canonical: true),
                        (Kind: "reported_exchange", Canonical: true), (Kind: "resolved_sequence", Canonical: true),
                        (Kind: "letter", Canonical: true), (Kind: "reference_graph_exchange", Canonical: true),
                        (Kind: "reference_graph_letter", Canonical: true), (Kind: "reported_memory", Canonical: false),
                        (Kind: "reviewed_durable_recollection", Canonical: false), (Kind: "obligation", Canonical: false),
                        (Kind: "unknown_future_kind", Canonical: false) })
                    {
                        string kind = expected.Kind;
                        var evidence = new MemoryEvidence { SourceId = "canonical_row_not_for_derived_disclosure", SourceHash = "source_hash_control",
                            Kind = kind, Owner = "npc", Text = "A remembered condition; parent source=original_turn_control.", Required = true };
                        string rendered = RenderMemoryEvidence(evidence, "m1");
                        bool linkable = renderMode == "enabled" && expected.Canonical;
                        var record = ContinuityPromptRecords(rendered, false).Single();
                        var packet = new MemoryEvidencePacket(); packet.Selected.Add(evidence);
                        var audit = ReadDictionaryList(PrecisionEvidenceAudit(packet, new MemoryQueryPlan { Observer = "npc" }, "precision"), "records").Single();
                        Check("only_linkable_kind_renders_canonical_" + renderMode + "_" + kind,
                            record.SourceId == (linkable ? evidence.SourceId : "m1")
                            && rendered.Contains(evidence.SourceId, StringComparison.Ordinal) == linkable
                            && rendered.Contains("source=original_turn_control", StringComparison.Ordinal)
                            && ReadString(audit, "alias", "") == "m1" && ReadString(audit, "sourceId", "") == evidence.SourceId
                            && ReadString(audit, "sourceHash", "") == evidence.SourceHash);
                    }
                }
                ConversationReferenceValidationMode.Value = "capture";
                Check("capture_mode_extends_actual_json_example", TryParseStrictJsonObject(ConversationActionOutputSchema("{\"reply\":\"\"}", false))
                    ?.ContainsKey("memoryReferences") == true && ConversationReferenceInstructions().Contains("Evaluate player_input and npc_reply independently"));
                var examples = ReadDictionaryList(TryParseStrictJsonObject(ConversationActionOutputSchema("{\"reply\":\"\"}", false)), "memoryReferences");
                Check("output_examples_show_both_origins_and_all_six_fields", examples.Count == 2
                    && examples.Select(p => ReadString(p, "origin", "")).ToHashSet().SetEquals(new[] { "player_input", "npc_reply" })
                    && examples.All(p => p.Count == 6 && new[] { "origin", "targetType", "targetId", "relation", "originQuote", "targetQuote" }.All(p.ContainsKey)));
                string orderedExample = ConversationActionOutputSchema("{\"reply\":\"\",\"memoryWrites\":[]}", false);
                Check("output_example_preserves_reply_before_writes_and_references", orderedExample.IndexOf("\"reply\"", StringComparison.Ordinal)
                    < orderedExample.IndexOf("\"memoryWrites\"", StringComparison.Ordinal)
                    && orderedExample.IndexOf("\"memoryWrites\"", StringComparison.Ordinal) < orderedExample.IndexOf("\"memoryReferences\"", StringComparison.Ordinal));
                var typeOffer = new ConversationReferenceOffer();
                typeOffer.Sources["letter:unique"] = new ConversationReferenceSource { Type = "letter", Id = "unique" };
                Check("unique_offered_id_owns_redundant_target_type", SelectOfferedConversationReference(typeOffer, "turn", "unique", out string typeError)?.Type == "letter" && typeError == "");
                Check("target_id_resolution_is_exact_and_offer_only", SelectOfferedConversationReference(typeOffer, "turn", "Unique", out _) == null
                    && SelectOfferedConversationReference(typeOffer, "letter", " unique", out _) == null
                    && SelectOfferedConversationReference(typeOffer, "letter", "not_offered", out _) == null
                    && SelectOfferedConversationReference(typeOffer, "summary", "unique", out typeError) == null && typeError == "invalid_reference_target_type");
                typeOffer.Sources["turn:unique"] = new ConversationReferenceSource { Type = "turn", Id = "unique" };
                Check("type_collision_rejected_even_when_claimed_type_matches", SelectOfferedConversationReference(typeOffer, "turn", "unique", out typeError) == null
                    && typeError == "ambiguous_reference_source_id" && SelectOfferedConversationReference(typeOffer, "letter", "unique", out typeError) == null
                    && typeError == "ambiguous_reference_source_id");
                string generation;
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsureConversationReferenceGraphSchema(c);
                    generation = ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
                    foreach (string session in new[] { "old", "current", "private", "foreign", "future", "public_other" })
                        ReignDatabase.ExecuteSql(c, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,$npc,'player',$status,1,1,$audience,$payload);",
                            TestDict("session", session, "campaign", campaign, "npc", session == "private" ? "other" : "npc",
                                "status", session == "current" ? "open" : "closed", "audience", session == "private" ? "[\"other\",\"player\"]"
                                    : session == "public_other" ? "[\"npc\",\"other\",\"player\"]" : "[\"npc\",\"player\"]",
                                "payload", Json.Serialize(TestDict("timelineId", session == "foreign" ? "foreign" : "main"))));
                    void Turn(string id, string session, string role, string speaker, string text, double day, string[] audience, int ordinal)
                        => InsertConversationTurn(c, id, session, id, ordinal, session + "_exchange", role, speaker,
                            speaker == "player" ? "Player" : "Mira", text, "in_person", day, (long)day, "town", audience.ToList(),
                            TestDict("timelineId", session == "foreign" ? "foreign" : "main", "worldDay", day));
                    Turn("old_player", "old", "player", "player", "What are your terms for the north gate?", 10, new[] { "npc", "player" }, 0);
                    Turn("old_npc", "old", "npc", "npc", original, 10, new[] { "npc", "player" }, 1);
                    Turn("secret_npc", "private", "npc", "other", "SECRET-GRAPH-717: bring the blue flag to the hidden orchard.", 10, new[] { "other", "player" }, 0);
                    Turn("foreign_npc", "foreign", "npc", "npc", "I promised a different gate on the foreign branch.", 10, new[] { "npc", "player" }, 0);
                    Turn("future_npc", "future", "npc", "npc", "I will tell you the result after next month's tournament.", 40, new[] { "npc", "player" }, 0);
                    Turn("other_heard_npc", "public_other", "npc", "other", "Bring the green flag to the west gate before dusk.", 10, new[] { "npc", "other", "player" }, 0);
                }
                int Edges(string eventId = "")
                {
                    using var c = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReadInt(ReignDatabase.QuerySql(c, "SELECT COUNT(*) AS n FROM conversation_reference_edges" + (eventId.Length == 0 ? "" : " WHERE source_event_id=$id") + ";",
                        eventId.Length == 0 ? null : TestDict("id", eventId)).Single(), "n", 0);
                }
                AcceptedConversationUnit Unit(string name)
                {
                    var payload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 20d,
                        "sceneTurnId", name, "turnId", name, "conversationSessionId", "current", "heroStringId", "npc",
                        "playerHeroStringId", "player", "locationId", "town", "participants", new[] { "npc", "player" },
                        "identityView", TestDict("knowsIdentity", true), "playerText", "Do you remember the red flag condition?");
                    const string reply = "I said I would open the north gate only if you brought the red flag.";
                    var proposals = new List<Dictionary<string, object>> {
                        TestDict("origin", "player_input", "targetType", "turn", "targetId", "old_npc", "relation", "refers_to",
                            "originQuote", "red flag condition", "targetQuote", "only if you bring the red flag"),
                        TestDict("origin", "npc_reply", "targetType", "turn", "targetId", "old_npc", "relation", "answers_about",
                            "originQuote", "open the north gate", "targetQuote", "open the north gate only if you bring the red flag") };
                    return new AcceptedConversationUnit { Campaign = campaign, Hero = "npc", Player = "player", Location = "town",
                        PlayerName = "Player", HeroName = "Mira", EventId = AcceptedConversationEventId(campaign, "npc", payload),
                        EventType = "dialogue_turn", PlayerText = ReadString(payload, "playerText", ""), Reply = reply, Timestamp = 20,
                        Generation = generation, Payload = payload, Profile = TestDict("name", "Mira"), Motive = TestDict(),
                        Parsed = TestDict("reply", reply, "memoryReferences", proposals), CommitEffects = false, RetrievalEligible = false,
                        Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                }
                string Packet(string id = "old_npc", string body = original) => RenderContinuityRecord(id, "targeted_historical_exchange", "npc", "old", 10, 95, true,
                    "- [Day 10] [source=" + id + " role=npc] Mira: " + body);
                void Bind(AcceptedConversationUnit unit, string expected, string delivered = null)
                {
                    CaptureRequiredPromptEvidence(unit.Payload, "selectedContextAndMemory", expected);
                    BindDeliveredMemoryRecallEvidence(unit.Payload, "npc", new() { TestDict("role", "user", "content", delivered ?? expected) }, "Mira");
                }
                Dictionary<string, object> Publish(AcceptedConversationUnit unit, Action<string> fault = null) => ReadDictionary(
                    PublishAcceptedConversationUnit(unit, accept => StoreConversationExchange(campaign, unit.Payload, "npc", "player", "Player", "Mira",
                        unit.PlayerText, unit.Reply, unit.EventId, unit.Timestamp, acceptance: accept), fault), "acceptedUnit");
                void NeutralReply(AcceptedConversationUnit unit)
                {
                    unit.Reply = "I heard your question."; unit.Parsed["reply"] = unit.Reply;
                    foreach (var p in ReadDictionaryList(unit.Parsed, "memoryReferences"))
                        if (ReadString(p, "origin", "") == "npc_reply") p["originQuote"] = "your question";
                }
                var first = Unit("linked"); Bind(first, Packet());
                var accepted = Publish(first);
                Check("both_accepted_inputs_and_replies_link_to_original", ReadInt(ReadDictionary(accepted, "references"), "stored", 0) == 2 && Edges(first.EventId) == 2, accepted);
                Check("accepted_replay_is_idempotent", ReadBool(Publish(first), "idempotentReplay", false) && Edges(first.EventId) == 2);
                var otherHeard = Unit("other_speaker_heard");
                otherHeard.PlayerText = "What did the other Mira say about the green flag?";
                otherHeard.Payload["playerText"] = otherHeard.PlayerText;
                otherHeard.Reply = "She asked you to bring the green flag to the west gate before dusk.";
                otherHeard.Parsed["reply"] = otherHeard.Reply;
                foreach (var proposal in ReadDictionaryList(otherHeard.Parsed, "memoryReferences"))
                {
                    proposal["targetId"] = "other_heard_npc";
                    proposal["originQuote"] = ReadString(proposal, "origin", "") == "player_input" ? "green flag" : "bring the green flag";
                    proposal["targetQuote"] = "Bring the green flag to the west gate before dusk.";
                }
                Bind(otherHeard, RenderContinuityRecord("other_heard_npc", "targeted_historical_exchange", "npc", "public_other", 10, 95, true,
                    "- [Day 10] [source=other_heard_npc role=npc] Mira: Bring the green flag to the west gate before dusk."));
                Check("eligible_other_character_source_links_by_id_despite_shared_name", ReadInt(ReadDictionary(Publish(otherHeard), "references"), "stored", 0) == 2);
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var meta = ReignDatabase.QuerySql(c, "SELECT payload_json FROM conversation_reference_edges WHERE source_event_id=$id;", TestDict("id", first.EventId))
                        .Select(row => TryParseJsonObject(ReadString(row, "payload_json", "{}"))).ToList();
                    Check("links_never_certify_facts_or_cancel_promises", meta.Count == 2 && meta.All(m => ReadString(m, "status", "") == "proposed"
                        && !ReadBool(m, "nativeFactVerified", true) && !ReadBool(m, "relationEntailmentVerified", true) && !ReadBool(m, "changesAgreementStatus", true))
                        && ReadString(ReignDatabase.QuerySql(c, "SELECT text FROM conversation_turns WHERE turn_id='old_npc';").Single(), "text", "") == original);
                    Check("existing_lineage_table_does_not_receive_graph_rows", ReignDatabase.QuerySql(c,
                        "SELECT document_id FROM memory_sources WHERE document_type LIKE 'conversation_reference%';").Count == 0);
                }
                var failed = Unit("rollback"); Bind(failed, Packet());
                bool rolledBack = false;
                try { Publish(failed, phase => { if (phase == "after_reference_graph") throw new InvalidOperationException("Injected reference rollback."); }); }
                catch (InvalidOperationException) { rolledBack = true; }
                Check("graph_and_accepted_sources_roll_back_together", rolledBack && Edges(failed.EventId) == 0);
                foreach (var item in new[] { ("secret_npc", "SECRET-GRAPH-717: bring the blue flag to the hidden orchard."),
                    ("foreign_npc", "I promised a different gate on the foreign branch."),
                    ("future_npc", "I will tell you the result after next month's tournament.") })
                {
                    var unit = Unit(item.Item1);
                    NeutralReply(unit);
                    foreach (var p in ReadDictionaryList(unit.Parsed, "memoryReferences")) { p["targetId"] = item.Item1; p["targetQuote"] = item.Item2; }
                    Bind(unit, Packet(item.Item1, item.Item2));
                    Check(item.Item1 + "_cannot_become_offered_evidence", ReadInt(ReadDictionary(Publish(unit), "references"), "stored", -1) == 0 && Edges(unit.EventId) == 0);
                }
                var omitted = Unit("not_delivered"); NeutralReply(omitted); Bind(omitted, Packet(), "An unrelated source was supplied.");
                Check("selected_but_undelivered_source_cannot_link", ReadInt(ReadDictionary(Publish(omitted), "references"), "stored", -1) == 0);
                var graphOnly = Unit("graph_only"); NeutralReply(graphOnly);
                Bind(graphOnly, RenderContinuityRecord("old_npc", "reference_graph_exchange", "npc", "old", 10, 10, false,
                    "- [Day 10] [source=old_npc role=npc] Mira: " + original));
                Check("graph_only_source_cannot_bootstrap_new_links", ReadInt(ReadDictionary(Publish(graphOnly), "references"), "stored", -1) == 0);
                var falseQuote = Unit("wrong_quote"); Bind(falseQuote, Packet());
                ReadDictionaryList(falseQuote.Parsed, "memoryReferences")[0]["targetQuote"] = "You may bring any flag without conditions.";
                var rejected = ReadDictionary(Publish(falseQuote), "references");
                Check("fabricated_target_quote_rejected", ReadInt(rejected, "stored", 0) == 1 && ReadDictionaryList(rejected, "rejected").Any(r => ReadString(r, "reason", "") == "reference_quote_outside_delivered_view"));
                var changed = Unit("changed_source"); NeutralReply(changed); Bind(changed, Packet());
                using (var c = ReignDatabase.OpenCampaignConnection(campaign)) ReignDatabase.ExecuteSql(c, "UPDATE conversation_turns SET text=$text WHERE turn_id='old_npc';", TestDict("text", original + " Changed later."));
                Check("source_revision_invalidates_old_offer", ReadInt(ReadDictionary(Publish(changed), "references"), "stored", -1) == 0);
                using (var c = ReignDatabase.OpenCampaignConnection(campaign)) ReignDatabase.ExecuteSql(c, "UPDATE conversation_turns SET text=$text WHERE turn_id='old_npc';", TestDict("text", original));
                var attempt = Unit("ambiguous_attempt"); NeutralReply(attempt); Bind(attempt, Packet()); Bind(attempt, Packet(), "The repair omitted the original.");
                Check("different_attempt_offers_cannot_be_borrowed", ReadString(ReadDictionary(Publish(attempt), "references"), "status", "") == "ambiguous_attempt_offer");
                var suppressed = Unit("suppressed"); Bind(suppressed, Packet());
                using (var c = ReignDatabase.OpenCampaignConnection(campaign)) Check("failed_response_cannot_supply_player_side_links",
                    ReadInt(StoreAcceptedConversationReferences(c, suppressed, new(), true), "stored", -1) == 0);

                List<Dictionary<string, object>> sources;
                using (var c = ReignDatabase.OpenCampaignConnection(campaign)) sources = ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active' ORDER BY turn_order;", TestDict("id", first.EventId));
                string firstPlayerId = ReadString(sources.Single(r => ReadString(r, "role", "") == "player"), "turn_id", "");
                var chain = Unit("second_mention"); NeutralReply(chain);
                foreach (var p in ReadDictionaryList(chain.Parsed, "memoryReferences"))
                {
                    p["targetId"] = firstPlayerId;
                    p["targetQuote"] = first.PlayerText;
                }
                Bind(chain, RenderContinuityRecord(firstPlayerId, "targeted_historical_exchange", "npc", "current", 20, 95, true,
                    "- [Day 20] [source=" + firstPlayerId + " role=player] Player: " + first.PlayerText));
                Check("later_mention_links_to_accepted_player_input", ReadInt(ReadDictionary(Publish(chain), "references"), "stored", 0) == 2);
                string LinkedInput(string name, params (string Id, string Quote)[] targets)
                {
                    var unit = Unit(name); NeutralReply(unit);
                    unit.PlayerText = "The red flag condition came up during our " + name.Replace('_', ' ') + ".";
                    unit.Payload["playerText"] = unit.PlayerText;
                    unit.Parsed["memoryReferences"] = targets.Select(t => TestDict("origin", "player_input", "targetType", "turn",
                        "targetId", t.Id, "relation", "refers_to", "originQuote", "red flag condition", "targetQuote", t.Quote)).ToList();
                    var packets = new List<string>();
                    using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                    foreach (var target in targets)
                    {
                        var source = ResolveConversationReferenceSource(c, "turn", target.Id,
                            new Program.KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "main", WorldDay = 21 });
                        string role = ReadString(source.Row, "role", "");
                        packets.Add(RenderContinuityRecord(target.Id, "targeted_historical_exchange", "npc", ReadString(source.Row, "session_id", ""),
                            source.Day, 95, true, "- [Day 20] [source=" + target.Id + " role=" + role + "] "
                            + ReadString(source.Row, "speaker_name", source.Speaker) + ": " + source.View));
                    }
                    Bind(unit, string.Join("\n\n", packets));
                    Check("chain_fixture_published_" + name, ReadInt(ReadDictionary(Publish(unit), "references"), "stored", 0) == targets.Length);
                    using var read = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReadString(ReignDatabase.QuerySql(read, "SELECT * FROM conversation_turns WHERE event_id=$event AND role='player' AND status='active';",
                        TestDict("event", unit.EventId)).Single(), "turn_id", "");
                }
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var access = new Program.KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "main", WorldDay = 21 };
                    var chainRows = ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active';", TestDict("id", chain.EventId));
                    var seeds = chainRows.Select(r => ("turn", ReadString(r, "turn_id", ""))).ToList();
                    var one = ExpandConversationReferences(c, seeds, access, generation, 1, 12, TestDict());
                    var two = ExpandConversationReferences(c, seeds, access, generation, 2, 12, TestDict());
                    Check("two_hops_recover_antecedent_beyond_latest_mention", one.Any(s => s.Id == firstPlayerId)
                        && !one.Any(s => s.Id == "old_npc") && two.Any(s => s.Id == "old_npc"));
                    var bounded = ExpandConversationReferences(c, seeds, access, generation, 2, 1, TestDict());
                    Check("node_limit_is_enforced", bounded.Count == 1);
                    // Keeping a public target never makes a hidden origin traversable.
                    var firstRow = sources.Single(r => ReadString(r, "role", "") == "player");
                    string audience = ReadString(firstRow, "participants_json", "");
                    ReignDatabase.ExecuteSql(c, "UPDATE conversation_turns SET participants_json='[\"player\",\"other\"]' WHERE turn_id=$id;", TestDict("id", firstPlayerId));
                    Check("hidden_origin_blocks_both_ends_of_a_link", ExpandConversationReferences(c,
                        new() { ("turn", firstPlayerId) }, access, generation, 2, 12, TestDict()).Count == 0);
                    ReignDatabase.ExecuteSql(c, "UPDATE conversation_turns SET participants_json=$audience WHERE turn_id=$id;", TestDict("id", firstPlayerId, "audience", audience));
                }
                string chainPlayerId;
                using (var c = ReignDatabase.OpenCampaignConnection(campaign)) chainPlayerId = ReadString(ReignDatabase.QuerySql(c,
                    "SELECT * FROM conversation_turns WHERE event_id=$event AND role='player' AND status='active';", TestDict("event", chain.EventId)).Single(), "turn_id", "");
                string third = LinkedInput("third_mention", (chainPlayerId, "red flag condition"));
                string fourth = LinkedInput("fourth_mention", (third, "red flag condition"));
                string sibling = LinkedInput("sibling_callback", ("old_npc", "only if you bring the red flag"));
                string star = LinkedInput("star_callback", ("old_npc", "only if you bring the red flag"));
                string diamond = LinkedInput("diamond_callback", (third, "red flag condition"), (sibling, "red flag condition"));
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var access = new Program.KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "main", WorldDay = 21 };
                    var deepAudit = TestDict();
                    var deep = ExpandConversationReferences(c, new() { ("turn", fourth) }, access, generation, 64, 64, deepAudit);
                    Check("growing_chain_recovers_four_prior_levels", new[] { third, chainPlayerId, firstPlayerId, "old_npc" }.All(id => deep.Any(s => s.Id == id)), deepAudit);
                    var shortAudit = TestDict();
                    var shortRead = ExpandConversationReferences(c, new() { ("turn", fourth) }, access, generation, 2, 64, shortAudit);
                    Check("short_depth_reports_partial_chain", shortRead.Any(s => s.Id == chainPlayerId) && !shortRead.Any(s => s.Id == "old_npc")
                        && ReadBool(shortAudit, "hopLimitReached", false) && ReadString(shortAudit, "traversalCoverage", "") == "partial", shortAudit);
                    var reverseAudit = TestDict();
                    var fromOriginal = ExpandConversationReferences(c, new() { ("turn", "old_npc") }, access, generation, 64, 64, reverseAudit);
                    Check("original_seed_finds_later_callbacks", fromOriginal.Any(s => s.Id == fourth) && ReadInt(reverseAudit, "reverseAdmitted", 0) > 0, reverseAudit);
                    var starRead = ExpandConversationReferences(c, new() { ("turn", star) }, access, generation, 2, 64, TestDict());
                    Check("shared_original_recovers_sibling_conversation", starRead.Any(s => s.Id == "old_npc") && starRead.Any(s => s.Id == sibling));
                    int beforeRead = Edges();
                    var diamondRead = ExpandConversationReferences(c, new() { ("turn", diamond) }, access, generation, 64, 64, TestDict());
                    Check("diamond_and_reverse_cycles_are_deduplicated_without_writes", diamondRead.Count(s => s.Id == "old_npc") == 1
                        && diamondRead.Select(s => s.Type + ":" + s.Id).Distinct().Count() == diamondRead.Count && Edges() == beforeRead);
                    using (var hidden = c.BeginTransaction())
                    {
                        ReignDatabase.ExecuteSql(c, "UPDATE conversation_turns SET participants_json='[\"other\",\"player\"]' WHERE turn_id=$id;", TestDict("id", third));
                        Check("hidden_intermediate_stops_forward_and_reverse_walk", ExpandConversationReferences(c, new() { ("turn", fourth) }, access, generation, 64, 64, TestDict()).Count == 0
                            && !ExpandConversationReferences(c, new() { ("turn", chainPlayerId) }, access, generation, 64, 64, TestDict()).Any(s => s.Id == fourth));
                        hidden.Rollback();
                    }
                    using (var stale = c.BeginTransaction())
                    {
                        ReignDatabase.ExecuteSql(c, "UPDATE conversation_turns SET text=text || ' Changed after the reference.' WHERE turn_id=$id;", TestDict("id", third));
                        Check("revised_intermediate_cannot_bridge_chain", ExpandConversationReferences(c, new() { ("turn", fourth) }, access, generation, 64, 64, TestDict()).Count == 0);
                        stale.Rollback();
                    }
                    using (var future = c.BeginTransaction())
                    {
                        ReignDatabase.ExecuteSql(c, "UPDATE conversation_reference_edges SET world_day=40 WHERE from_id=$id;", TestDict("id", fourth));
                        Check("future_edge_cannot_bridge_chain", ExpandConversationReferences(c, new() { ("turn", fourth) }, access, generation, 64, 64, TestDict()).Count == 0);
                        future.Rollback();
                    }
                    Check("other_generation_has_no_forward_or_reverse_path", ExpandConversationReferences(c, new() { ("turn", "old_npc") }, access, "other_generation", 64, 64, TestDict()).Count == 0);
                    using (var proposedWithdrawal = c.BeginTransaction())
                    {
                        string originalHash = MemoryTranscriptSourceHash(ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id='old_npc';").Single());
                        ReignDatabase.ExecuteSql(c, "UPDATE conversation_reference_edges SET relation='withdraws' WHERE from_id=$id;", TestDict("id", sibling));
                        var withdrawalAudit = TestDict();
                        var related = ExpandConversationReferences(c, new() { ("turn", "old_npc") }, access, generation, 1, 64, withdrawalAudit);
                        Check("incoming_withdrawal_hint_does_not_change_original_status", related.Any(s => s.Id == sibling)
                            && ReadInt(withdrawalAudit, "reverseAdmitted", 0) > 0
                            && ReadString(ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id='old_npc';").Single(), "status", "") == "active"
                            && MemoryTranscriptSourceHash(ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id='old_npc';").Single()) == originalHash);
                        proposedWithdrawal.Rollback();
                    }
                    using (var foreignAssociation = c.BeginTransaction())
                    {
                        ReignDatabase.ExecuteSql(c, "UPDATE conversation_reference_edges SET owner_id='other' WHERE from_id=$id;", TestDict("id", fourth));
                        Check("shared_endpoints_do_not_share_private_observer_association", ExpandConversationReferences(c, new() { ("turn", fourth) }, access, generation, 64, 64, TestDict()).Count == 0
                            && !ExpandConversationReferences(c, new() { ("turn", third) }, access, generation, 64, 64, TestDict()).Any(s => s.Id == fourth));
                        foreignAssociation.Rollback();
                    }
                    using (var bounds = c.BeginTransaction())
                    {
                        const string quote = "I remember the red flag condition";
                        var dense = new List<ConversationReferenceSource>();
                        for (int i = 0; i <= 40; i++)
                        {
                            string id = "bounded_chain_" + i;
                            InsertConversationTurn(c, id, "old", id, 100 + i, id, "npc", "npc", "Mira", quote + ".", "in_person", 10, 10, "town",
                                new() { "npc", "player" }, TestDict("timelineId", "main", "worldDay", 10d));
                            dense.Add(ResolveConversationReferenceSource(c, "turn", id, access));
                        }
                        for (int i = 1; i < dense.Count; i++)
                            ReignDatabase.ExecuteSql(c, @"INSERT INTO conversation_reference_edges(edge_id,owner_id,timeline_id,restore_generation,from_type,from_id,from_hash,to_type,to_id,to_hash,
relation,source_event_id,as_of_sequence,world_day,created_ts,payload_json)
SELECT $prefix || n,'npc','main',$generation,'turn',$from,$fromHash,'turn',$to,$toHash,'refers_to',$from,$sequence,10,10,$payload FROM generate_series(1,32) n;",
                                TestDict("prefix", "bounded_edge_" + i + "_", "generation", generation, "from", dense[i].Id, "fromHash", dense[i].Hash,
                                    "to", dense[i - 1].Id, "toHash", dense[i - 1].Hash, "sequence", dense[i].Sequence,
                                    "payload", Json.Serialize(TestDict("schema", ConversationReferenceGraphVersion, "originQuote", quote, "targetQuote", quote))));
                        var boundAudit = TestDict();
                        var boundedChain = ExpandConversationReferences(c, new() { ("turn", dense[0].Id) }, access, generation, 64, 64, boundAudit);
                        Check("duplicate_heavy_long_chain_stops_at_global_work_cap", ReadBool(boundAudit, "scanCapReached", false)
                            && ReadInt(boundAudit, "scannedEdgeRows", int.MaxValue) <= ConversationReferenceMaximumScannedEdges
                            && ReadInt(boundAudit, "edgeQueries", int.MaxValue) <= 2 * (6 + ConversationReferenceMaximumNodes)
                            && ReadInt(boundAudit, "sourceLookups", int.MaxValue) <= 6 + 2 * ReadInt(boundAudit, "scannedEdgeRows", 0)
                            && boundedChain.Count < 40 && ReadString(boundAudit, "traversalCoverage", "") == "partial", boundAudit);
                        bounds.Rollback();
                    }
                }
                var readPayload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 21d, "playerHeroStringId", "player",
                    "identityView", TestDict("knowsIdentity", true), "playerText", "And that condition?", "heroStringId", "npc");
                ConversationReferenceValidationMode.Value = "enabled";
                string graph = BuildConversationReferenceContext(campaign, "npc", "And that condition?", readPayload, sources, "RAW_BASELINE");
                Check("next_read_expands_complete_original_exchange", graph.Contains(original) && graph.Contains("What are your terms for the north gate?"), readPayload);
                Check("chain_context_keeps_distinct_whole_mentions_in_source_order", graph.Contains("The red flag condition came up during our third mention.")
                    && graph.Contains("The red flag condition came up during our fourth mention.")
                    && graph.Contains("The red flag condition came up during our sibling callback.")
                    && graph.IndexOf(original, StringComparison.Ordinal) < graph.IndexOf("The red flag condition came up during our third mention.", StringComparison.Ordinal));
                string signature = MemoryRecallTurnSignature(readPayload, "npc");
                CaptureRequiredPromptEvidence(readPayload, "selectedContextAndMemory", graph);
                var deliveredGraph = BindDeliveredMemoryRecallEvidence(readPayload, "npc", new() { TestDict("role", "user", "content", graph) }, "Mira");
                Check("graph_originals_reuse_existing_recall_guard_without_new_offers", ReadInt(deliveredGraph, "sourceCount", 0) > 0
                    && ReadInt(ReadDictionary(deliveredGraph, "referenceGraph"), "sourceCount", -1) == 0
                    && MemoryRecallTurnSignature(readPayload, "npc") == signature);
                const string budgetModel = "z-ai/glm-5.3-flash";
                var budgetSettings = TestDict("llmProvider", Program.NanoGptProvider, "dialogueModel", budgetModel, "eventsModel", budgetModel,
                    "conversationInputTokenLimit", 120000);
                var budgetCatalog = new ConversationCapacityCatalog(() => Json.Serialize(TestDict("data", new[] {
                    TestDict("id", budgetModel, "context_length", 1048576, "max_output_tokens", 131072) })), () => DateTimeOffset.UtcNow);
                var exactRecent = Enumerable.Range(0, 30).Select(i => RenderContinuityRecord("chain_recent_" + i,
                    "accepted_exchange", "npc", "current", 20, 300, true, "Exact recent turn " + i + ": " + new string('h', 1300))).ToList();
                string optionalOverflow = RenderContinuityRecord("oversized_graph_context", "reference_graph_exchange", "npc", "old", 10, 10, false, new string('g', 240000));
                foreach (string route in new[] { "dialogue", "event" })
                {
                    string historyKey = route == "dialogue" ? "priorDialogueText" : "eventHistoryText";
                    var values = new Dictionary<string, string> { ["heroName"] = "Mira", ["playerName"] = "Player",
                        ["playerText"] = "And that condition?", ["contextPullText"] = graph + "\n\n" + optionalOverflow,
                        [historyKey] = string.Join("\n\n", exactRecent) };
                    var capacity = ResolveConversationPromptCapacity(TestDict(), route == "dialogue" ? "dialogue" : "social_event", budgetSettings, budgetCatalog);
                    var selection = BuildBudgetedConversationLiveTurn(route + "_live_turn_template.txt", historyKey, values,
                        "CURRENT SCENE", "PROTECTED FOUNDATION " + new string('b', 300000), "RELATIONSHIP", "RULES", "CHARACTER", capacity: capacity);
                    var envelope = CreatePromptEnvelope(route, "chain_budget", "RULES", "CHARACTER", selection.LiveTurn);
                    FinalizeContinuityPromptBudget(envelope, TestDict("continuityEvidence", TestDict("sourceIds",
                        Enumerable.Range(0, 30).Select(i => "chain_recent_" + i).ToList())), capacity);
                    var body = TestDict("model", budgetModel, "messages", envelope.Messages, "max_tokens", 16000, "response_format", TestDict("type", "json_object"));
                    var final = BuildContinuityProviderPreflight(budgetSettings, TestDict("promptEnvelope", envelope.Diagnostics), body,
                        PortraitsService.NanoGptChatCompletionsUrl, budgetModel, budgetCatalog);
                    Check("chain_budget_preserves_thirty_whole_records_" + route, exactRecent.All(selection.LiveTurn.Contains)
                        && !selection.LiveTurn.Contains(new string('g', 40)) && ReadBool(final, "fits", false)
                        && ReadInt(final, "inputTokenEstimate", int.MaxValue) <= 120000, final);
                    int headroom = Math.Max(0, 120000 - ReadInt(final, "inputTokenEstimate", 0));
                    body["tools"] = new[] { TestDict("type", "function", "function", TestDict("name", "chain_fixture",
                        "description", new string('界', headroom + 128), "parameters", TestDict("type", "object", "properties", TestDict()))) };
                    var late = BuildContinuityProviderPreflight(budgetSettings, TestDict("promptEnvelope", envelope.Diagnostics), body,
                        PortraitsService.NanoGptChatCompletionsUrl, budgetModel, budgetCatalog);
                    Check("chain_budget_rechecks_late_multibyte_wrapper_" + route, ReadBool(final, "fits", false) && !ReadBool(late, "fits", true)
                        && ReadInt(late, "inputTokenEstimate", 0) > ReadInt(final, "inputTokenEstimate", 0), late);
                }
                string tiny = BuildConversationReferenceContext(campaign, "npc", "And that condition?", readPayload, sources, "RAW_BASELINE",
                    TestDict("memoryReferenceGraphMaxCharacters", 0));
                Check("budget_rejects_whole_graph_unit_without_displacing_raw_context", tiny.Length == 0
                    && ReadInt(ConversationReferenceDiagnostic(readPayload, "npc"), "budgetRejectedUnits", 0) > 0);
                ConversationReferenceValidationMode.Value = "off";
                Check("off_mode_has_no_graph_text_or_writer_instructions", BuildConversationReferenceContext(campaign, "npc", "And that condition?", readPayload, sources, "RAW_BASELINE") == ""
                    && ConversationReferenceInstructions() == "");
                Check("off_mode_prefix_is_byte_identical_with_persisted_edges", disabledPrefix == BuildDialogueGlobalPrefix(false, false, new List<Dictionary<string, object>>()));
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string id = ReadString(RelationshipsService.InsertLetter(c, "npc", "Mira", "player", "Player", "I moved the plant into a pot beside the stone wall.",
                        "npc_initiated", "plant", "", "a", "b", 10d, 15d, TestDict()), "letterId", "");
                    linkedLetterId = id;
                    var access = new Program.KnowledgeAccessContext { NpcId = "npc", PlayerId = "player", TimelineId = "main", WorldDay = 20 };
                    Check("letter_author_knows_own_sent_source", ResolveConversationReferenceSource(c, "letter", id, access) != null);
                    access.NpcId = "player";
                    Check("unread_letter_is_not_granted_to_recipient", ResolveConversationReferenceSource(c, "letter", id, access) == null);
                    access.NpcId = "other";
                    Check("letter_reference_does_not_grant_bystander_access", ResolveConversationReferenceSource(c, "letter", id, access) == null);
                    ConversationReferenceValidationMode.Value = "capture";
                    var letterUnit = Unit("own_letter_reference");
                    letterOriginEvent = letterUnit.EventId;
                    letterUnit.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1;
                    letterUnit.PlayerText = "What did you write about moving the plant?";
                    letterUnit.Payload["playerText"] = letterUnit.PlayerText;
                    letterUnit.Reply = "I wrote that I moved the plant into a pot beside the stone wall.";
                    letterUnit.Parsed["reply"] = letterUnit.Reply;
                    foreach (var proposal in ReadDictionaryList(letterUnit.Parsed, "memoryReferences"))
                    {
                        proposal["targetType"] = ReadString(proposal, "origin", "") == "player_input" ? "turn" : "letter"; proposal["targetId"] = id;
                        proposal["originQuote"] = ReadString(proposal, "origin", "") == "player_input" ? "moving the plant" : "moved the plant into a pot";
                        proposal["targetQuote"] = "I moved the plant into a pot beside the stone wall.";
                    }
                    var letterRow = ReignDatabase.QuerySql(c, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", id)).Single();
                    AcceptedConversationUnit NormalizedLetterUnit(string name)
                    {
                        var unit = Unit(name);
                        unit.PlayerText = letterUnit.PlayerText; unit.Payload["playerText"] = unit.PlayerText;
                        unit.Reply = letterUnit.Reply; unit.Parsed["reply"] = unit.Reply;
                        foreach (var proposal in ReadDictionaryList(unit.Parsed, "memoryReferences"))
                        {
                            proposal["targetType"] = "turn"; proposal["targetId"] = id;
                            proposal["originQuote"] = ReadString(proposal, "origin", "") == "player_input" ? "moving the plant" : "moved the plant into a pot";
                            proposal["targetQuote"] = "I moved the plant into a pot beside the stone wall.";
                        }
                        return unit;
                    }
                    string letterPacket = RenderMemoryEvidence(CorrespondenceMemoryEvidence(letterRow, "npc", 10), "m7");
                    var wrongLetterQuote = NormalizedLetterUnit("normalized_letter_wrong_quote"); Bind(wrongLetterQuote, letterPacket);
                    foreach (var proposal in ReadDictionaryList(wrongLetterQuote.Parsed, "memoryReferences")) proposal["targetQuote"] = original;
                    var wrongLetterResult = ReadDictionary(Publish(wrongLetterQuote), "references");
                    Check("normalized_type_cannot_borrow_quote_from_another_record", ReadInt(wrongLetterResult, "stored", -1) == 0
                        && ReadDictionaryList(wrongLetterResult, "rejected").All(r => ReadString(r, "reason", "") == "reference_quote_outside_delivered_view"));
                    var unofferedLetter = NormalizedLetterUnit("normalized_letter_unoffered"); Bind(unofferedLetter, Packet());
                    Check("normalized_type_never_discovers_unoffered_letter", ReadInt(ReadDictionary(Publish(unofferedLetter), "references"), "stored", -1) == 0);
                    var revisedLetter = NormalizedLetterUnit("normalized_letter_changed_revision"); Bind(revisedLetter, letterPacket);
                    ReignDatabase.ExecuteSql(c, "UPDATE letters SET body=$body WHERE letter_id=$id;", TestDict("id", id, "body", ReadString(letterRow, "body", "") + " Changed later."));
                    Check("normalized_type_preserves_current_revision_gate", ReadInt(ReadDictionary(Publish(revisedLetter), "references"), "stored", -1) == 0);
                    ReignDatabase.ExecuteSql(c, "UPDATE letters SET body=$body WHERE letter_id=$id;", TestDict("id", id, "body", ReadString(letterRow, "body", "")));
                    Bind(letterUnit, RenderMemoryEvidence(CorrespondenceMemoryEvidence(letterRow, "npc", 10), "m7"));
                    var letterReferences = ReadDictionary(Publish(letterUnit), "references");
                    Check("accepted_pair_can_link_canonical_authored_letter", ReadInt(letterReferences, "stored", 0) == 2);
                    Check("accepted_letter_type_normalization_is_reported_and_canonical", ReadInt(letterReferences, "normalizedTargetTypeProposals", 0) == 1
                        && ReignDatabase.QuerySql(c, "SELECT edge_id FROM conversation_reference_edges WHERE source_event_id=$event AND to_type<>'letter';", TestDict("event", letterUnit.EventId)).Count == 0
                        && ReignDatabase.QuerySql(c, "SELECT edge_id FROM conversation_reference_edges WHERE source_event_id=$event AND payload_json LIKE '%targetTypeDerivedFromUniqueOffer%';", TestDict("event", letterUnit.EventId)).Count == 2);
                    var letterSources = ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active';", TestDict("id", letterUnit.EventId));
                    ConversationReferenceValidationMode.Value = "enabled";
                    string letterGraph = BuildConversationReferenceContext(campaign, "npc", "And the plant?", readPayload, letterSources, "RAW_BASELINE");
                    Check("letter_expansion_preserves_authorship_without_granting_read_receipt", letterGraph.Contains("I moved the plant into a pot beside the stone wall.")
                        && letterGraph.Contains("Recipient receipt/read state is unknown")
                        && ReadString(ReignDatabase.QuerySql(c, "SELECT status FROM letters WHERE letter_id=$id;", TestDict("id", id)).Single(), "status", "") != "read");
                }
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var point = TestDict("savePointId", "reference_restore", "timelineId", "main", "campaignTimeDays", 21d,
                        "worldHistorySequence", 100L, "worldHistoryHeadEventId", "reference_head");
                    const string restored = "reference_restored_generation";
                    CaptureRestoredMemorySources(c, point, restored);
                    ReignDatabase.ExecuteSql(c, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current';", TestDict("generation", restored));
                    CaptureRestoredLetterSources(c, point);
                    RebindRestoredConversationReferences(c, point, restored);
                    int CountRestored() => ReadInt(ReignDatabase.QuerySql(c, "SELECT COUNT(*) AS n FROM conversation_reference_edges WHERE restore_generation=$generation;",
                        TestDict("generation", restored)).Single(), "n", 0);
                    int count = CountRestored();
                    Check("restore_preserves_exact_letter_target_membership", ReignDatabase.QuerySql(c,
                        "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_type='letter' AND to_id=$id;",
                        TestDict("generation", restored, "id", linkedLetterId)).Count == 2);
                    Check("restore_rebinds_only_exact_eligible_sources", count >= 4 && ReignDatabase.QuerySql(c,
                        "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_id IN ('secret_npc','foreign_npc','future_npc');",
                        TestDict("generation", restored)).Count == 0);
                    RebindRestoredConversationReferences(c, point, restored);
                    Check("restore_rebinding_is_idempotent", CountRestored() == count);
                    const string again = "reference_restored_again";
                    CaptureRestoredMemorySources(c, point, again);
                    ReignDatabase.ExecuteSql(c, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current'; DELETE FROM memory_restored_sources WHERE source_id='old_npc';",
                        TestDict("generation", again));
                    CaptureRestoredLetterSources(c, point);
                    RebindRestoredConversationReferences(c, point, again);
                    Check("missing_snapshot_member_is_not_recovered_by_graph", ReignDatabase.QuerySql(c,
                        "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_id='old_npc';",
                        TestDict("generation", again)).Count == 0 && ReignDatabase.QuerySql(c,
                        "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_id=$id;",
                        TestDict("generation", again, "id", firstPlayerId)).Count > 0);
                    Check("restores_do_not_accumulate_duplicate_generations", ReignDatabase.QuerySql(c,
                        "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation<>$generation;", TestDict("generation", again)).Count == 0);
                }
                int snapshotEdges = Edges();
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, snapshotName);
                snapshotCreated = true;
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                    ReignDatabase.ExecuteSql(c, "DELETE FROM conversation_reference_edges; UPDATE conversation_turns SET text='Abandoned post-snapshot replacement.' WHERE turn_id=$id;",
                        TestDict("id", firstPlayerId));
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, snapshotName);
                AdvanceMemoryRestoreGeneration(campaign, TestDict("savePointId", snapshotName, "timelineId", "main", "campaignTimeDays", 21d,
                    "worldHistorySequence", 100L, "worldHistoryHeadEventId", "reference_head"), ReignPostgreSqlStorage.SnapshotSchemaName(campaign, snapshotName));
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string restoredGeneration = ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
                    Check("database_snapshot_preserves_reference_table_and_original_ids", snapshotEdges > 0 && Edges() == snapshotEdges
                        && ReadString(ReignDatabase.QuerySql(c, "SELECT text FROM conversation_turns WHERE turn_id=$id;", TestDict("id", firstPlayerId)).Single(), "text", "") == first.PlayerText
                        && ReignDatabase.QuerySql(c, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation<>$generation;", TestDict("generation", restoredGeneration)).Count == 0);
                    var chainRows = ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active';", TestDict("id", chain.EventId));
                    ConversationReferenceValidationMode.Value = "enabled";
                    string next = BuildConversationReferenceContext(campaign, "npc", "And that condition?", readPayload, chainRows, "RAW_BASELINE");
                    Check("post_snapshot_read_follows_rebound_original_sources", next.Contains(first.PlayerText) && next.Contains(first.Reply)
                        && !next.Contains("Abandoned post-snapshot replacement."));
                    var letterSources = ReignDatabase.QuerySql(c, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active';", TestDict("id", letterOriginEvent));
                    string letterNext = BuildConversationReferenceContext(campaign, "npc", "And the plant?", readPayload, letterSources, "RAW_BASELINE");
                    Check("post_snapshot_read_preserves_letter_links_without_marking_recipient_read", letterNext.Contains("I moved the plant into a pot beside the stone wall.")
                        && ReignDatabase.QuerySql(c, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_type='letter' AND to_id=$id;",
                            TestDict("generation", restoredGeneration, "id", linkedLetterId)).Count == 2
                        && ReadString(ReignDatabase.QuerySql(c, "SELECT status FROM letters WHERE letter_id=$id;", TestDict("id", linkedLetterId)).Single(), "status", "") != "read");
                }
                const string secret = "PRIVATE-GRAPH-QUOTE-948";
                var privateOutput = TestDict("reply", "I remember.", "parsed", TestDict("reply", "I remember.", "memoryReferences", new[] { TestDict("targetQuote", secret) }),
                    "llm", TestDict("content", Json.Serialize(TestDict("reply", "I remember.", "memoryReferences", new[] { TestDict("targetQuote", secret) }))));
                string safe = Json.Serialize(WithoutConversationReferenceMetadata(privateOutput));
                Check("public_and_audit_projection_removes_nested_private_metadata", !safe.Contains(secret) && !safe.Contains("memoryReferences") && safe.Contains("I remember."));
                Check("malformed_raw_private_metadata_is_withheld", !Json.Serialize(WithoutConversationReferenceMetadata(
                    TestDict("rawContent", "{\"reply\":\"ok\",\"memoryReferences\":[{\"targetQuote\":\"" + secret))).Contains(secret));
                Check("literal_authored_metadata_word_is_preserved", ReadString((Dictionary<string, object>)WithoutConversationReferenceMetadata(
                    TestDict("reply", "The page says \"memoryReferences\".")), "reply", "") == "The page says \"memoryReferences\".");
                string promptText = "OUTPUT SCHEMA\n{\"reply\":\"\",\"memoryReferences\":[]}\nUse exact source references.";
                var promptAudit = TestDict("messages", new[] { TestDict("role", "system", "content", promptText) });
                Check("ordinary_prompt_audit_keeps_output_schema_text", ReadString(ReadDictionaryList(
                    (Dictionary<string, object>)WithoutConversationReferenceMetadata(promptAudit), "messages").Single(), "content", "") == promptText);
                var page = TestDict("content", "{\"memoryReferences\":[{\"targetQuote\":\"" + secret + "\"}]}", "sha256", "native_exact_page_hash");
                Check("operator_evidence_transport_preserves_exact_pages", ReferenceEquals(page, ConversationReferenceTransportPayload(page, "GET", "/audit/prompt"))
                    && ReferenceEquals(page, ConversationReferenceTransportPayload(page, "GET", "/api/diagnostics/conversations/payload")));
                Check("evidence_transport_exception_does_not_apply_to_normal_responses", !ReferenceEquals(privateOutput,
                    ConversationReferenceTransportPayload(privateOutput, "POST", "/api/diagnostics/conversations/payload"))
                    && !Json.Serialize(ConversationReferenceTransportPayload(privateOutput, "GET", "/api/jobs/status")).Contains(secret));
            }
            finally
            {
                ConversationReferenceValidationMode.Value = oldMode;
                if (snapshotCreated) ReignPostgreSqlStorage.DropSnapshot(campaign, snapshotName);
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            return results;
        }
    }
}
