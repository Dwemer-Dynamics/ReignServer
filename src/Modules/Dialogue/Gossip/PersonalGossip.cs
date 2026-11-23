using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static readonly ConcurrentDictionary<string, object> PersonalGossipLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string PersonalGossipClockBoot = Guid.NewGuid().ToString("N");
        private static object GossipGate(string campaign) => PersonalGossipLocks.GetOrAdd(campaign, _ => new object());

        private static void EnsurePersonalGossipSchema(ReignDbConnection c)
        {
            const string marker = "personal_gossip_schema";
            if (ReignDatabase.IsPostgreSqlComponentSchemaReady(c, marker, 1)) return;
            // Save Sync snapshots the whole schema, including decisions, reservations and receipts.
            DialogueSchema.CreatePersonalGossipClock(c);
            DialogueStore.UpsertSchemaMeta(c, GossipFields("key", marker));
            ReignDatabase.MarkPostgreSqlComponentSchemaReady(c, marker, 1);
        }

        private static Dictionary<string, object> GossipJson(Dictionary<string, object> row) =>
            TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
        private static string GossipTimeline(Dictionary<string, object> payload) => ReadString(payload, "timelineId", "main");
        private static Dictionary<string, object> GossipClock(ReignDbConnection c, string timeline) =>
            DialogueStore.SelectPersonalGossipClock(c, GossipFields("timeline", timeline)).FirstOrDefault();
        private static Dictionary<string, object> GossipSnapshot(ReignDbConnection c, string timeline) =>
            TryParseJsonObject(ReadString(GossipClock(c, timeline), "snapshot_json", "{}")) ?? new Dictionary<string, object>();

        private static string GossipClockEpoch(ReignDbConnection c) => PersonalGossipClockBoot + ":"
            + Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration).ToString(CultureInfo.InvariantCulture) + ":"
            + (WorldSimulationService.TableExists(c, "memory_precision_state") ? ReadString(DialogueStore.SelectMemoryPrecisionState2(c).FirstOrDefault(), "restore_generation", "") : "");

        private static Dictionary<string, object> GossipPlayerReference(ReignDbConnection c, string observer, string player,
            Dictionary<string, object> main, Dictionary<string, object> payload)
        {
            var context = new Dictionary<string, object>(payload ?? new Dictionary<string, object>());
            context["canonicalName"] = ReadString(main, "name", "");
            context["subject"] = ReadDictionary(payload, "playerIdentity") ?? main;
            var row = WorldSimulationService.TableExists(c, "acquaintances") ? CharactersService.ReadAcquaintance(c, observer, player) : null;
            var view = CharactersService.BuildIdentityView(row, context);
            return GossipFields("label", ReadString(view, "usableName", "the stranger"),
                "identityState", ReadString(view, "identityState", "encountered_unknown"),
                "named", ReadBool(view, "knowsIdentity", false), "originalObserverId", observer);
        }

        private static bool GossipSubjectRecognized(ReignDbConnection c, Dictionary<string, object> reference,
            string observer, string player, bool originalEncounter)
        {
            if (originalEncounter && GossipSameId(ReadString(reference, "originalObserverId", ""), observer)) return true;
            if (!ReadBool(reference, "named", false) || !WorldSimulationService.TableExists(c, "acquaintances")) return false;
            var view = CharactersService.BuildIdentityView(CharactersService.ReadAcquaintance(c, observer, player), new Dictionary<string, object>());
            return ReadBool(view, "knowsIdentity", false) && string.Equals(ReadString(reference, "label", ""),
                ReadString(view, "usableName", ""), StringComparison.OrdinalIgnoreCase);
        }

        private static bool GossipAcceptPresence(ReignDbConnection c, Dictionary<string, object> payload)
        {
            var snapshot = ReadDictionary(payload, "gossipPresence");
            double day = ReadDouble(payload, "worldDay", -1), captured = ReadDouble(snapshot, "worldDay", -2);
            string timeline = GossipTimeline(payload);
            if (snapshot == null || ReadString(snapshot, "schema", "") != "reign-gossip-presence-v1"
                || !ReadBool(snapshot, "rosterComplete", false)
                || !double.IsFinite(day) || !double.IsFinite(captured) || day < 0 || Math.Abs(day - captured) > 1d / 1440
                || GossipTimeline(snapshot) != timeline || ReadDictionaryList(snapshot, "people").Count > 8192) return false;
            if (WorldSimulationService.ActiveWorldHistoryTimeline(c) != timeline) return false;
            // A clock proof belongs to this process and exact restored campaign generation.
            // Reject an old in-flight pump before it can advance the replacement's clock.
            string epoch = ReadString(payload, "gossipClockEpoch", "");
            if (epoch.Length > 0 && epoch != GossipClockEpoch(c)) return false;
            var old = GossipClock(c, timeline);
            if (old != null && ReadDouble(old, "world_day", 0) > day) return false;
            DialogueStore.UpsertPersonalGossipClock(c, GossipFields("timeline", timeline, "day", day, "json", Json.Serialize(snapshot)));
            return true;
        }

        internal static Dictionary<string, object> PersonalGossipTickApi(Dictionary<string, object> payload)
        {
            string campaign = ReadString(payload, "campaignId", "");
            if (string.IsNullOrWhiteSpace(campaign)) return GossipFields("ok", false, "error", "Campaign is required.");
            if (!ReadBool(PersonalGossipSettings(), "enabled", true)) return GossipFields("ok", true, "enabled", false);
            int delivered;
            using (PersistenceService.EnterCampaignWorkLock(GossipGate(campaign)))
            using (var c = ReignDatabase.OpenCampaignConnection(campaign))
            {
                EnsurePersonalGossipSchema(c);
                if (!GossipAcceptPresence(c, payload)) return GossipFields("ok", false, "error", "A current native gossip presence snapshot is required.");
                PublishReadyPersonalGossip(c, campaign, payload);
                delivered = DeliverPersonalGossip(c, campaign, GossipTimeline(payload), ReadDouble(payload, "worldDay", 0));
            }
            SchedulePersonalGossip(campaign);
            return GossipFields("ok", true, "delivered", delivered);
        }

        private static double GossipAffinity(ReignDbConnection c, string observer, string subject,
            Dictionary<string, object> observerProfile, Dictionary<string, object> subjectProfile)
        {
            var row = DialogueStore.SelectRelationshipPairChemistry2(c, GossipFields("pair", RelationshipsService.AmbientPairKey(observer, subject))).FirstOrDefault();
            return row == null ? RelationshipsService.InitialReignAffinity(observerProfile, subjectProfile)
                : ReputationService.EffectiveDirectionalAffinity(row, GossipSameId(ReadString(row, "hero_a_id", ""), observer));
        }

        private static bool GossipKnown(ReignDbConnection c, string observer, string target,
            Dictionary<string, object> a, Dictionary<string, object> b)
        {
            if (GossipFamily(a, b)) return true;
            if (WorldSimulationService.TableExists(c, "acquaintances") && DialogueStore.SelectAcquaintances(c, GossipFields("a", observer, "b", target)).Count > 0) return true;
            // Persisted social contact, unlike a raw native relation, establishes a known pair.
            return DialogueStore.SelectRelationshipPairChemistry3(c, GossipFields("pair", RelationshipsService.AmbientPairKey(observer, target))).Count > 0;
        }

        private static List<string> GossipMentionedIds(string spoken, Dictionary<string, object> speaker,
            Dictionary<string, object> player, IEnumerable<Dictionary<string, object>> people)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roster = people.Where(p => !string.IsNullOrWhiteSpace(GossipHeroId(p))).ToList();
            IEnumerable<string> AmbiguityNames(Dictionary<string, object> person)
            {
                string name = ReadString(person, "name", "").Trim();
                if (name.Length >= 2) yield return name;
                foreach (string separator in new[] { " the ", " of " })
                {
                    int title = name.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                    if (title >= 2) yield return name.Substring(0, title);
                }
            }
            // Shortened titled names are ambiguity guards, never new travel permission.
            // "Eupitor" in narration about Eupitor the Vintner cannot select an
            // unrelated exact-name Eupitor. Include both current participants even
            // when a bounded operator roster omits them.
            var aliasOwners = roster.Concat(new[] { speaker, player }).Where(p => p != null && !string.IsNullOrWhiteSpace(GossipHeroId(p)))
                .SelectMany(p => AmbiguityNames(p).Select(name => new { Name = name, Id = GossipHeroId(p) }))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var matches = roster.GroupBy(p => ReadString(p, "name", "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Key.Length >= 2).SelectMany(g => Regex.Matches(spoken ?? "",
                    @"(?<![\p{L}\p{N}])" + Regex.Escape(g.Key) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)
                    .Cast<Match>().Select(m => new { Start = m.Index, End = m.Index + m.Length, Ids = aliasOwners[g.Key] })).ToList();
            foreach (var match in matches)
                // Prefer the complete name in an actual spoken span; its embedded
                // short name is not a second person, even when the full name is ambiguous.
                if (match.Ids.Count == 1 && !matches.Any(other => other.Start <= match.Start && other.End >= match.End
                    && other.End - other.Start > match.End - match.Start)) result.Add(match.Ids[0]);
            // Resolve possessives separately for the NPC and player lines at the call site.
            foreach (var kin in new[] { ("mother", "motherId"), ("father", "fatherId"), ("wife", "spouseId"), ("husband", "spouseId"), ("spouse", "spouseId") })
            {
                if (Regex.IsMatch(spoken ?? "", @"\bmy\s+" + kin.Item1 + @"\b", RegexOptions.IgnoreCase)) result.Add(ReadString(speaker, kin.Item2, ""));
                if (Regex.IsMatch(spoken ?? "", @"\byour\s+" + kin.Item1 + @"\b", RegexOptions.IgnoreCase)) result.Add(ReadString(player, kin.Item2, ""));
            }
            // A child's name can be shared by unrelated native characters. The
            // spoken possessive and actual parent IDs disambiguate that person;
            // the model's proposed recipient ID is never the identity evidence.
            foreach (var relation in new[] { ("my", speaker), ("your", player) })
            {
                string parent = GossipHeroId(relation.Item2);
                if (parent.Length == 0) continue;
                var children = roster.Where(p => GossipSameId(ReadString(p, "motherId", ""), parent)
                    || GossipSameId(ReadString(p, "fatherId", ""), parent)).ToList();
                string prefix = @"\b" + relation.Item1 + @"\s+(?:(?:only|eldest|youngest|elder|younger)\s+)?(?:daughter|son|child)\b";
                foreach (var named in children.GroupBy(p => ReadString(p, "name", "").Trim(), StringComparer.OrdinalIgnoreCase))
                    if (named.Key.Length >= 2 && named.Select(GossipHeroId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                        && Regex.IsMatch(spoken ?? "", prefix + @"\s*(?:,\s*|named\s+)?" + Regex.Escape(named.Key) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase))
                        result.Add(GossipHeroId(named.First()));
                if (children.Count == 1 && Regex.IsMatch(spoken ?? "", prefix
                    + @"(?=\s*(?:[,.!?;:]|$|\b(?:about|that|how|what|whether|when|where|to|will|would|can|shall|should|has|is|was|and|as|the)\b))", RegexOptions.IgnoreCase))
                    result.Add(GossipHeroId(children[0]));
            }
            result.RemoveWhere(string.IsNullOrWhiteSpace);
            return result.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        private static double GossipConversationDelta(ReignDbConnection c, string timeline, string session, string speaker, string player)
        {
            if (!WorldSimulationService.TableExists(c, "conversation_relationship_receipts")) return 0;
            var row = DialogueStore.SelectConversationRelationshipReceipts3(c, GossipFields("speaker", speaker, "player", player, "timeline", timeline, "session", session)).FirstOrDefault();
            return ReadDouble(row, "delta", 0);
        }

        internal static void AcceptPersonalGossip(string campaign, Dictionary<string, object> payload,
            Dictionary<string, object> parsed, Dictionary<string, object> exchange, string speaker, string player,
            string playerText, string reply, Dictionary<string, object> episodeExchange = null)
        {
            try
            {
                if (ReadBool(payload, "officialMemoryFirewall", false) || string.IsNullOrWhiteSpace(speaker)
                    || string.IsNullOrWhiteSpace(player) || !ReadBool(PersonalGossipSettings(), "enabled", true)) return;
                using (PersistenceService.EnterCampaignWorkLock(GossipGate(campaign)))
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c);
                    if (!GossipAcceptPresence(c, payload)) return;
                    var episode = episodeExchange ?? exchange;
                    string timeline = GossipTimeline(payload), session = ReadString(episode, "sessionId", "");
                    double day = ReadDouble(payload, "worldDay", 0);
                    var people = ReadDictionaryList(ReadDictionary(payload, "gossipPresence"), "people");
                    var self = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), speaker));
                    var main = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), player));
                    if (self == null || session.Length == 0) return;
                    var mentioned = GossipMentionedIds(playerText, main, self, people)
                        .Concat(GossipMentionedIds(reply, self, main, people)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    mentioned.RemoveAll(id => GossipSameId(id, speaker) || GossipSameId(id, player));
                    var signals = ReadDictionaryList(parsed, "gossipSignals").Take(8)
                        .Where(s => ReadDouble(s, "confidence", 0) >= .80
                            && GossipQuoteSupported(ReadString(s, "evidenceQuote", ""), playerText + "\n" + reply)
                            && GossipSourcePrivacyClassified(s)).ToList();
                    // A relationship receipt proves a feeling, not permission to reveal
                    // that an encounter occurred. Retain the decision without disclosure
                    // until accepted source-linked metadata classifies its privacy.
                    if (signals.Count == 0) signals.Add(GossipFields("topic", "impression", "evidenceQuote", "", "confidence", 1d,
                        "privacyUnclassified", true));
                    foreach (var signal in signals)
                    {
                        signal["confidenceOwnerIds"] = GossipResolveConfidenceOwners(signal, speaker, player, mentioned, playerText);
                        // Identities are resolved from the accepted words, never model-proposed
                        // names in background context. Keep the accepted source references.
                        signal["resolvedMentionedIds"] = mentioned;
                        signal["sourceTurnIds"] = ReadStringList(exchange, "turnIds");
                        string category = GossipTopicCategory(ReadString(signal, "topic", "conversation"));
                        if (category != "impression" && (ReadDouble(signal, "confidence", 0) < .80
                            || !GossipQuoteSupported(ReadString(signal, "evidenceQuote", ""), playerText + "\n" + reply))) continue;
                        string subject = ReadString(signal, "subjectId", "");
                        if (!mentioned.Contains(subject, StringComparer.OrdinalIgnoreCase)) subject = "";
                        string id = GossipTopicId(timeline, speaker, player, day, category, subject);
                        var prior = DialogueStore.SelectPersonalGossipTopics(c, GossipFields("id", id)).FirstOrDefault();
                        if (prior != null && ReadString(prior, "status", "") == "queued") continue;
                        var topic = GossipFields("id", id, "timelineId", timeline, "speakerId", speaker, "playerId", player,
                            "sessionId", session, "createdDay", prior == null ? day : ReadDouble(prior, "created_day", day),
                            "worldDay", day, "category", category, "subjectId", subject, "signal", signal,
                            "playerReference", GossipPlayerReference(c, speaker, player, main, payload),
                            "mentionedIds", mentioned, "sourceTurnIds", ReadStringList(exchange, "turnIds"),
                            "sourceExchangeId", ReadString(episode, "exchangeId", ""), "sourceText", playerText + "\n" + reply,
                            "sourceSessionId", ReadString(exchange, "sessionId", ""), "sourceEvidenceExchangeId", ReadString(exchange, "exchangeId", ""),
                            "sourceQuote", ReadString(signal, "evidenceQuote", ""), "speaker", self,
                            "policy", prior == null ? PersonalGossipSettings() : ReadDictionary(GossipJson(prior), "policy") ?? PersonalGossipSettings(),
                            "rootId", id, "hop", 1, "holders", new List<string> { speaker },
                            "presenceComplete", ReadBool(ReadDictionary(payload, "gossipPresence"), "rosterComplete", false));
                        topic["playerName"] = ReadString(ReadDictionary(topic, "playerReference"), "label", "the stranger");
                        topic = PreservePersonalGossipSource(c, prior == null ? null : GossipJson(prior), topic);
                        CapturePersonalGossipTestControls(c, campaign, topic, prior);
                        EvaluatePersonalGossip(c, campaign, topic, people);
                    }
                    AcceptPublicWitnessGossip(c, campaign, payload, parsed, episode, speaker, player, people);
                    MarkPersonalGossipRaised(c, timeline, speaker, session, parsed, reply, payload);
                    DeliverPersonalGossip(c, campaign, timeline, day);
                }
                SchedulePersonalGossip(campaign);
            }
            catch (Exception ex) { OperationsService.LogOperational("gossip.accept_failed", GossipFields("campaignId", campaign, "error", DialogueService.LimitText(ex.Message, 500))); }
        }

        private static int PersonalGossipSourceStrength(Dictionary<string, object> topic)
        {
            if (IsOriginalPublicWitnessGossip(topic)) return GossipFeeling(topic, "witnessInterest");
            if (ReadString(topic, "category", "") == "impression") return 0;
            var signal = ReadDictionary(topic, "signal");
            return new[] { GossipFeeling(signal, "excitement"), GossipFeeling(signal, "curiosity"), GossipFeeling(signal, "hurt") }
                .Concat(ReadDictionaryList(signal, "targets").Select(target => GossipQuoteSupported(
                    ReadString(target, "evidenceQuote", ""), ReadString(topic, "sourceText", "")) ? GossipFeeling(target, "hurt") : 0))
                .DefaultIfEmpty(0).Max();
        }

        private static Dictionary<string, object> PreservePersonalGossipSource(ReignDbConnection c,
            Dictionary<string, object> prior, Dictionary<string, object> incoming)
        {
            // Called under the campaign gossip gate. Preserve an entire accepted
            // evidence bundle: quote, feelings, source/session IDs and transcript.
            // A later bland line cannot borrow the earlier line's supported feeling.
            if (prior == null || !GossipSourcesActive(c, prior)) return incoming;
            var priorIds = ReadStringList(prior, "sourceTurnIds");
            bool sameSource = priorIds.Count > 0 && priorIds.SequenceEqual(ReadStringList(incoming, "sourceTurnIds"), StringComparer.Ordinal)
                && ReadString(prior, "sessionId", "") == ReadString(incoming, "sessionId", "")
                && ReadString(prior, "sourceQuote", "") == ReadString(incoming, "sourceQuote", "");
            int previousStrength = PersonalGossipSourceStrength(prior), incomingStrength = PersonalGossipSourceStrength(incoming);
            bool retain = previousStrength > 0 && (previousStrength > incomingStrength
                || previousStrength == incomingStrength && !sameSource);
            var result = new Dictionary<string, object>(retain ? prior : incoming, StringComparer.OrdinalIgnoreCase);
            result["worldDay"] = ReadDouble(incoming, "worldDay", 0);
            result["speaker"] = ReadDictionary(incoming, "speaker");
            result["presenceComplete"] = ReadBool(incoming, "presenceComplete", true);
            result["sourceRetainedFromEarlierExchange"] = retain
                && ReadString(prior, "sourceEvidenceExchangeId", "") != ReadString(incoming, "sourceEvidenceExchangeId", "");
            result["relationshipReassessmentExchangeIds"] = (ReadString(result, "sessionId", "") == ReadString(prior, "sessionId", "")
                    ? ReadStringList(prior, "relationshipReassessmentExchangeIds").Concat(new[] { ReadString(prior, "sourceExchangeId", "") })
                    : Enumerable.Empty<string>())
                .Concat(ReadString(result, "sessionId", "") == ReadString(incoming, "sessionId", "")
                    ? new[] { ReadString(incoming, "sourceExchangeId", "") } : Array.Empty<string>())
                .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).TakeLast(64).ToList();
            foreach (string key in new[] { "policy", "savedRolls", "operatorTestControls" })
                if (prior.TryGetValue(key, out object value)) result[key] = value;
            // Repeated discussion in one episode cannot downgrade an already
            // classified confidence. This does not rewrite a queued story.
            var oldSignal = ReadDictionary(prior, "signal");
            var newSignal = ReadDictionary(incoming, "signal");
            if (ReadString(prior, "sessionId", "") == ReadString(incoming, "sessionId", "")
                && GossipSourcePrivacyClassified(oldSignal) && GossipSourcePrivacyClassified(newSignal))
            {
                var merged = new Dictionary<string, object>(ReadDictionary(result, "signal"), StringComparer.OrdinalIgnoreCase);
                var ranks = new[] { "public", "personal", "intimate", "secret" };
                foreach (string key in new[] { "privacy", "existencePrivacy" })
                    merged[key] = ranks[Math.Max(Array.IndexOf(ranks, ReadString(oldSignal, key, "public")),
                        Array.IndexOf(ranks, ReadString(newSignal, key, "public")))];
                foreach (string key in new[] { "promisedSecrecy", "ownCompromising" })
                    merged[key] = ReadBool(oldSignal, key, false) || ReadBool(newSignal, key, false);
                merged["confidenceOwnerIds"] = ReadStringList(oldSignal, "confidenceOwnerIds")
                    .Concat(ReadStringList(newSignal, "confidenceOwnerIds")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                result["signal"] = merged;
            }
            return result;
        }

        private static bool GossipSourcePrivacyClassified(Dictionary<string, object> signal) =>
            new[] { "public", "personal", "intimate", "secret" }.Contains(ReadString(signal, "privacy", ""))
            && new[] { "public", "personal", "intimate", "secret" }.Contains(ReadString(signal, "existencePrivacy", ""))
            && signal.TryGetValue("promisedSecrecy", out object promised) && promised is bool
            && signal.TryGetValue("ownCompromising", out object compromising) && compromising is bool;

        internal static void PersonalGossipRelationshipApplied(string campaign, Dictionary<string, object> payload)
        {
            try
            {
                if (!ReadBool(PersonalGossipSettings(), "enabled", true)) return;
                using (PersistenceService.EnterCampaignWorkLock(GossipGate(campaign)))
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c);
                    string exchange = ReadFirstString(payload, "exchangeId", "turnId", "sceneTurnId");
                    foreach (var row in DialogueStore.SelectPersonalGossipTopics2(c, GossipFields("day", ReadDouble(payload, "worldDay", 0) - 1)))
                    {
                        var topic = GossipJson(row);
                        if (ReadString(topic, "sourceExchangeId", "") != exchange
                            && !ReadStringList(topic, "relationshipReassessmentExchangeIds").Contains(exchange, StringComparer.Ordinal)) continue;
                        var snapshot = GossipSnapshot(c, ReadString(topic, "timelineId", "main"));
                        if (Math.Abs(ReadDouble(snapshot, "worldDay", -1) - ReadDouble(topic, "worldDay", 0)) > 1d / 1440) continue;
                        EvaluatePersonalGossip(c, campaign, topic, ReadDictionaryList(snapshot, "people"));
                    }
                }
                SchedulePersonalGossip(campaign);
            }
            catch (Exception ex) { OperationsService.LogOperational("gossip.relationship_failed", GossipFields("campaignId", campaign, "error", DialogueService.LimitText(ex.Message, 500))); }
        }

        private static List<GossipCandidate> BuildPersonalGossipCandidates(ReignDbConnection c, Dictionary<string, object> topic,
            List<Dictionary<string, object>> people, string type)
        {
            string speaker = ReadString(topic, "speakerId", ""), player = ReadString(topic, "playerId", "");
            var self = ReadDictionary(topic, "speaker");
            var signal = ReadDictionary(topic, "signal");
            var holders = ReadStringList(topic, "holders");
            var mentioned = ReadStringList(topic, "mentionedIds");
            var targets = ReadDictionaryList(signal, "targets");
            string accessible = ReadString(topic, "sourceText", "");
            var result = new List<GossipCandidate>();
            foreach (var person in people.GroupBy(GossipHeroId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                string id = GossipHeroId(person);
                var target = targets.FirstOrDefault(t => GossipSameId(ReadString(t, "heroId", ""), id)
                    && GossipQuoteSupported(ReadString(t, "evidenceQuote", ""), accessible));
                var candidate = new GossipCandidate { Id = id, Person = person,
                    Family = GossipFamily(self, person), Local = GossipLocal(self, person),
                    Mentioned = mentioned.Contains(id, StringComparer.OrdinalIgnoreCase),
                    Relevant = target != null && ReadBool(target, "relevant", false),
                    Hurt = target == null ? 0 : GossipFeeling(target, "hurt"), Grievance = target == null ? 0 : GossipFeeling(target, "grievance") };
                if (string.IsNullOrWhiteSpace(id) || GossipSameId(id, speaker) || GossipSameId(id, player)
                    || holders.Contains(id, StringComparer.OrdinalIgnoreCase)) candidate.Exclusion = "self_player_or_previous_holder";
                else if (!ReadBool(person, "isAlive", false) || ReadBool(person, "isPrisoner", true)
                    || !ReadBool(person, "available", false)) candidate.Exclusion = "unavailable";
                else if (!candidate.Local && !candidate.Mentioned) candidate.Exclusion = "unnamed_remote";
                else if (!GossipKnown(c, speaker, id, self, person)) candidate.Exclusion = "unknown_identity";
                else candidate.Affinity = GossipAffinity(c, speaker, id, self, person);
                result.Add(candidate);
            }
            return result;
        }

        private static void SavePersonalGossipTopic(ReignDbConnection c, Dictionary<string, object> topic, string status)
        {
            DialogueStore.UpsertPersonalGossipTopics(c, GossipFields("id", ReadString(topic, "id", ""), "timeline", ReadString(topic, "timelineId", "main"),
                    "speaker", ReadString(topic, "speakerId", ""), "player", ReadString(topic, "playerId", ""),
                    "session", ReadString(topic, "sessionId", ""), "day", ReadDouble(topic, "createdDay", 0), "status", status, "json", Json.Serialize(topic)));
        }

        private static void EvaluatePersonalGossip(ReignDbConnection c, string campaign, Dictionary<string, object> topic,
            List<Dictionary<string, object>> people)
        {
            string id = ReadString(topic, "id", ""), speaker = ReadString(topic, "speakerId", ""), timeline = ReadString(topic, "timelineId", "main");
            string player = ReadString(topic, "playerId", ""), session = ReadString(topic, "sessionId", "");
            var policy = ReadDictionary(topic, "policy");
            var signal = ReadDictionary(topic, "signal");
            var traits = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaign, speaker, "traits.json"));
            double day = ReadDouble(topic, "worldDay", 0);
            if (DialogueStore.SelectPersonalGossipMessages(c, GossipFields("id", id)).Count > 0) return;
            if (ReadInt(topic, "hop", 1) > ReadInt(policy, "maximumOutwardHops", 2)
                || day > ReadDouble(topic, "rootCreatedDay", ReadDouble(topic, "createdDay", day)) + ReadDouble(policy, "expiryDays", 14))
            { SavePersonalGossipTopic(c, topic, "expired_or_hop_limit"); return; }
            if (!ReadBool(topic, "presenceComplete", true)) { SavePersonalGossipTopic(c, topic, "presence_incomplete"); return; }
            topic["policyVersion"] = ReadString(policy, "policyVersion", PersonalGossipVersion);
            topic["savedRolls"] = GossipFields("share", GossipDecisionRoll(topic, id, "share"), "negative", GossipDecisionRoll(topic, id, "negative"),
                "disclosure", GossipDecisionRoll(topic, id, "disclosure"), "distortion", GossipDecisionRoll(topic, id, "distortion"), "severity", GossipDecisionRoll(topic, id, "severity"),
                "negativeRecipient", GossipDecisionDraw(topic, id, "negative_recipient"), "friendlyRecipient", GossipDecisionDraw(topic, id, "friendly_recipient"));
            if (ReadBool(signal, "privacyUnclassified", false)) { SavePersonalGossipTopic(c, topic, "privacy_unclassified"); return; }
            bool publicWitness = IsOriginalPublicWitnessGossip(topic);
            double delta = publicWitness ? 0 : GossipConversationDelta(c, timeline, session, speaker, player);
            // Public salience describes the witnessed words, not an invented private
            // feeling or a relationship receipt for a nonparticipant.
            int witnessInterest = publicWitness ? GossipFeeling(topic, "witnessInterest") : 0;
            int positiveFeeling = Math.Max(witnessInterest, Math.Max(GossipFeeling(signal, "excitement"), GossipFeeling(signal, "curiosity")));
            double supportedHurt = ReadDictionaryList(signal, "targets")
                .Where(t => GossipQuoteSupported(ReadString(t, "evidenceQuote", ""), ReadString(topic, "sourceText", "")))
                .Select(t => GossipFeeling(t, "hurt")).DefaultIfEmpty(0).Max();
            double emotion = Math.Max(positiveFeeling, Math.Max(Math.Max(supportedHurt, GossipFeeling(signal, "hurt")), Math.Min(100, 20 * Math.Abs(delta))));
            topic["delta"] = delta; topic["deltaSourceSessionId"] = publicWitness ? "" : session;
            topic["emotion"] = emotion; topic["traits"] = traits;
            topic["signalDiagnostic"] = ReadString(topic, "category", "") == "impression"
                && new[] { "excitement", "curiosity", "hurt" }.Any(key => GossipFeeling(signal, key) != 0)
                ? "impression_feelings_ignored_requires_detailed_classification" : "";
            if (ReadString(topic, "category", "") == "impression") emotion = Math.Min(100, 20 * Math.Abs(delta));
            topic["emotion"] = emotion;
            if (!GossipEmotionEligible(emotion, traits)) { SavePersonalGossipTopic(c, topic, "insufficient_feeling"); return; }
            var friendlyAudit = BuildPersonalGossipCandidates(c, topic, people, "positive");
            var negativeAudit = BuildPersonalGossipCandidates(c, topic, people, "negative");
            var friendly = GossipRecipients(friendlyAudit, "positive", GossipTrait(traits, "familyMotivation"), policy);
            var negative = publicWitness ? new List<GossipCandidate>()
                : GossipRecipients(negativeAudit, "negative", GossipTrait(traits, "familyMotivation"), policy);
            topic["candidateAudit"] = GossipFields("friendly", friendlyAudit.Where(x => x.Local || x.Mentioned).Select(x => x.Audit()).ToList(),
                "negative", negativeAudit.Where(x => x.Local || x.Mentioned).Select(x => x.Audit()).ToList(),
                "exclusionCounts", friendlyAudit.Where(x => x.Exclusion.Length > 0).GroupBy(x => x.Exclusion)
                    .ToDictionary(g => g.Key, g => (object)g.Count()));
            if (friendly.Count + negative.Count == 0) { SavePersonalGossipTopic(c, topic, ReadBool(topic, "presenceComplete", true) ? "no_audience" : "presence_incomplete"); return; }
            int share = GossipShareChance(emotion, traits);
            bool retell = ReadInt(topic, "hop", 1) > 1;
            share = retell ? SettingsService.GossipAdjustedChance(GossipRetellChance(share, ReadInt(topic, "credibility", 50)), "retell", policy)
                : SettingsService.GossipAdjustedChance(share, "share", policy);
            topic["shareChance"] = share; topic["shareRoll"] = GossipDecisionRoll(topic, id, "share");
            if (GossipDecisionRoll(topic, id, "share") > share) { SavePersonalGossipTopic(c, topic, "sharing_declined"); return; }
            var selected = GossipChooseRecipient(negative, GossipDecisionDraw(topic, id, "negative_recipient"));
            string type = "";
            int negativeChance = 0;
            if (selected != null)
            {
                var target = ReadDictionaryList(signal, "targets").FirstOrDefault(t => GossipSameId(ReadString(t, "heroId", ""), selected.Id));
                negativeChance = GossipNegativeChance(traits, selected.Hurt, selected.Affinity, selected.Grievance,
                    ReadBool(target, "knownJealousy", false), ReadBool(target, "knownEnvy", false));
                negativeChance = SettingsService.GossipAdjustedChance(negativeChance, "negative", policy);
                if (GossipDecisionRoll(topic, id, "negative") <= negativeChance) type = "negative";
            }
            if (type.Length == 0)
            {
                selected = GossipChooseRecipient(friendly, GossipDecisionDraw(topic, id, "friendly_recipient"));
                negativeChance = 0;
                if (selected != null && GossipEmotionEligible(positiveFeeling, traits) && ReadString(topic, "category", "") != "impression") type = "positive";
                else if (selected != null && delta != 0) type = "impression";
            }
            if (type.Length == 0 || selected == null) { SavePersonalGossipTopic(c, topic, "no_eligible_intent"); return; }
            topic["attemptRecipientId"] = selected.Id; topic["attemptType"] = type; topic["negativeChance"] = negativeChance;
            if (retell && type != "negative" && ReadInt(topic, "credibility", 50) < 25
                && GossipTrait(traits, "curiosity") < 60) { SavePersonalGossipTopic(c, topic, "doubtful_retelling_declined"); return; }
            double bond = GossipConfidenceBond(c, ReadDictionary(topic, "speaker"), people, ReadStringList(signal, "confidenceOwnerIds"), ReadString(topic, "playerId", ""));
            string privacy = GossipPrivacy(ReadString(signal, "privacy", "personal"));
            if (type == "impression") privacy = GossipPrivacy(ReadString(signal, "existencePrivacy", "personal"));
            int detail = GossipDetailChance(traits, privacy, emotion, selected.Affinity, bond,
                ReadBool(signal, "ownCompromising", false), ReadBool(signal, "promisedSecrecy", false), negativeChance);
            detail = SettingsService.GossipAdjustedChance(detail, "disclosure", policy);
            int disclosureRoll = GossipDecisionRoll(topic, id, "disclosure");
            topic["detailChance"] = detail; topic["disclosureRoll"] = disclosureRoll;
            if (disclosureRoll > detail)
            {
                int existence = GossipDetailChance(traits, GossipPrivacy(ReadString(signal, "existencePrivacy", privacy)), emotion,
                    selected.Affinity, bond, ReadBool(signal, "ownCompromising", false), ReadBool(signal, "promisedSecrecy", false), 0);
                existence = SettingsService.GossipAdjustedChance(existence, "disclosure", policy);
                if (type == "negative" || delta == 0 || disclosureRoll > existence) { SavePersonalGossipTopic(c, topic, "privacy_declined"); return; }
                type = "impression";
            }
            var capRows = DialogueStore.SelectPersonalGossipMessages2(c, GossipFields("timeline", timeline, "speaker", speaker, "day", day - 1, "session", session));
            if (capRows.Count(r => ReadDouble(r, "created_day", 0) > day - 1) >= ReadInt(policy, "maximumOutgoingPerDay", 3)
                || !retell && capRows.Count(r => ReadString(r, "session_id", "") == session && ReadInt(r, "hop", 1) == 1) >= ReadInt(policy, "maximumOriginalsPerConversation", 2))
            { SavePersonalGossipTopic(c, topic, "capacity_reached"); return; }
            if (publicWitness && PublicWitnessGossipCapacityReached(c, topic))
            { SavePersonalGossipTopic(c, topic, "witness_capacity_reached"); return; }
            string distortion = "none";
            int distortionChance = GossipTrait(traits, "honesty") >= 100 ? 0
                : SettingsService.GossipAdjustedChance(GossipDistortionChance(traits, selected.Hurt), "distortion", policy);
            topic["distortionChance"] = distortionChance;
            if (type == "negative" && GossipDecisionRoll(topic, id, "distortion") <= distortionChance)
                distortion = GossipDistortionSeverity(GossipDistortionScore(traits, selected.Hurt), GossipDecisionRoll(topic, id, "severity"));
            topic["type"] = type; topic["distortion"] = distortion; topic["negativeChance"] = negativeChance;
            topic["detailChance"] = detail; topic["disclosureRoll"] = disclosureRoll; topic["recipient"] = selected.Person;
            topic["recipientId"] = selected.Id; topic["local"] = selected.Local;
            double transit = GossipTransitDays(ReadDictionary(topic, "speaker"), selected.Person, policy, selected.Local);
            topic["dueDay"] = day + transit;
            topic["route"] = GossipFields("local", selected.Local, "delayDays", transit, "queuedDay", day,
                "speakerPresence", ReadDictionary(topic, "speaker"), "recipientPresence", selected.Person);
            topic["expiresDay"] = ReadDouble(topic, "rootCreatedDay", ReadDouble(topic, "createdDay", day)) + ReadDouble(policy, "expiryDays", 14);
            topic["claimKind"] = type == "impression" ? "impression" : ReadBool(signal, "question", false)
                || retell && ReadInt(topic, "credibility", 50) < 25 && type != "negative" ? "question" : "assertion";
            topic["uncertain"] = retell && type != "negative" && ReadInt(topic, "credibility", 50) < 60;
            using (var tx = c.BeginTransaction())
            {
                SavePersonalGossipTopic(c, topic, "queued");
                InsertPersonalGossipMessage(c, topic, id + "_out", false);
                tx.Commit();
            }
        }

        private static string GossipConfidenceOwnerId(string id, string speaker, string player)
        {
            id = (id ?? "").Trim();
            return GossipSameId(id, "player") ? player : GossipSameId(id, "self") ? speaker : id;
        }

        private static List<string> GossipResolveConfidenceOwners(Dictionary<string, object> signal, string speaker,
            string player, IEnumerable<string> mentioned, string playerText)
        {
            var allowed = new HashSet<string>((mentioned ?? Enumerable.Empty<string>()).Concat(new[] { speaker, player }), StringComparer.OrdinalIgnoreCase);
            var owners = new HashSet<string>(ReadStringList(signal, "confidenceOwnerIds").Take(16)
                .Select(id => GossipConfidenceOwnerId(id, speaker, player)).Where(id => !string.IsNullOrWhiteSpace(id) && allowed.Contains(id)), StringComparer.OrdinalIgnoreCase);
            // A player-authored private source entrusts the player's confidence even
            // when the classifier incorrectly names only the NPC receiving it.
            // NPC-only words and background context cannot supply this provenance.
            string quote = ReadString(signal, "evidenceQuote", "");
            if (GossipSourcePrivacyClassified(signal) && !string.IsNullOrWhiteSpace(player)
                && (ReadString(signal, "privacy", "public") != "public" || ReadString(signal, "existencePrivacy", "public") != "public"
                    || ReadBool(signal, "promisedSecrecy", false))
                && quote.Length > 0 && GossipQuoteSupported(quote, playerText)) owners.Add(player);
            return owners.OrderBy(id => id, StringComparer.Ordinal).ToList();
        }

        private static double GossipConfidenceBond(ReignDbConnection c, Dictionary<string, object> self,
            List<Dictionary<string, object>> people, List<string> owners, string player = "") => owners
            .Select(id => GossipConfidenceOwnerId(id, GossipHeroId(self), player)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !string.IsNullOrWhiteSpace(id) && !GossipSameId(id, GossipHeroId(self)))
            .Select(id => { var person = people.FirstOrDefault(p => GossipSameId(GossipHeroId(p), id));
                // Confidence owners need not be living, available recipients. A
                // persisted bond remains relevant when its owner is off this roster.
                bool knownBond = person != null || DialogueStore.SelectRelationshipPairChemistry3(c, GossipFields("pair", RelationshipsService.AmbientPairKey(GossipHeroId(self), id))).Count > 0;
                return knownBond ? Math.Max(0, GossipAffinity(c, GossipHeroId(self), id, self, person)) : 0; }).DefaultIfEmpty(0).Max();

        private static void InsertPersonalGossipMessage(ReignDbConnection c, Dictionary<string, object> data, string id, bool reply)
        {
            DialogueStore.InsertOrIgnorePersonalGossipMessages(c, GossipFields("id", id, "topic", ReadString(data, "id", ""), "root", ReadString(data, "rootId", ""),
                "timeline", ReadString(data, "timelineId", "main"), "speaker", ReadString(data, "speakerId", ""), "recipient", ReadString(data, "recipientId", ""),
                "player", ReadString(data, "playerId", ""), "reply", reply ? 1 : 0, "hop", ReadInt(data, "hop", 1),
                "day", ReadDouble(data, "worldDay", 0), "due", ReadDouble(data, "dueDay", 0), "expires", ReadDouble(data, "expiresDay", 0), "json", Json.Serialize(data)));
        }
    }
}
