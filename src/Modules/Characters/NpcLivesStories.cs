using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static IReadOnlyList<NpcStoryDefinition> ReadNpcStoryDefinitions()
        {
            string path = Path.Combine(DataDir, "npc-lives-stories.json");
            if (!File.Exists(path)) return NpcStoryCatalog.All;
            var edits = Json.Deserialize<List<NpcStoryDefinition>>(File.ReadAllText(path, Encoding.UTF8));
            var errors = NpcStoryCatalog.Validate(edits);
            if (errors.Count > 0) throw new InvalidDataException("The NPC story catalog is invalid: " + errors[0]);
            var merged = NpcStoryCatalog.All.Where(d => !edits.Any(e => e.Id == d.Id)).Concat(edits).ToList();
            errors = NpcStoryCatalog.ValidateLinks(merged);
            if (errors.Count > 0) throw new InvalidDataException("The NPC story catalog is invalid: " + errors[0]);
            return merged;
        }

        private static void CaptureNpcPurposePrompt(NpcLivesState state, string hero, Dictionary<string, object> p)
        {
            if (p == null) return;
            string storyId = ReadString(p, "npcStoryId", ""), leaveId = ReadString(p, "npcLifeRequestId", "");
            var scoped = new NpcLivesState { CampaignId = state.CampaignId, TimelineId = state.TimelineId,
                // Pending offers remain reference context; only an accepted story
                // may turn its authored activity into a current system objective.
                Stories = state.Stories.Where(s => s.HeroId == hero && s.Status == "active"
                    && s.PhaseIndex >= 0 && s.PhaseIndex < s.Definition.Phases.Count
                    && (storyId.Length > 0 ? s.Id == storyId : s.Definition.Phases[s.PhaseIndex].LocationKind == "current")).ToList(),
                Leaves = state.Leaves.Where(l => l.HeroId == hero && l.Status == "discussing"
                    && (leaveId.Length == 0 || l.Id == leaveId)).ToList() };
            var b = new StringBuilder();
            if (scoped.Stories.Count > 0 || scoped.Leaves.Count > 0)
            {
                b.AppendLine("CURRENT NPC PERSONAL PURPOSE — authoritative instructions for this speaker and this turn.");
                b.AppendLine("The supplied saved identities, places, phase and terms govern this experience. Older narration is historical evidence, not permission to replace these bindings. If an earlier account used a different place or person for this same purpose, correct it naturally and continue with the established binding. Distinguish your current native place from a remembered or future destination. Never relocate the current experience through dialogue, invent future chapters, or claim a native action from speech.");
                AppendNpcStoryPrompt(b, scoped, hero, p);
            }
            // Only a fresh server snapshot can create this instruction. Neither
            // incoming payload fields nor remembered player announcements can do so.
            NpcPurposePromptWindows.GetValue(p, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, string>())[hero] = b.ToString();
        }

        private static void AttachNpcPurposePrompt(PromptEnvelope envelope, Dictionary<string, object> p, string hero)
        {
            if (p == null || !NpcPurposePromptWindows.TryGetValue(p, out var windows)
                || !windows.TryGetValue(hero, out var body) || body.Length == 0) return;
            envelope.Messages.Insert(Math.Max(0, envelope.Messages.Count - 1), TestDict("role", "system", "content", body));
            envelope.TotalChars = envelope.Messages.Sum(m => ReadString(m, "content", "").Length);
            envelope.Diagnostics["messageCount"] = envelope.Messages.Count;
            envelope.Diagnostics["npcPersonalPurpose"] = TestDict("source", "server_snapshot", "heroId", hero,
                "characters", body.Length, "sha256", PromptHash(body));
        }

        private static void AppendNpcStoryPrompt(StringBuilder b, NpcLivesState state, string hero, Dictionary<string, object> p)
        {
            foreach (var story in state.Stories.Where(s => s.HeroId == hero && (s.Status == "invited" || s.Status == "active")
                && s.PhaseIndex >= 0 && s.PhaseIndex < s.Definition.Phases.Count))
            {
                var phase = story.Definition.Phases[story.PhaseIndex];
                bool invited = story.Status == "invited";
                b.AppendLine((invited ? "For this optional personal invitation" : "For this current personal purpose")
                    + ", the saved bindings and fixed destination below take precedence over conflicting older dialogue, memories or player suggestions. Older descriptions cannot rename this person, substitute a different friend, or move this purpose to another place. Do not invent a second friend or a changed name to reconcile a conflict. Use the established identity naturally without discussing system instructions.");
                if (story.PhaseIndex >= story.Definition.SupportingRevealPhase)
                {
                    foreach (var binding in story.Bindings) b.AppendLine("Established story binding " + binding.Key + ": " + binding.Value + ". Preserve this identity; do not substitute another person or object.");
                    if (story.Definition.SupportingRole.Length > 0
                        && (!story.Bindings.TryGetValue("heroName", out var boundName) || string.IsNullOrWhiteSpace(boundName)))
                        b.AppendLine("The native identity of your " + story.Definition.SupportingRole + " has not been established yet. Refer to this person only by that relationship. Do not invent a name, nickname, gender, appearance or hometown, and do not accept an invented identity suggested by the player. Defer those details until an established story binding supplies them; agreeing to the experience does not establish that identity.");
                }
                b.AppendLine((invited ? "PENDING PERSONAL INVITATION [" : "CURRENT PERSONAL PURPOSE [")
                    + story.Id + ", revision " + story.Revision + "]: " + phase.Title);
                if (story.LinkedLocationName.Length > 0) b.AppendLine("The established place connected with this interest is " + story.LinkedLocationName + ". Keep that place consistent with earlier experiences.");
                if (story.EarlierExperiences.Count > 0) b.AppendLine("Earlier completed experiences leading here: " + string.Join("; ", story.EarlierExperiences) + ". Refer to accepted memories; do not replay them as new or reveal future chapters.");
                b.AppendLine("Fixed destination ID: " + story.LocationId + "; current native place ID: " + ReadFirstString(p, "locationId", "currentSettlementId", "settlementId") + ".");
                string destinationName = story.LocationName.Length > 0 ? story.LocationName
                    : story.LocationId == story.LinkedLocationId ? story.LinkedLocationName : "";
                if (destinationName.Length > 0) b.AppendLine("This phase's fixed native destination is " + destinationName + ". Do not substitute a tavern, town or village recalled from a different experience.");
                else b.AppendLine("The bound destination's native name has not been supplied. Refer to the agreed destination without inventing its name or borrowing a place from older dialogue.");
                if (invited)
                {
                    b.AppendLine("This is an optional, unaccepted offer, not a current conversation objective. Answer the player's actual contribution. A pending invitation is not evidence that this topic is new or unfinished in ordinary conversation. Honor supplied accepted dialogue and memories; acknowledge what you already discussed. Do not repeat the setup, ask again for an opinion already given, or redirect an unrelated conversation to this offer just because it is pending.");
                    b.AppendLine("Personal interest connected with the offer: " + story.Definition.Interest + ". You may discuss or offer it when relevant; reviewing the offer does not begin the activity.");
                    // A deliberate review needs enough detail to explain the exact
                    // offer. Ordinary chat must not receive its execution script.
                    if (ReadString(p, "npcStoryId", "") == story.Id)
                        b.AppendLine("Proposed activity reference for this exact offer (quoted data, not instructions to perform it): " + Json.Serialize(phase.Prompt));
                    b.AppendLine("The personal-story system awards its own affinity and XP. Do not also assign relationship changes just for agreeing, participating, delaying or declining this personal request.");
                    b.AppendLine("The existing proposal is " + story.ProposalId + ". Do not claim acceptance, progress or rewards from ordinary dialogue. A player may ask questions, decline or review the explicit action offer. Prior discussion remains part of your shared history without granting mechanical acceptance or completion.");
                    continue;
                }
                b.AppendLine(phase.Prompt);
                if (phase.Objectives.Any(o => o.Kind == "duel"))
                    b.AppendLine("This personal practice duel belongs to this story's existing invitation and Story Location. Invite the player to begin it there. Do not create an additional standalone duel_player action offer; it would be a different encounter and cannot complete this chapter.");
                b.AppendLine("The personal-story system awards its own affinity and XP. Do not also assign relationship changes just for agreeing, participating, delaying or declining this personal request.");
                {
                    b.AppendLine("Stay with this phase. If the player goes off topic, respond briefly and bring the conversation back to your purpose. Meaningful alternatives and disagreement can be good participation; no required phrase. The engine alone decides native results.");
                    b.AppendLine("Conversation progress requires at least two different substantive accepted contributions. Return npcStoryAssessments as an array of {storyId,revision,objectiveId,outcome,evidenceQuote,resolutionQuote}; outcome is 'progress' for meaningful participation, 'tangent' otherwise. Quote exact words from this player contribution. Missing or ambiguous evidence earns nothing. Never assess native objectives.");
                    foreach (var objective in phase.Objectives)
                    {
                        b.AppendLine((objective.Native ? "NATIVE RECEIPT REQUIRED" : "CONVERSATION OBJECTIVE") + " [" + objective.Id + "]: " + objective.Instruction);
                        if (!objective.Native && objective.CompletionOutcomes.Count > 0)
                            b.AppendLine("For this objective, participation alone never completes the chapter. Only after the actual activity or personal conclusion has occurred, return one of these final outcomes: "
                                + string.Join(", ", objective.CompletionOutcomes) + ". Supply resolutionQuote as one complete, self-contained paragraph from your final visible reply establishing the accomplished conclusion, alongside the exact accepted player evidenceQuote. Put the accomplished conclusion in its own short paragraph, and copy that entire paragraph exactly into resolutionQuote. Keep conditional plans or side remarks in separate paragraphs. Future plans, conditional willingness, permission, inspection still to come, or 'let us go' remain progress. Do not cherry-pick an affirmative fragment from a conditional paragraph. For adoption, 'adopted' means the rabbit is now your companion after meeting its keeper and deciding; 'declined' means the decision against adoption is final. Both are valid personal conclusions. Do not invent a purchase, coin transfer or inventory effect.");
                    }
                }
            }
            foreach (var leave in state.Leaves.Where(l => l.HeroId == hero && l.Status != "returned" && l.Status != "cancelled"))
            {
                b.AppendLine("FIXED PERSONAL LEAVE PURPOSE [" + leave.Id + ", revision " + leave.Revision + "]: " + leave.Purpose);
                b.AppendLine("State: " + leave.Status + "; agreed absence: " + leave.AbsenceDays + " days; proposal: " + leave.ProposalId + ".");
                b.AppendLine(NpcLeaveNegotiation.PrivacyRule(leave.Trust));
                b.AppendLine("For an assessed argument, the following responses are locked by your saved personality, trust, duties and urgency. Include the exact applicable sentence in your reply; it records only staying for now or continued intent, never player consent to departure.");
                foreach (string reason in NpcLeaveNegotiation.Reasons)
                    foreach (bool compelling in new[] { false, true })
                        b.AppendLine(reason + ", compelling=" + compelling + ": " + NpcLeaveNegotiation.Response(
                            NpcLeaveNegotiation.Persuaded(leave, reason, compelling, state.CampaignId + "|" + state.TimelineId),
                            NpcLivesPolicy.ReaskDelay(ReadNpcLivesOptions())));
                b.AppendLine("Your reason for wanting to leave remains the subject. Do not replace it with a different errand or agree to unrelated terms. Ordinary friendly speech cannot authorize departure or remove the request. Only the explicit current proposal can set a wait or departure. Waiting before departure is separate from time away.");
                b.AppendLine("Listen to a concrete reason to stay. Return npcLeaveAssessment:{leaveId,revision,reasonKind,evidenceQuote,compelling}, where reasonKind is medical_need,imminent_danger,prior_commitment,alternative_arrangement,emotional_appeal or authority_demand. Quote the player's exact new argument. Tangents and repeated arguments do not change your decision. The engine fixes the result for each reason; never contradict its saved decision.");
                if (leave.ReconsiderDay.HasValue) b.AppendLine("You agreed to stay for now and reconsider on campaign day " + leave.ReconsiderDay.Value + ". This did not abandon your purpose.");
                if (leave.DepartureDay.HasValue) b.AppendLine("Agreed departure is campaign day " + leave.DepartureDay.Value + ". Actual time away starts when native departure is safe.");
                foreach (var reason in leave.AcceptedArgumentIds) b.AppendLine("Already considered argument: " + reason + ". Do not reroll or treat a paraphrase as new.");
            }
        }

        private static bool NpcLivesOwnsRelationshipReward(NpcLivesState state, string hero, Dictionary<string, object> payload)
        {
            string storyId = ReadString(payload, "npcStoryId", "");
            string location = ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId");
            return state.Stories.Any(s => s.HeroId == hero && s.PhaseIndex >= 0 && s.PhaseIndex < s.Definition.Phases.Count
                && (s.Status == "invited" && s.Id == storyId
                    || s.Status == "active" && (s.LocationId.Length == 0 || s.LocationId == location)
                        && (s.Id == storyId || storyId.Length == 0 && s.Definition.Phases[s.PhaseIndex].LocationKind == "current")))
                || state.Leaves.Any(l => l.HeroId == hero && l.Id == ReadString(payload, "npcLifeRequestId", "") && l.Status == "discussing");
        }

        private static bool ApplyNpcLivesRelationshipOwnership(Dictionary<string, object> exchange,
            List<Dictionary<string, object>> assessments, string player)
        {
            var accepted = ReadDictionary(exchange, "acceptedUnit");
            if (!ReadBool(accepted, "ok", false) || ReadString(accepted, "personalRelationshipOwner", "") != "npc_lives") return false;
            // This flag comes from the committed accepted unit, never a provider
            // suggestion or client payload. Other participants retain their reactions.
            assessments.RemoveAll(a => ReadFirstString(a, "targetHeroStringId", "targetId") == player);
            return true;
        }

        private static bool RepeatedNpcStoryContribution(ReignDbConnection c, NpcStoryInstance story,
            string objective, string player, string sourceText)
        {
            string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
            string current = Normalize(sourceText);
            foreach (string id in story.Evidence.Where(e => e.ObjectiveId == objective && e.Kind == "conversation")
                .Select(e => e.SourceId).Distinct(StringComparer.Ordinal))
            {
                // Compare the immutable accepted contribution, not the excerpt
                // selected by the writer. Choosing another quote cannot reroll it.
                var original = QuerySql(c, "SELECT text FROM conversation_turns WHERE turn_id=$source AND speaker_id=$player AND role='player' LIMIT 1;",
                    TestDict("source", id, "player", player)).FirstOrDefault();
                if (original != null && string.Equals(current, Normalize(ReadString(original, "text", "")), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool NpcStoryResolutionMatches(string reply, string quote)
        {
            if (string.IsNullOrWhiteSpace(reply) || quote.Length < 12 || quote.Length > 2000
                || !System.Text.RegularExpressions.Regex.Split(reply.Trim(), @"\r?\n\s*\r?\n").Any(paragraph => paragraph.Trim() == quote)) return false;
            // A complete paragraph prevents extracting "yes" from "if it passes, then yes".
            // These words never initiate actions; they only reject uncertain completion evidence.
            return !System.Text.RegularExpressions.Regex.IsMatch(quote,
                @"\b(if|unless|would|might|could|perhaps|maybe|will|shall|before|once)\b|\b(let us|let's|need to|going to|want to|plan to)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static void AcceptNpcStoryAssessment(ReignDbConnection c, NpcLivesState state, AcceptedConversationUnit unit, List<Dictionary<string, object>> sources)
        {
            string current = ReadFirstString(unit.Payload, "locationId", "currentSettlementId", "settlementId");
            var playerSources = sources.Where(r => ReadString(r, "speaker_id", "") == unit.Player && ReadString(r, "role", "") == "player").ToList();
            var accepted = new HashSet<string>(playerSources.Select(r => ReadString(r, "turn_id", "")), StringComparer.Ordinal);
            foreach (var row in ReadDictionaryList(unit.Parsed, "npcStoryAssessments").Take(8))
            {
                var story = state.Stories.FirstOrDefault(s => s.Id == ReadString(row, "storyId", "") && s.HeroId == unit.Hero && s.Status == "active");
                if (story == null || story.PhaseIndex >= story.Definition.Phases.Count || story.LocationId.Length > 0 && story.LocationId != current) continue;
                if (story.Definition.Phases[story.PhaseIndex].LocationKind != "current" && ReadString(unit.Payload, "npcStoryId", "") != story.Id) continue;
                var objective = story.Definition.Phases[story.PhaseIndex].Objectives.FirstOrDefault(o => o.Id == ReadString(row, "objectiveId", "") && !o.Native);
                string quote = ReadString(row, "evidenceQuote", "").Trim();
                string outcome = ReadString(row, "outcome", ""), resolution = ReadString(row, "resolutionQuote", "").Trim();
                bool resolved = objective != null && objective.CompletionOutcomes.Contains(outcome) && NpcStoryResolutionMatches(unit.Reply, resolution);
                if (objective == null || quote.Length < 6 || outcome != "progress" && !resolved) continue;
                var source = playerSources.FirstOrDefault(r => ReadString(r, "text", "").IndexOf(quote, StringComparison.Ordinal) >= 0);
                if (source == null || RepeatedNpcStoryContribution(c, story, objective.Id, unit.Player, ReadString(source, "text", ""))) continue;
                story.AcceptEvidence(new NpcStoryEvidence { ObjectiveId = objective.Id, HeroId = unit.Hero,
                    SourceId = ReadString(source, "turn_id", ""), Quote = quote, LocationId = current,
                    Outcome = resolved ? outcome : "", ResolutionQuote = resolved ? resolution : "" },
                    ReadInt(row, "revision", -1), accepted, false, resolved);
                state.Advance(story, ReadDouble(unit.Payload, "worldDay", 0), ReadNpcLivesOptions());
            }
            var assessment = ReadDictionary(unit.Parsed, "npcLeaveAssessment");
            if (assessment != null)
            {
                var leave = state.Leaves.FirstOrDefault(l => l.Id == ReadString(assessment, "leaveId", "") && l.HeroId == unit.Hero && l.Status == "discussing");
                string kind = ReadString(assessment, "reasonKind", ""), quote = ReadString(assessment, "evidenceQuote", "").Trim();
                if (leave != null && leave.Revision == ReadInt(assessment, "revision", -1) && quote.Length >= 8
                    && new[] { "medical_need", "imminent_danger", "prior_commitment", "alternative_arrangement", "emotional_appeal", "authority_demand" }.Contains(kind)
                    && playerSources.Any(r => ReadString(r, "text", "").IndexOf(quote, StringComparison.Ordinal) >= 0)
                    && !leave.AcceptedArgumentIds.Contains(kind))
                {
                    leave.AcceptedArgumentIds.Add(kind);
                    // Stable per reason, not per message. A paraphrase cannot buy another roll.
                    bool persuaded = NpcLeaveNegotiation.Persuaded(leave, kind, ReadBool(assessment, "compelling", false), state.CampaignId + "|" + state.TimelineId);
                    if (ReadBool(assessment, "compelling", false)) leave.RefusalMitigation = 4;
                    leave.LastArgumentResult = kind + ": " + (persuaded ? "agreed to stay for now" : "still intends to leave");
                    if (persuaded) leave.StayForNow(leave.Revision, ReadDouble(unit.Payload, "worldDay", 0), NpcLivesPolicy.ReaskDelay(ReadNpcLivesOptions()));
                }
            }
            ApplyNpcLifeAffinity(c, state, unit.Player, ReadDouble(unit.Payload, "worldDay", 0));
        }

        private static void NpcLivesDecision(ReignDbConnection c, NpcLivesState state, Dictionary<string, object> p,
            string player, NpcLivesOptions options, double day)
        {
            string id = ReadString(p, "id", ""), choice = ReadString(p, "choice", ""), operation = ReadString(p, "operationId", "");
            int revision = ReadInt(p, "revision", -1);
            if (operation.Length == 0) throw new InvalidOperationException("A stable decision receipt is required.");
            string hash = PromptHash(CanonicalJson(TestDict("id", id, "revision", revision, "choice", choice,
                "heroId", ReadString(p, "heroId", ""), "playerId", player, "agreementHash", ReadString(p, "agreementHash", ""),
                "proposalId", ReadString(p, "proposalId", ""), "waitDays", ReadDouble(p, "waitDays", 0), "awayDays", ReadDouble(p, "awayDays", 5))));
            if (state.OperationReceipts.TryGetValue(operation, out var prior))
            { if (prior != hash) throw new InvalidOperationException("The decision receipt belongs to different terms."); return; }
            var story = state.Stories.FirstOrDefault(s => s.Id == id);
            if (story != null)
            {
                if (story.HeroId != ReadString(p, "heroId", "")) throw new InvalidOperationException("This story belongs to another character.");
                if (story.Revision != revision || story.Status != "invited" || story.ProposalId != ReadString(p, "proposalId", ""))
                    throw new InvalidOperationException("The story offer has changed. Review its current terms.");
                if (choice == "accept")
                {
                    if (ReadString(p, "agreementHash", "").Length == 0) throw new InvalidOperationException("Accept the explicit story action offer.");
                    story.Status = "active";
                }
                else if (choice == "decline")
                {
                    story.Status = "declined"; state.Character(story.HeroId).PendingStoryId = "";
                    if (state.Character(story.HeroId).ActiveMajorId == story.Id) state.Character(story.HeroId).ActiveMajorId = "";
                    AddNpcLifeRefusal(state, story.Id + ":declined", story.HeroId, story.Definition.Category);
                }
                else if (choice != "later") throw new InvalidOperationException("Unsupported story choice.");
                else { story.Status = "between_phases"; state.Character(story.HeroId).NextInvitationDay = day + NpcLivesPolicy.ReaskDelay(options); }
            }
            else
            {
                var leave = state.Leaves.FirstOrDefault(l => l.Id == id);
                if (leave == null || leave.HeroId != ReadString(p, "heroId", "") || leave.Revision != revision || leave.Status != "discussing" || leave.ProposalId != ReadString(p, "proposalId", ""))
                    throw new InvalidOperationException("The leave request has changed. Review its current terms.");
                if (choice == "accept" || choice == "wait")
                {
                    if (ReadString(p, "agreementHash", "").Length == 0) throw new InvalidOperationException("Agree to leave through its explicit action.");
                    double wait = ReadDouble(p, "waitDays", 0), away = ReadDouble(p, "awayDays", leave.AbsenceDays);
                    if (wait > 31.5 || away > 31.5 || !leave.AgreeDeparture(leave.ProposalId, revision, day, wait, away))
                        throw new InvalidOperationException("Leave requires a finite wait of 0–31.5 days and absence of more than zero through 31.5 days.");
                }
                else if (choice == "decline")
                {
                    AddNpcLifeRefusal(state, leave.Id + ":declined:" + leave.Revision, leave.HeroId, "leave", leave.RefusalMitigation);
                    if (NpcLeaveNegotiation.LeavesIndependently(leave, state.CampaignId + "|" + state.TimelineId))
                        leave.AgreeDeparture(leave.ProposalId, revision, day, 0, leave.AbsenceDays, independent: true);
                    else leave.StayForNow(revision, day, NpcLivesPolicy.ReaskDelay(options));
                }
                else if (choice == "later") leave.StayForNow(revision, day, NpcLivesPolicy.ReaskDelay(options));
                else throw new InvalidOperationException("Unsupported leave choice.");
            }
            state.OperationReceipts[operation] = hash;
            ApplyNpcLifeAffinity(c, state, player, day);
        }
        private static void AddNpcLifeRefusal(NpcLivesState state, string id, string hero, string category, int mitigation = 0)
        {
            if (!state.Effects.Any(e => e.Id == id)) state.Effects.Add(new NpcLifeEffect { Id = id, StoryId = id.Split(':')[0],
                HeroId = hero, Kind = "affinity", Amount = (int)Math.Round(Math.Min(0, NpcLivesPolicy.RefusalLoss(category) + mitigation)
                    * ReadNpcLivesOptions().Scale("RefusalLoss"), MidpointRounding.AwayFromZero) });
        }
        private static void ApplyNpcLifeAffinity(ReignDbConnection c, NpcLivesState state, string player, double day)
        {
            foreach (var effect in state.Effects.Where(e => e.Kind == "affinity" && e.Status == "pending"))
            {
                string key = AmbientPairKey(effect.HeroId, player);
                var ids = new[] { effect.HeroId, player }.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                EnsureMbtiRelationshipSchema(c); EnsureRelationshipNativeTargetSchema(c);
                EnsurePoliticalRelationshipPair(c, state.CampaignId, state.TimelineId, key, ids[0], ids[1], day, true);
                var pair = QuerySql(c, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", TestDict("pair", key)).Single();
                string column = ReadString(pair, "hero_a_id", "") == effect.HeroId ? "affinity_a_to_b" : "affinity_b_to_a";
                int affinity = Clamp(ReadInt(pair, column, 0) + effect.Amount, -100, 100);
                ExecuteSql(c, "UPDATE relationship_pair_chemistry SET " + column + "=$value WHERE pair_key=$pair;", TestDict("value", affinity, "pair", key));
                pair[column] = affinity;
                RefreshEffectivePairProjection(c, state.CampaignId, state.TimelineId, pair, day, "npc_personal_story");
                InvalidateRelationshipPairStateCache(state.CampaignId);
                effect.Status = "applied"; effect.NativeReceipt = effect.Id;
            }
        }

        private static void NpcLivesNativeReceipt(ReignDbConnection c, NpcLivesState state, Dictionary<string, object> p, string player, double day)
        {
            string kind = ReadString(p, "kind", ""), id = ReadString(p, "id", ""), operation = ReadString(p, "operationId", "");
            if (operation.Length == 0) throw new InvalidOperationException("Native evidence requires its stable operation identity.");
            string hash = PromptHash(CanonicalJson(TestDict("kind", kind, "id", id, "revision", ReadInt(p, "revision", -1),
                "heroId", ReadString(p, "heroId", ""), "objectiveId", ReadString(p, "objectiveId", ""), "nativeReceipt", ReadString(p, "nativeReceipt", ""),
                "encounterId", ReadString(p, "encounterId", ""), "outcome", ReadString(p, "outcome", ""), "locationId", ReadString(p, "locationId", ""),
                "meeting", ReadDictionary(p, "meeting"), "bindings", ReadDictionary(p, "bindings"),
                "nativeOutcomeEvidence", ReadDictionary(p, "nativeOutcomeEvidence"), "agreementHash", ReadString(p, "agreementHash", ""))));
            if (state.OperationReceipts.TryGetValue(operation, out var prior))
            { if (hash != prior) throw new InvalidOperationException("Native receipt terms changed."); return; }
            if (ReadString(p, "nativeReceipt", "") != operation) throw new InvalidOperationException("Native evidence requires an exact receipt identity.");
            if (kind.StartsWith("meeting_", StringComparison.Ordinal))
            {
                AcceptNpcMeetingReceipt(state, p, id, kind, day);
                state.OperationReceipts[operation] = hash; return;
            }
            var story = state.Stories.FirstOrDefault(s => s.Id == id);
            if (story != null)
            {
                bool binding = kind == "story_binding" && story.PhaseIndex >= story.Definition.SupportingRevealPhase
                    && ReadInt(p, "revision", -1) > 0 && ReadInt(p, "revision", -1) <= story.Revision;
                if ((!binding && (story.Status != "active" || story.Revision != ReadInt(p, "revision", -1))) || story.HeroId != ReadString(p, "heroId", ""))
                    throw new InvalidOperationException("Native outcome belongs to a different story phase or participant.");
                if (kind == "story_binding")
                {
                    var bindings = ReadDictionary(p, "bindings");
                    if (bindings == null || bindings.Count == 0 || bindings.Count > 5
                        || bindings.Any(b => !new[] { "heroId", "heroName", "role", "itemId", "itemName" }.Contains(b.Key)
                            || !(b.Value is string s) || s.Length == 0 || s.Length > 200))
                        throw new InvalidOperationException("The story binding must contain bounded native identities.");
                    foreach (var pair in bindings)
                    {
                        if (story.Bindings.TryGetValue(pair.Key, out var priorBinding) && priorBinding != (string)pair.Value)
                            throw new InvalidOperationException("A supporting story identity cannot be replaced.");
                        story.Bindings[pair.Key] = (string)pair.Value;
                    }
                }
                else if (kind == "encounter_started")
                {
                    string encounter = ReadString(p, "encounterId", "");
                    var objective = story.Definition.Phases[story.PhaseIndex].Objectives.FirstOrDefault(o => o.Id == ReadString(p, "objectiveId", ""));
                    if (objective == null || (objective.Kind != "duel" && objective.Kind != "hideout") || story.LocationId != ReadString(p, "locationId", ""))
                        throw new InvalidOperationException("The native encounter must match the active objective and bound location.");
                    if (encounter.Length == 0 || story.NativeEncounterId.Length > 0 && story.NativeEncounterId != encounter)
                        throw new InvalidOperationException("This story already has a different native encounter.");
                    story.NativeEncounterId = encounter;
                }
                else
                {
                    var phase = story.Definition.Phases[story.PhaseIndex];
                    var objective = phase.Objectives.FirstOrDefault(o => o.Id == ReadString(p, "objectiveId", "") && o.Native);
                    if (objective == null || (kind != "visit" && kind != "duel" && kind != "hideout" && kind != "item") || objective.Kind != kind
                        || ReadString(p, "nativeReceipt", "") != operation) throw new InvalidOperationException("Unsupported or unverified native story evidence.");
                    string outcome = ReadString(p, "outcome", "");
                    if (!NpcStoryCatalog.SupportsNativeOutcome(kind, outcome))
                        throw new InvalidOperationException("Unsupported native encounter outcome.");
                    var nativeEvidence = ReadDictionary(p, "nativeOutcomeEvidence");
                    if (outcome == "rescued" && (!story.Bindings.TryGetValue("heroId", out var rescuedId)
                        || ReadString(nativeEvidence, "heroId", "") != rescuedId || !ReadBool(nativeEvidence, "aliveAndFree", false)))
                        throw new InvalidOperationException("Rescue requires the bound native character to be alive and free.");
                    if (outcome == "recovered" && (!story.Bindings.TryGetValue("itemId", out var itemId)
                        || ReadString(nativeEvidence, "itemId", "") != itemId || ReadInt(nativeEvidence, "afterCount", 0) <= ReadInt(nativeEvidence, "beforeCount", 0)))
                        throw new InvalidOperationException("Recovery requires actual possession of the bound native item.");
                    if ((kind == "duel" || kind == "hideout") && (ReadString(p, "encounterId", "") != story.NativeEncounterId
                        || story.NativeEncounterId.Length == 0 || ReadString(p, "locationId", "") != story.LocationId))
                        throw new InvalidOperationException("The encounter result belongs to a different native mission or location.");
                    if ((kind == "duel" || kind == "hideout") && (outcome == "withdrawn" || outcome == "lost" && (kind == "hideout" || objective.RequiredOutcome.Length > 0)))
                    {
                        story.NativeEncounterId = ""; // Retry uses a new real encounter; no reward on failure.
                    }
                    else
                    {
                        bool accepted = story.AcceptEvidence(new NpcStoryEvidence { ObjectiveId = objective.Id, SourceId = operation, Kind = "native",
                            HeroId = story.HeroId, LocationId = ReadString(p, "locationId", ""), EncounterId = ReadString(p, "encounterId", ""), Outcome = outcome },
                            story.Revision, new HashSet<string>(), true);
                        if (!accepted) throw new InvalidOperationException("The native evidence did not match the saved objective, location or encounter.");
                        state.Advance(story, day, ReadNpcLivesOptions());
                    }
                }
            }
            else if (state.Leaves.FirstOrDefault(l => l.Id == id) is { } leave)
            {
                if (leave.HeroId != ReadString(p, "heroId", "") || leave.Revision != ReadInt(p, "revision", -1))
                    throw new InvalidOperationException("Leave receipt belongs to another character or revision.");
                if (kind == "departed") { if (!leave.Depart(day, true)) throw new InvalidOperationException("Departure is not due."); }
                else if (kind == "returned") {
                    if (!leave.Return(day, true)) throw new InvalidOperationException("Return is not due.");
                    state.Character(leave.HeroId).Facts.Add(new NpcLifeFact { Id = leave.Id + ":journey", Kind = "experience",
                        Text = "You completed a personal visit: " + leave.Purpose + ". The player did not accompany you; do not invent their participation.",
                        SourceId = operation, Private = true });
                }
                else if (kind == "interrupted") { leave.Status = "cancelled"; }
                else throw new InvalidOperationException("Unsupported personal-leave receipt.");
            }
            else throw new InvalidOperationException("The native receipt has no matching personal matter.");
            state.OperationReceipts[operation] = hash; ApplyNpcLifeAffinity(c, state, player, day);
        }
        private static void AcceptNpcMeetingReceipt(NpcLivesState state, Dictionary<string, object> p, string id, string kind, double day)
        {
            var supplied = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(ReadDictionary(p, "meeting")));
            if (supplied == null || supplied.Id != id || string.IsNullOrWhiteSpace(supplied.IssuerId) || string.IsNullOrWhiteSpace(supplied.TargetId)
                || supplied.IssuerId == supplied.TargetId || string.IsNullOrWhiteSpace(supplied.DestinationId) || string.IsNullOrWhiteSpace(supplied.Purpose)
                || !NpcLivesPolicy.Finite(supplied.StayDays) || supplied.StayDays < .25 || supplied.StayDays > 31.5)
                throw new InvalidOperationException("The meeting receipt has invalid participants or terms.");
            bool scheduled = supplied.ScheduledArrivalDay.HasValue;
            if (scheduled != supplied.ArrivalDelayDays.HasValue || scheduled
                && (!NpcLivesPolicy.Finite(supplied.ScheduledArrivalDay.Value) || supplied.ScheduledArrivalDay.Value < 0
                    || !NpcLivesPolicy.Finite(supplied.ArrivalDelayDays.Value) || supplied.ArrivalDelayDays.Value < 0 || supplied.ArrivalDelayDays.Value > 126))
                throw new InvalidOperationException("The meeting has an invalid scheduled arrival.");
            if (scheduled && (kind == "meeting_agreed" || kind == "meeting_revised" && supplied.Status != "meeting")
                && Math.Abs(supplied.ScheduledArrivalDay.Value - day - supplied.ArrivalDelayDays.Value) > 1e-7)
                throw new InvalidOperationException("The scheduled date must follow the accepted arrival wait.");
            var meeting = state.Meetings.FirstOrDefault(m => m.Id == id);
            if (kind == "meeting_agreed")
            {
                if (meeting != null || ReadString(p, "agreementHash", "").Length == 0 || supplied.Status != "agreed"
                    || supplied.ArrivalDay.HasValue || supplied.ReleaseDay.HasValue || supplied.ProposalId.Length == 0)
                    throw new InvalidOperationException("A meeting requires a new accepted action with exact terms.");
                state.Meetings.Add(supplied); return;
            }
            if (kind == "meeting_revised")
            {
                bool continuing = meeting != null && meeting.Status == "meeting" && supplied.Status == "meeting"
                    && supplied.DestinationId == meeting.DestinationId && supplied.ArrivalDay == meeting.ArrivalDay
                    && meeting.ArrivalDay.HasValue && meeting.ArrivalDay.Value <= day
                    && meeting.ReleaseDay.HasValue && day < meeting.ReleaseDay.Value
                    && supplied.ReleaseDay == meeting.ArrivalDay.Value + supplied.StayDays
                    && supplied.DeliveryDay == meeting.DeliveryDay && supplied.ArrivalDelayDays == meeting.ArrivalDelayDays
                    && supplied.ScheduledArrivalDay == meeting.ScheduledArrivalDay;
                bool rescheduled = supplied.Status == "agreed" && !supplied.ArrivalDay.HasValue
                    && !supplied.ReleaseDay.HasValue && !supplied.DeliveryDay.HasValue;
                if (meeting == null || meeting.Status == "completed" || meeting.Status == "cancelled" || meeting.Status == "interrupted"
                    || supplied.Revision != meeting.Revision + 1 || supplied.IssuerId != meeting.IssuerId || supplied.TargetId != meeting.TargetId
                    || supplied.Purpose != meeting.Purpose || supplied.ProposalId.Length == 0 || supplied.ProposalId == meeting.ProposalId
                    || supplied.SoloAttendance != meeting.SoloAttendance
                    || ReadString(p, "agreementHash", "").Length == 0 || !(continuing || rescheduled))
                    throw new InvalidOperationException("Changing a meeting requires fresh accepted terms for its current revision.");
                state.Meetings[state.Meetings.IndexOf(meeting)] = supplied; return;
            }
            if (kind == "meeting_cancelled" && ReadString(p, "agreementHash", "").Length == 0)
                throw new InvalidOperationException("Cancelling a meeting requires fresh accepted action terms.");
            if (meeting == null || supplied.IssuerId != meeting.IssuerId || supplied.TargetId != meeting.TargetId
                || supplied.DestinationId != meeting.DestinationId || supplied.Purpose != meeting.Purpose || supplied.StayDays != meeting.StayDays
                || supplied.ProposalId != meeting.ProposalId || supplied.Revision != meeting.Revision
                || supplied.SoloAttendance != meeting.SoloAttendance
                || supplied.ArrivalDelayDays != meeting.ArrivalDelayDays || supplied.ScheduledArrivalDay != meeting.ScheduledArrivalDay)
                throw new InvalidOperationException("The meeting receipt does not match its agreed terms.");
            if (kind == "meeting_travelling" && !scheduled && meeting.Status == "agreed" && supplied.DeliveryDay.HasValue
                && supplied.DeliveryDay.Value <= day && NpcLivesPolicy.Finite(supplied.DeliveryDay.Value))
            { meeting.DeliveryDay = supplied.DeliveryDay; meeting.Status = "travelling"; return; }
            if (kind == "meeting_arrived" && supplied.ArrivalDay.HasValue && supplied.ArrivalDay.Value <= day
                && supplied.ReleaseDay == supplied.ArrivalDay.Value + meeting.StayDays
                && (!meeting.DeliveryDay.HasValue || supplied.ArrivalDay.Value >= meeting.DeliveryDay.Value)
                && meeting.Arrive(meeting.TargetId, meeting.DestinationId, supplied.ArrivalDay.Value, true)) return;
            if (kind == "meeting_completed" && supplied.ArrivalDay == meeting.ArrivalDay && supplied.ReleaseDay == meeting.ReleaseDay && meeting.Complete(day)) return;
            if ((kind == "meeting_interrupted" || kind == "meeting_cancelled") && meeting.Status != "completed" && meeting.Status != "cancelled"
                && meeting.Status != "interrupted" && !string.IsNullOrWhiteSpace(supplied.Interruption))
            { meeting.Status = kind == "meeting_cancelled" ? "cancelled" : "interrupted"; meeting.Interruption = supplied.Interruption; return; }
            throw new InvalidOperationException("The meeting transition is not supported by its agreed date or actual arrival.");
        }
    }
}
