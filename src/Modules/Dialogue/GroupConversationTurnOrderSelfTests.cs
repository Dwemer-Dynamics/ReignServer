using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunGroupConversationTurnOrderSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void check(string id, bool passed) => results.Add(TestDict("caseId", "group_turn_order_" + id,
                "suite", "interaction_architecture", "passed", passed, "summary", "Shared local group scheduling: " + id));
            var roster = new[] {
                new GroupTurnParticipant { Id = "observer", Name = "Taren" },
                new GroupTurnParticipant { Id = "second", Name = "Lysa the Elder" },
                new GroupTurnParticipant { Id = "first", Name = "Arven" }
            };
            string text = "Arven and Lysa, what do each of you think?";
            for (int i = 0; i < 20; i++)
            {
                var focus = GroupConversationTurnOrder.ResolveAddressed(roster, text);
                var order = GroupConversationTurnOrder.Order(roster, text, "beat_" + i, focus);
                check("multiple_names_first_" + i, order.SequenceEqual(new[] { "first", "second", "observer" }));
            }
            check("full_roster_not_one_speaker", GroupConversationTurnOrder.Order(roster, "Arven, please answer.", "beat")
                .Distinct().Count() == 3);
            check("both_preserves_all_listeners", GroupConversationTurnOrder.ResolveAddressed(roster.Take(2), "Both of you, tell me your choice.").Count == 2);
            check("everyone_with_named_lead", GroupConversationTurnOrder.ResolveAddressed(roster, "Everyone, including Arven, give your opinion.").Count == 3);
            check("both_plans_not_both_people", GroupConversationTurnOrder.ResolveAddressed(roster, "Arven, both plans remain possible.").SequenceEqual(new[] { "first" }));
            check("substring_not_name", GroupConversationTurnOrder.ResolveAddressed(roster, "Arvenia is far away.").Count == 3);
            var duplicateNames = new[] { new GroupTurnParticipant { Id = "one", Name = "Lysa the Elder" },
                new GroupTurnParticipant { Id = "two", Name = "Lysa the Younger" } };
            check("ambiguous_first_name_open_floor", GroupConversationTurnOrder.ResolveAddressed(duplicateNames, "Lysa, tell me.").Count == 2);
            check("full_name_disambiguates", GroupConversationTurnOrder.ResolveAddressed(duplicateNames, "Lysa the Younger, tell me.").SequenceEqual(new[] { "two" }));
            var prior = new List<GroupTurnLine> {
                new GroupTurnLine { Role = "player", Text = "I will buy land through neutral trade." },
                new GroupTurnLine { Role = "npc", SpeakerId = "first", Text = "Do you prefer the coastal route?" },
                new GroupTurnLine { Role = "npc", SpeakerId = "second", Text = "Will you take a guard?" },
                new GroupTurnLine { Role = "player", Text = "Yes, I agree." }
            };
            check("reply_preserves_multiple_question_owners", GroupConversationTurnOrder.ResolveAddressed(roster, "Yes, I agree.", prior)
                .SequenceEqual(new[] { "first", "second" }));
            check("new_topic_does_not_reopen_old_question", GroupConversationTurnOrder.ResolveAddressed(roster, "Let us discuss the festival.", prior).Count == 3);
            prior.Add(new GroupTurnLine { Role = "npc", SpeakerId = "observer", Text = "We have settled it." });
            check("answered_question_not_reused_as_focus", GroupConversationTurnOrder.ResolveAddressed(roster, "Yes, indeed.", prior).Count == 3);
            var privateQuestion = new[] { new GroupTurnLine { Role = "npc", SpeakerId = "first", Text = "*She wonders whether he knows?* Very well." } };
            check("narrator_question_not_spoken_question", GroupConversationTurnOrder.ResolveAddressed(roster, "Yes.", privateQuestion).Count == 3);
            check("deterministic_replay", GroupConversationTurnOrder.Order(roster, "Good evening.", "same")
                .SequenceEqual(GroupConversationTurnOrder.Order(roster.Reverse(), "Good evening.", "same")));
            check("invalid_focus_id_not_speaker", GroupConversationTurnOrder.Order(roster, "Yes.", "same", new[] { "foreign", "first", "first" })
                .First() == "first" && GroupConversationTurnOrder.Order(roster, "Yes.", "same", new[] { "foreign" }).Count == 3);
            check("duplicate_roster_once", GroupConversationTurnOrder.Order(roster.Concat(roster), text, "same").Count == 3);
            check("empty_roster", GroupConversationTurnOrder.Order(null, text, "same").Count == 0);
            var profiles = roster.Select(p => TestDict("heroStringId", p.Id, "name", p.Name)).ToList();
            check("event_adapter_multiple_named", BuildSocialEventResponseOrder(TestDict("playerText", text, "turnId", "shared"), profiles)
                .SequenceEqual(new[] { "first", "second", "observer" }));
            check("event_adapter_unresolved_focus_no_provider", ResolveSocialEventAddressedHeroes(
                TestDict("playerText", "A curious evening.", "turnId", "shared"), profiles).Count == 3);
            check("event_adapter_latest_question_only", ResolveSocialEventAddressedHeroes(TestDict("playerText", "Yes.",
                "transcript", new[] { "Arven: Are you staying?", "Player: Until morning.", "Taren: That settles it.", "Player: Yes." }), profiles).Count == 3);
            return results;
        }
    }
}
