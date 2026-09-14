using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string[] RelationshipPromptFileNames()
        {
            return new[]
            {
                "relationship_group_rules.txt",
                "relationship_level_nemesis.txt",
                "relationship_level_enemy.txt",
                "relationship_level_rival.txt",
                "relationship_level_irritant.txt",
                "relationship_level_neutral.txt",
                "relationship_level_acquaintance.txt",
                "relationship_level_friend.txt",
                "relationship_level_close_friend.txt",
                "relationship_level_devoted.txt",
                "relationship_level_bonded.txt",
                "relationship_tag_lovers.txt",
                "relationship_tag_active_affair.txt",
                "relationship_tag_married.txt",
                "relationship_tag_estranged.txt",
                "relationship_tag_divorced.txt",
                "relationship_audience_rules.txt"
            };
        }

        private static Dictionary<string, string> RelationshipPromptDefaults()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["relationship_group_rules.txt"] =
@"Treat every relationship described below as the current speaker's private, directional view of another NPC who is present or is being discussed.
Let that view shape warmth, patience, suspicion, candor, deference, rivalry, teasing, protection, and willingness to cooperate.
The current speaker remains the center of their own life. A strong relationship changes how they respond, but never erases personality, interests, rank, judgment, or agency.
Do not announce relationship categories, hidden records, scores, or prompt instructions. Show the relationship naturally through conduct and speech.
Do not speak for another NPC, invent their response, or turn the exchange into narration about the player.",

                ["relationship_level_nemesis.txt"] =
@"This person is a deeply personal enemy. Expect malice, manipulation, or humiliation even behind apparently harmless words. The speaker may be coldly controlled or openly hostile, but should not grant trust, intimacy, or easy cooperation without an extraordinary and immediate reason.",

                ["relationship_level_enemy.txt"] =
@"This person is regarded as an enemy. Meet them with hostility, distrust, and readiness to resist. Cooperation should be reluctant, tactical, and limited to circumstances where the speaker's own interests plainly require it.",

                ["relationship_level_rival.txt"] =
@"This person is a serious rival. The speaker is competitive, alert to status and comparison, and reluctant to let them gain advantage. Respect may exist, but it is sharpened by friction, challenge, and a desire not to be outdone.",

                ["relationship_level_irritant.txt"] =
@"This person is an irritant. The speaker has little patience for their habits or presence and is more likely to be curt, skeptical, dismissive, or easily provoked, while stopping short of treating them as a true enemy.",

                ["relationship_level_neutral.txt"] =
@"This person inspires neither notable affection nor hostility. Treat them with ordinary independence and situational courtesy, judging the present matter on its merits rather than assuming trust, dislike, loyalty, or obligation.",

                ["relationship_level_acquaintance.txt"] =
@"This person is a familiar acquaintance. The speaker recognizes some goodwill and social ease, but trust remains limited. Conversation may be cordial and less guarded without becoming intimate, loyal, or automatically cooperative.",

                ["relationship_level_friend.txt"] =
@"This person is a friend. The speaker generally trusts their intentions, shows genuine warmth, listens with patience, and is willing to offer reasonable support, while still disagreeing or refusing when important interests conflict.",

                ["relationship_level_close_friend.txt"] =
@"This person is a close friend. The speaker can be candid, protective, forgiving, and personally invested in their welfare. Private concerns may be shared more readily, yet the speaker still has limits and an independent will.",

                ["relationship_level_devoted.txt"] =
@"This person commands deep trust and devotion. Their safety, dignity, and interests carry exceptional personal weight. The speaker is strongly inclined to defend, comfort, confide in, and stand beside them, though this bond is not romantic unless separate relationship history says so.",

                ["relationship_level_bonded.txt"] =
@"This person is one of the speaker's profound life bonds. Their approval, pain, and fate matter at the highest personal level, and the speaker treats the relationship as enduring and central. Preserve individual judgment; profound attachment is not mindless obedience and is not romantic unless separate relationship history says so.",

                ["relationship_tag_lovers.txt"] =
@"These two are lovers. Let private tenderness, familiarity, desire, protectiveness, and emotional vulnerability show when circumstances permit. Their intimacy should feel lived-in rather than newly invented.",

                ["relationship_tag_active_affair.txt"] =
@"This love is a clandestine affair. The speaker is alert to exposure, scandal, betrayed spouses, and watching eyes. In public they may conceal intimacy behind formality, distance, coded remarks, or deliberate restraint; in credible privacy the suppressed attachment may emerge.",

                ["relationship_tag_married.txt"] =
@"These two are married. Let shared history, household interests, obligations, familiarity, and the practical knowledge of one another shape the exchange. Marriage does not guarantee happiness, agreement, affection, or obedience.",

                ["relationship_tag_estranged.txt"] =
@"These two are estranged. The speaker carries distance, disappointment, hurt, or exhausted caution into the exchange. Familiarity remains, but easy warmth and ordinary marital or intimate assumptions should not return without meaningful cause.",

                ["relationship_tag_divorced.txt"] =
@"These two are divorced former spouses. Their exchange carries remembered intimacy, broken expectations, settled boundaries, and whatever warmth or bitterness their present relationship supports. Do not treat them as currently married.",

                ["relationship_audience_rules.txt"] =
@"Other people are present. Separate private feeling from public behavior.
Consider rank, etiquette, scandal, reputation, loyalties, rivals, spouses, and who may repeat what is seen or heard.
Conceal secrets that the speaker has reason to hide. Public restraint may mask affection, fear, resentment, or rivalry, but it should not erase subtle tells appropriate to the speaker's personality.
Do not expose a private relationship tag merely because it is supplied as behavioral context."
            };
        }

        private static List<Dictionary<string, object>> RelationshipPromptMetadata()
        {
            return new List<Dictionary<string, object>>
            {
                PromptMeta("relationship_group_rules.txt", "NPC Relationship Rules", "relationship", true, "Shared rules for applying directional NPC-to-NPC relationships in group scenes and when another NPC is discussed."),
                PromptMeta("relationship_level_nemesis.txt", "Nemesis", "relationship", true, "Behavior when the speaker's relationship is from -100 through -70."),
                PromptMeta("relationship_level_enemy.txt", "Enemy", "relationship", true, "Behavior when the speaker's relationship is from -69 through -50."),
                PromptMeta("relationship_level_rival.txt", "Rival", "relationship", true, "Behavior when the speaker's relationship is from -49 through -30."),
                PromptMeta("relationship_level_irritant.txt", "Irritant", "relationship", true, "Behavior when the speaker's relationship is from -29 through -10."),
                PromptMeta("relationship_level_neutral.txt", "Neutral", "relationship", true, "Behavior when the speaker's relationship is from -9 through 9."),
                PromptMeta("relationship_level_acquaintance.txt", "Acquaintance", "relationship", true, "Behavior when the speaker's relationship is from 10 through 29."),
                PromptMeta("relationship_level_friend.txt", "Friend", "relationship", true, "Behavior when the speaker's relationship is from 30 through 49."),
                PromptMeta("relationship_level_close_friend.txt", "Close Friend", "relationship", true, "Behavior when the speaker's relationship is from 50 through 69."),
                PromptMeta("relationship_level_devoted.txt", "Devoted", "relationship", true, "Behavior when the speaker's relationship is from 70 through 84."),
                PromptMeta("relationship_level_bonded.txt", "Bonded", "relationship", true, "Behavior when the speaker's relationship is from 85 through 100."),
                PromptMeta("relationship_tag_lovers.txt", "Lovers", "relationship", true, "Additional behavior for NPCs with the lovers lifecycle tag."),
                PromptMeta("relationship_tag_active_affair.txt", "Active Affair", "relationship", true, "Additional secrecy and scandal behavior for an active affair."),
                PromptMeta("relationship_tag_married.txt", "Married", "relationship", true, "Additional behavior for NPC spouses."),
                PromptMeta("relationship_tag_estranged.txt", "Estranged", "relationship", true, "Additional behavior for estranged NPC spouses or lovers."),
                PromptMeta("relationship_tag_divorced.txt", "Divorced", "relationship", true, "Additional behavior for divorced former spouses."),
                PromptMeta("relationship_audience_rules.txt", "Audience And Concealment", "relationship", true, "Rules for expressing or concealing relationships when other people are present.")
            };
        }

        private static Dictionary<string, object> BuildNpcRelationshipPromptContext(
            string campaignId,
            string speakerHeroId,
            Dictionary<string, object> eventPayload)
        {
            eventPayload = eventPayload ?? new Dictionary<string, object>();
            string mode = ReadString(eventPayload, "mode", ReadString(eventPayload, "templateId", "")).ToLowerInvariant();
            if (mode == "correspondence" || string.IsNullOrWhiteSpace(speakerHeroId))
                return EmptyNpcRelationshipPromptContext("unsupported_mode");

            string playerId = ReadFirstString(eventPayload, "playerHeroStringId", "mainHeroStringId", "playerId");
            List<Dictionary<string, object>> attendees = ReadDictionaryList(eventPayload, "attendees");
            Dictionary<string, object> speaker = ReadDictionary(eventPayload, "speaker") ?? new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(CharacterIdFrom(speaker))) attendees.Add(speaker);

            Dictionary<string, Dictionary<string, object>> profiles = attendees
                .Where(x => !string.IsNullOrWhiteSpace(CharacterIdFrom(x)))
                .GroupBy(CharacterIdFrom, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            HashSet<string> activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "activeHeroIds", "activeHeroStringIds", "activeParticipantIds" })
                activeIds.UnionWith(ReadStringList(eventPayload, key).Where(x => !string.IsNullOrWhiteSpace(x)));
            if (activeIds.Count == 0 && mode.Contains("party_chat"))
                activeIds.UnionWith(profiles.Keys);
            activeIds.Add(speakerHeroId);
            if (!string.IsNullOrWhiteSpace(playerId)) activeIds.Remove(playerId);

            List<string> mentionedIds = ResolveMentionedRelationshipHeroIds(
                campaignId, eventPayload, speakerHeroId, playerId);
            foreach (string mentionedId in mentionedIds)
            {
                if (!profiles.ContainsKey(mentionedId))
                {
                    Dictionary<string, object> mentionedProfile =
                        ReadJsonObject(CharacterFile(campaignId, mentionedId, "profile.json"));
                    if (mentionedProfile.Count > 0) profiles[mentionedId] = mentionedProfile;
                }
            }
            mentionedIds = mentionedIds.Where(profiles.ContainsKey).ToList();
            List<string> targetIds = activeIds
                .Where(x => !x.Equals(speakerHeroId, StringComparison.OrdinalIgnoreCase))
                .Concat(mentionedIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targetIds.Count == 0) return EmptyNpcRelationshipPromptContext("no_other_active_npcs");

            Dictionary<string, object> historyGeneration =
                EnsureSharedRelationshipHistoriesForConversation(
                    campaignId, speakerHeroId, activeIds, mentionedIds,
                    profiles, eventPayload);
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("NPC-TO-NPC RELATIONSHIP CONDUCT");
            builder.AppendLine(LoadPromptTemplate("relationship_group_rules.txt").Trim());
            List<Dictionary<string, object>> diagnostics = new List<Dictionary<string, object>>();
            HashSet<string> usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                EnsureIdentitySchema(connection);
                foreach (string targetId in targetIds)
                {
                    profiles.TryGetValue(targetId, out Dictionary<string, object> targetProfile);
                    targetProfile = targetProfile ?? new Dictionary<string, object> { ["heroStringId"] = targetId };
                    Dictionary<string, object> identityView = BuildIdentityView(
                        ReadAcquaintance(connection, speakerHeroId, targetId),
                        new Dictionary<string, object>
                        {
                            ["mode"] = mode,
                            ["canonicalName"] = ReadString(targetProfile, "name", ""),
                            ["subject"] = targetProfile,
                            ["appearance"] = ReadDictionary(targetProfile, "appearance") ?? new Dictionary<string, object>()
                        });
                    string label = FirstNonEmpty(ReadString(identityView, "usableName", ""), "the other person");
                    if (!usedLabels.Add(label))
                    {
                        label = label.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                            ? "the other " + label.Substring(4)
                            : "the other person known as " + label;
                        usedLabels.Add(label);
                    }

                    string pairKey = AmbientPairKey(speakerHeroId, targetId);
                    Dictionary<string, object> chemistry = QuerySql(connection,
                        "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                    bool speakerIsA = chemistry != null && ReadString(chemistry, "hero_a_id", "")
                        .Equals(speakerHeroId, StringComparison.OrdinalIgnoreCase);
                    int affinity = chemistry == null ? 0 : EffectiveDirectionalAffinity(chemistry, speakerIsA, 0);
                    string band = RelationshipBand(Clamp(affinity, -100, 100));
                    Dictionary<string, object> lifecycle = RelationshipLifecycleView(connection, speakerHeroId, targetId);
                    List<string> tags = ReadStringList(lifecycle, "tags")
                        .Where(IsPromptedRelationshipLifecycleTag)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    builder.AppendLine();
                    builder.AppendLine("Toward " + label + ":");
                    builder.AppendLine(LoadPromptTemplate(RelationshipLevelPromptFile(band)).Trim());
                    foreach (string tag in tags)
                        builder.AppendLine(LoadPromptTemplate(RelationshipTagPromptFile(tag)).Trim());
                    Dictionary<string, object> socialStanding = BuildKnownSocialStandingView(connection,
                        campaignId, ReadString(eventPayload, "timelineId", "main"), speakerHeroId, targetId,
                        IdentityStateVerified(ReadAcquaintance(connection, speakerHeroId, targetId)),
                        ReadDouble(eventPayload, "worldDay", 0d));
                    builder.AppendLine(BuildKnownSocialStandingPromptBlock(socialStanding));
                    string historyBlock = BuildSharedRelationshipHistoryPromptBlock(
                        connection, speakerHeroId, targetId, out List<string> historyIds);
                    if (!string.IsNullOrWhiteSpace(historyBlock))
                    {
                        builder.AppendLine(historyBlock);
                    }

                    diagnostics.Add(new Dictionary<string, object>
                    {
                        ["targetHeroStringId"] = targetId,
                        ["usableName"] = label,
                        ["identityState"] = ReadString(identityView, "identityState", "encountered_unknown"),
                        ["directionalAffinity"] = affinity,
                        ["relationshipBand"] = band,
                        ["lifecycleTags"] = tags,
                        ["presentInScene"] = activeIds.Contains(targetId),
                        ["mentionedWhileAbsent"] = !activeIds.Contains(targetId),
                        ["sharedRelationshipHistoryEligible"] = chemistry != null,
                        ["sharedRelationshipHistoryIds"] = historyIds,
                        ["sharedRelationshipHistoryCount"] = historyIds.Count
                    });
                }
            }

            builder.AppendLine();
            builder.AppendLine("AUDIENCE AND CONCEALMENT");
            builder.AppendLine(LoadPromptTemplate("relationship_audience_rules.txt").Trim());
            string block = builder.ToString().Trim();
            return new Dictionary<string, object>
            {
                ["block"] = block,
                ["targets"] = diagnostics,
                ["targetCount"] = diagnostics.Count,
                ["characterCount"] = block.Length,
                ["playerExcluded"] = diagnostics.All(x => !ReadString(x, "targetHeroStringId", "").Equals(playerId, StringComparison.OrdinalIgnoreCase)),
                ["historyGeneration"] = historyGeneration,
                ["reason"] = ""
            };
        }

        private static Dictionary<string, object> EmptyNpcRelationshipPromptContext(string reason)
        {
            return new Dictionary<string, object>
            {
                ["block"] = "",
                ["targets"] = new List<Dictionary<string, object>>(),
                ["targetCount"] = 0,
                ["characterCount"] = 0,
                ["playerExcluded"] = true,
                ["reason"] = reason ?? ""
            };
        }

        private static bool IsPromptedRelationshipLifecycleTag(string tag)
        {
            return new[] { "lovers", "active_affair", "married", "estranged", "divorced" }
                .Contains(tag ?? "", StringComparer.OrdinalIgnoreCase);
        }

        private static string RelationshipLevelPromptFile(string band)
        {
            string normalized = new[] { "nemesis", "enemy", "rival", "irritant", "neutral", "acquaintance", "friend", "close_friend", "devoted", "bonded" }
                .Contains(band ?? "", StringComparer.OrdinalIgnoreCase) ? band.ToLowerInvariant() : "neutral";
            return "relationship_level_" + normalized + ".txt";
        }

        private static string RelationshipTagPromptFile(string tag)
        {
            string normalized = IsPromptedRelationshipLifecycleTag(tag) ? tag.ToLowerInvariant() : "married";
            return "relationship_tag_" + normalized + ".txt";
        }

        private static List<Dictionary<string, object>> RunNpcRelationshipPromptSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = passed,
                ["suite"] = "npc_relationship_prompts",
                ["caseId"] = id,
                ["name"] = id,
                ["summary"] = summary,
                ["durationMs"] = 0
            });

            string campaignId = "rp_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                string pairKey = AmbientPairKey("speaker", "target");
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureIdentitySchema(connection);
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,first_day,last_day,updated_ts)
VALUES($pair,'speaker','target',55,-35,1,1,$ts);",
                        new Dictionary<string, object> { ["pair"] = pairKey, ["ts"] = ts });
                    ExecuteSql(connection, @"INSERT INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,updated_ts)
VALUES('speaker','target','verified','Target Noble','same_kingdom',1,$ts);",
                        new Dictionary<string, object> { ["ts"] = ts });
                    ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,affair_active,last_processed_day,updated_ts)
VALUES($pair,'speaker','target',1,1,1,$ts);",
                        new Dictionary<string, object> { ["pair"] = pairKey, ["ts"] = ts });
                }

                Dictionary<string, object> payload = new Dictionary<string, object>
                {
                    ["mode"] = "party_chat",
                    ["skipSharedRelationshipHistoryGeneration"] = true,
                    ["playerHeroStringId"] = "player",
                    ["activeHeroIds"] = new List<string> { "speaker", "target", "player" },
                    ["attendees"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["heroStringId"] = "speaker", ["name"] = "Speaker Noble" },
                        new Dictionary<string, object> { ["heroStringId"] = "target", ["name"] = "Target Noble" },
                        new Dictionary<string, object> { ["heroStringId"] = "player", ["name"] = "Canonical Player Name" }
                    }
                };
                Dictionary<string, object> forward = BuildNpcRelationshipPromptContext(campaignId, "speaker", payload);
                Dictionary<string, object> targetDiagnostic = ReadDictionaryList(forward, "targets").FirstOrDefault() ?? new Dictionary<string, object>();
                add("directional_band_injected",
                    ReadString(targetDiagnostic, "relationshipBand", "") == "close_friend",
                    "The speaker-to-target score selects the 50 through 69 close-friend prompt.");
                add("lifecycle_tags_injected",
                    ReadStringList(targetDiagnostic, "lifecycleTags").SequenceEqual(new[] { "lovers", "active_affair" }),
                    "Only current lifecycle tags are selected for additional relationship behavior.");
                add("player_never_becomes_relationship_target",
                    ReadBool(forward, "playerExcluded", false)
                    && !ReadDictionaryList(forward, "targets").Any(x => ReadString(x, "targetHeroStringId", "") == "player"),
                    "The player is excluded from NPC-to-NPC relationship prompt targets.");

                Dictionary<string, object> reversePayload = new Dictionary<string, object>(payload)
                {
                    ["activeHeroIds"] = new List<string> { "target", "speaker", "player" }
                };
                Dictionary<string, object> reverse = BuildNpcRelationshipPromptContext(campaignId, "target", reversePayload);
                add("relationship_is_directional",
                    ReadString(ReadDictionaryList(reverse, "targets").FirstOrDefault(), "relationshipBand", "") == "rival",
                    "The reverse direction independently selects the -49 through -30 rival prompt.");

                Dictionary<string, object> single = BuildNpcRelationshipPromptContext(campaignId, "speaker",
                    new Dictionary<string, object>
                    {
                        ["mode"] = "social_event",
                        ["playerHeroStringId"] = "player",
                        ["activeHeroIds"] = new List<string> { "speaker", "player" },
                        ["attendees"] = ReadDictionaryList(payload, "attendees")
                    });
                add("single_npc_player_scene_unchanged",
                    ReadInt(single, "targetCount", -1) == 0 && string.IsNullOrWhiteSpace(ReadString(single, "block", "")),
                    "A scene containing only the speaker and player receives no NPC-to-NPC relationship block.");

                Dictionary<string, object> mentioned = BuildNpcRelationshipPromptContext(campaignId, "speaker",
                    new Dictionary<string, object>
                    {
                        ["mode"] = "dialogue",
                        ["skipSharedRelationshipHistoryGeneration"] = true,
                        ["playerHeroStringId"] = "player",
                        ["activeHeroIds"] = new List<string> { "speaker", "player" },
                        ["mentionedHeroIds"] = new List<string> { "target" },
                        ["attendees"] = ReadDictionaryList(payload, "attendees")
                    });
                Dictionary<string, object> mentionedTarget =
                    ReadDictionaryList(mentioned, "targets").FirstOrDefault() ?? new Dictionary<string, object>();
                add("individual_chat_includes_discussed_npc_relationship",
                    ReadInt(mentioned, "targetCount", 0) == 1
                    && ReadString(mentionedTarget, "targetHeroStringId", "") == "target"
                    && ReadBool(mentionedTarget, "mentionedWhileAbsent", false)
                    && !ReadBool(mentionedTarget, "presentInScene", true),
                    "Individual chat receives the speaker's directional relationship context when the player discusses an absent NPC.");

                Dictionary<string, object> positiveApproach = SocialEventApproachScore(50, 80);
                Dictionary<string, object> negativeApproach = SocialEventApproachScore(50, -80);
                add("player_relation_modifies_approach",
                    ReadInt(positiveApproach, "relationshipModifier", 0) == 24
                    && ReadInt(positiveApproach, "adjustedBoldness", 0) == 74
                    && ReadInt(negativeApproach, "relationshipModifier", 0) == -24
                    && ReadInt(negativeApproach, "adjustedBoldness", 0) == 26,
                    "Player relation contributes a signed thirty-percent modifier to social-event approach boldness.");
            }
            catch (Exception ex)
            {
                add("npc_relationship_prompt_self_test_exception", false, ex.Message);
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return results;
        }
    }
}
