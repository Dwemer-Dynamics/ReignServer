using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> ContextualIntroductionFixture(
            string campaignId, string key, string text)
        {
            var player = IdentityTestHero("contextual_player", "Michael", "contextual_clan", "", false, false);
            player["isPlayer"] = true;
            player["clanName"] = "Howarton";
            var observer = IdentityTestHero("contextual_observer_" + key, "Constalia", "other_clan", "empire", true, false);
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = "contextual_intro_" + key,
                ["eventId"] = "contextual_event_" + key,
                ["worldDay"] = 51d,
                ["playerName"] = "Michael",
                ["playerIdentity"] = player,
                ["speaker"] = observer,
                ["attendees"] = new List<Dictionary<string, object>> { observer, player },
                ["playerText"] = text
            };
        }

        private static List<Dictionary<string, object>> ContextualIntroductionHistory(string observerId)
        {
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "npc", ["speakerHeroStringId"] = observerId,
                    ["text"] = "You have the advantage of me. We haven't been introduced. You know my name, but I don't know yours. Who are you, and what brings you to Phycaon beyond the lists?"
                }
            };
        }

        private static Dictionary<string, object> ContextualIntroductionResponse(
            string disposition, string name, string quote, double confidence = 0.99d)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["content"] = Json.Serialize(new Dictionary<string, object>
                {
                    ["disposition"] = disposition, ["introducedName"] = name,
                    ["evidenceQuote"] = quote, ["confidence"] = confidence
                })
            };
        }

        private static Dictionary<string, object> ResolveContextualIntroductionFixture(
            Dictionary<string, object> payload,
            Func<Dictionary<string, object>, Dictionary<string, object>> responder,
            string mode = "social_event",
            IEnumerable<Dictionary<string, object>> history = null)
        {
            string observerId = IdentityHeroId(ReadDictionary(payload, "speaker"));
            return ResolvePromptIdentity(payload, observerId, mode, ReadString(payload, "eventId", ""),
                history ?? ContextualIntroductionHistory(observerId), responder);
        }

        private static void AddContextualSelfIntroductionTests(string campaignId,
            Action<string, bool, string, object> add)
        {
            const string exactText = "*I look down for a moment embarassed* Please forgive my manners, Michael of Howarton. I was just here for the lists, tomorrow I move on to the next one, Lycaron I believe.";
            const string exactQuote = "Please forgive my manners, Michael of Howarton.";
            const string correctReply = "Michael of Howarton.\n\nWell, Michael. Manners corrected, grudge retired.";
            var captured = ContextualIntroductionFixture(campaignId, "constalia", exactText);
            int calls = 0;
            Dictionary<string, object> observedRequest = null;
            var identity = ResolveContextualIntroductionFixture(captured, request =>
            {
                calls++;
                observedRequest = request;
                return ContextualIntroductionResponse("self_introduction", "Michael of Howarton", exactQuote);
            });
            string delivered = NormalizeUnknownIdentityVisibleAddress(
                SanitizePromptForIdentity(correctReply, "", "Michael", identity), identity);
            add("contextual_introduction_constalia_final_reply", calls == 1
                && ReadBool(identity, "canonicalNameAllowed", false) && delivered == correctReply,
                "The captured apology-before-name updates identity before both production final-name filters; both Michael addresses survive.",
                new Dictionary<string, object> { ["identity"] = identity, ["deliveredReply"] = delivered });
            var packet = TryParseJsonObject(ReadString(ReadDictionaryList(observedRequest, "messages").Last(), "content", ""));
            add("contextual_introduction_bounded_context", ReadString(packet, "currentSpokenText", "").StartsWith(exactQuote, StringComparison.Ordinal)
                && !Json.Serialize(packet).Contains("embarassed", StringComparison.Ordinal)
                && ReadDictionaryList(packet, "previousExchange").Count == 1
                && ReadDictionaryList(packet, "previousExchange").Any(row =>
                    ReadString(row, "text", "").Contains("don't know yours", StringComparison.Ordinal))
                && ReadInt(observedRequest, "maxTokens", 0) == 768,
                "The helper receives the actual prior question and current spoken statement without stage directions or a full memory/profile packet.", packet);
            using (var connection = OpenCampaignConnection(campaignId))
            {
                var stored = ReadStoredAcquaintance(connection, "contextual_observer_constalia", "contextual_player");
                var evidence = QuerySql(connection, "SELECT payload_json FROM identity_evidence WHERE observer_id=$observer AND evidence_type='contextual_self_introduction';",
                    new Dictionary<string, object> { ["observer"] = "contextual_observer_constalia" });
                add("contextual_introduction_persists_with_evidence", IdentityStateVerified(stored)
                    && ReadString(stored, "verification_source", "") == "authoritative_qualified_self_introduction_contextual"
                    && evidence.Count == 1 && ReadString(TryParseJsonObject(ReadString(evidence[0], "payload_json", "")), "playerText", "") == exactQuote,
                    "A fresh database connection sees verified observer-specific knowledge and the exact source quote.", stored);
            }
            captured["playerText"] = "Perhaps you heard Michael of Howarton announced at the lists?";
            var subsequent = ResolveContextualIntroductionFixture(captured, request => { calls++; return null; });
            add("contextual_introduction_next_turn_no_helper", calls == 1 && ReadBool(subsequent, "canonicalNameAllowed", false)
                && NormalizeUnknownIdentityVisibleAddress(SanitizePromptForIdentity(correctReply, "", "Michael", subsequent), subsequent) == correctReply,
                "Learned identity survives the next prompt and adds no classification call.", null);

            int index = 0;
            foreach (string text in new[]
            {
                "Forgive me for skipping that part; Michael of Howarton. It is a pleasure.",
                "A fair question. Michael of Howarton, at your service.",
                "You are right to ask — Michael of Howarton. Tomorrow I travel east."
            })
            {
                var payload = ContextualIntroductionFixture(campaignId, "preface_" + index, text);
                int used = 0;
                var view = ResolveContextualIntroductionFixture(payload, request =>
                {
                    used++;
                    return ContextualIntroductionResponse("self_introduction", "Michael of Howarton", text);
                });
                add("contextual_introduction_preface_" + index++, used == 1 && ReadBool(view, "canonicalNameAllowed", false),
                    "A contextual introduction does not depend on one apology phrase or a name at the beginning.", view);
            }
            foreach (string text in new[] { "Michael of Howarton, Im sorry I am a traveller.", "My name is Michael.", "Call me Rowan. Michael is a friend." })
            {
                var payload = ContextualIntroductionFixture(campaignId, "fast_" + index, text);
                int used = 0;
                var view = ResolveContextualIntroductionFixture(payload, request => { used++; return null; });
                bool alias = text.Contains("Rowan", StringComparison.Ordinal);
                add("contextual_introduction_fast_path_" + index++, used == 0
                    && (alias ? ReadString(view, "claimedName", "") == "Rowan" && !ReadBool(view, "canonicalNameAllowed", false)
                        : ReadBool(view, "canonicalNameAllowed", false)),
                    "Existing explicit and name-first introductions remain local; an alias remains a claim.", view);
            }
            foreach (string text in new[]
            {
                "Good evening.", "*Michael of Howarton bows* A pleasure.", "*Michael of Howarton bows",
                "I was looking for Michaelson.", "Perhaps Michael of Nowhere could tell you.",
                new string('x', 4001) + " Michael of Howarton."
            })
            {
                var payload = ContextualIntroductionFixture(campaignId, "no_candidate_" + index, text);
                payload["contextualIntroduction"] = new Dictionary<string, object> { ["introducedName"] = "Michael", ["accepted"] = true };
                int used = 0;
                var view = ResolveContextualIntroductionFixture(payload, request => { used++; return null; });
                add("contextual_introduction_no_candidate_" + index++, used == 0 && !ReadBool(view, "canonicalNameAllowed", false),
                    "Missing, narrated, partial, unsupported or over-limit names cannot request or forge an identity upgrade.", null);
            }
            foreach (string text in new[]
            {
                "I heard Michael won the tournament.",
                "Over there, Michael, would you pass the wine?",
                "Someone shouted \"Michael\" outside the hall.",
                "If Michael were here, he would answer.",
                "You have confused me with Michael. That is not my name."
            })
            {
                var payload = ContextualIntroductionFixture(campaignId, "mention_" + index, text);
                int used = 0;
                var view = ResolveContextualIntroductionFixture(payload, request =>
                {
                    used++;
                    return ContextualIntroductionResponse("mention", "", "");
                });
                add("contextual_introduction_mention_" + index++, used == 1 && !ReadBool(view, "canonicalNameAllowed", false),
                    "An incidental, quoted, addressed, hypothetical or denied name mention alone does not reveal the speaker's identity.", null);
            }
            foreach (var bad in new[]
            {
                ContextualIntroductionResponse("self_introduction", "Michael of Howarton", exactQuote, 0.5d),
                ContextualIntroductionResponse("ambiguous", "Michael of Howarton", exactQuote),
                ContextualIntroductionResponse("self_introduction", "Michael of Nowhere", exactQuote),
                ContextualIntroductionResponse("self_introduction", "Michael of Howarton", "I am Michael of Howarton."),
                ContextualIntroductionResponse("self_introduction", "Michael of Howarton", "Michael of Howarton"),
                ContextualIntroductionResponse("self_introduction", "Michael", exactQuote),
                ContextualIntroductionResponse("self_introduction", "Michael of Howarton", exactQuote, 2d),
                new Dictionary<string, object> { ["ok"] = false, ["content"] = "{}" },
                new Dictionary<string, object> { ["ok"] = true, ["content"] = "not JSON" }
            })
            {
                var payload = ContextualIntroductionFixture(campaignId, "invalid_" + index, exactText);
                int used = 0;
                var view = ResolveContextualIntroductionFixture(payload, request => { used++; return bad; });
                string filtered = SanitizePromptForIdentity(correctReply, "", "Michael", view);
                add("contextual_introduction_invalid_evidence_" + index++, used == 1
                    && !ReadBool(view, "canonicalNameAllowed", false) && !filtered.Contains("Michael", StringComparison.Ordinal),
                    "Uncertain, unsupported, shortened, fabricated or malformed helper output fails closed and keeps the final privacy filter.", null);
            }
            var duplicate = ContextualIntroductionFixture(campaignId, "ambiguous", "Well then, Michael. Pleased to meet you.");
            duplicate["attendees"] = ReadDictionaryList(duplicate, "attendees")
                .Concat(new[] { IdentityTestHero("other_michael", "Michael", "another_clan", "", false, false) }).ToList();
            int ambiguousCalls = 0;
            var ambiguous = ResolveContextualIntroductionFixture(duplicate, request => { ambiguousCalls++; return null; });
            add("contextual_introduction_same_name_attendee", ambiguousCalls == 0 && !ReadBool(ambiguous, "canonicalNameAllowed", false),
                "A name shared with another present person cannot establish who is being addressed.", null);
            var isolated = ContextualIntroductionFixture(campaignId, "other_observer", "Good evening.");
            var isolatedView = ResolveContextualIntroductionFixture(isolated, request => { throw new InvalidOperationException("Unexpected helper"); });
            add("contextual_introduction_observer_isolation", !ReadBool(isolatedView, "canonicalNameAllowed", false),
                "Learning the name for Constalia does not grant it to another observer.", null);
            var aliasCorrection = ContextualIntroductionFixture(campaignId, "alias_correction", "Call me Rowan.");
            ResolveContextualIntroductionFixture(aliasCorrection, request => { throw new InvalidOperationException("Unexpected helper"); });
            aliasCorrection["playerText"] = exactText;
            var correctedAlias = ResolveContextualIntroductionFixture(aliasCorrection,
                request => ContextualIntroductionResponse("self_introduction", "Michael of Howarton", exactQuote));
            using (var connection = OpenCampaignConnection(campaignId))
            {
                var stored = ReadStoredAcquaintance(connection, "contextual_observer_alias_correction", "contextual_player");
                add("contextual_introduction_preserves_prior_alias", ReadBool(correctedAlias, "canonicalNameAllowed", false)
                    && ReadString(stored, "aliases_json", "").Contains("Rowan", StringComparison.Ordinal),
                    "A later supported introduction can upgrade an old alias without erasing its evidence.", stored);
            }
            var noHistory = ContextualIntroductionFixture(campaignId, "no_history", exactText);
            int noHistoryCalls = 0;
            var noHistoryView = ResolvePromptIdentity(noHistory, IdentityHeroId(ReadDictionary(noHistory, "speaker")),
                "social_event", ReadString(noHistory, "eventId", ""), null,
                request => { noHistoryCalls++; return null; });
            add("contextual_introduction_requires_scoped_adapter", noHistoryCalls == 0 && !ReadBool(noHistoryView, "canonicalNameAllowed", false),
                "A caller without canonical history cannot silently use an unscoped client transcript for semantic identification.", null);
            var failure = ResolveContextualIntroductionFixture(ContextualIntroductionFixture(campaignId, "failure", exactText),
                request => { throw new InvalidOperationException("Injected helper failure"); });
            add("contextual_introduction_helper_failure", !ReadBool(failure, "canonicalNameAllowed", false),
                "An unavailable helper keeps the existing unknown state without preventing the main reply.", null);
            bool cancelled = false;
            try
            {
                ResolveContextualIntroductionFixture(ContextualIntroductionFixture(campaignId, "cancelled", exactText),
                    request => { throw new OperationCanceledException(); });
            }
            catch (OperationCanceledException) { cancelled = true; }
            add("contextual_introduction_cancellation", cancelled, "Cancellation propagates instead of publishing a stale identity result.", null);
            bool replaced = false;
            try
            {
                ResolveContextualIntroductionFixture(ContextualIntroductionFixture(campaignId, "replaced", exactText),
                    request => { throw new CampaignRequestReplacedException(); });
            }
            catch (CampaignRequestReplacedException) { replaced = true; }
            add("contextual_introduction_campaign_replacement", replaced, "Campaign replacement cannot be swallowed as an ordinary classifier failure.", null);

            foreach (string mode in new[] { "dialogue", "social_event", "party_chat" })
            {
                var payload = ContextualIntroductionFixture(campaignId, mode, exactText);
                payload["transcript"] = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "npc", ["text"] = "UNSCOPED_CLIENT_HISTORY" } };
                payload["groupTranscript"] = payload["transcript"];
                bool rightHistory = false;
                var view = ResolveContextualIntroductionFixture(payload, request =>
                {
                    var decoded = TryParseJsonObject(ReadString(ReadDictionaryList(request, "messages").Last(), "content", ""));
                    var history = ReadDictionaryList(decoded, "previousExchange");
                    rightHistory = history.Any(row => ReadString(row, "text", "").Contains("don't know yours", StringComparison.Ordinal))
                        && history.All(row => !ReadString(row, "text", "").Contains("UNSCOPED_CLIENT_HISTORY", StringComparison.Ordinal));
                    return ContextualIntroductionResponse("self_introduction", "Michael of Howarton", exactQuote);
                }, mode);
                add("contextual_introduction_scoped_history_" + mode, rightHistory && ReadBool(view, "canonicalNameAllowed", false),
                    "The prompt adapter uses canonical channel history instead of an unrelated client transcript copy.", null);
            }
            bool isolatedLab = !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1";
            if (isolatedLab)
            {
                var offline = ResolveContextualIntroductionFixture(ContextualIntroductionFixture(campaignId, "offline", exactText), null);
                add("contextual_introduction_offline_provider_disabled", !ReadBool(offline, "canonicalNameAllowed", false),
                    "Omitting the injected responder inside Verification Lab cannot contact a production provider.", null);
            }
            else
                add("contextual_introduction_offline_provider_disabled", false,
                    "Identity verification must run through the isolated Verification Lab.", null);
        }
    }
}
