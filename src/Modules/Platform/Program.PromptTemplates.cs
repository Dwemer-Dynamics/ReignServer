using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> ReadPromptTemplatesApi()
        {
            EnsurePromptTemplates();
            Dictionary<string, object> files = new Dictionary<string, object>();
            List<string> promptNames = ListPromptFileNames();
            foreach (string name in promptNames)
            {
                files[name] = File.ReadAllText(PromptPath(name), Encoding.UTF8);
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["promptsDir"] = PromptsDir,
                ["files"] = files,
                ["catalog"] = PromptCatalog(promptNames),
                ["composition"] = DialogueService.PromptCompositionInventory()
            };
        }

        private static Dictionary<string, object> SavePromptTemplatesApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (ReadBool(payload, "reset", false))
            {
                ResetPromptTemplates();
                LogOperational("prompts.reset", new Dictionary<string, object> { ["promptsDir"] = PromptsDir });
                return ReadPromptTemplatesApi();
            }

            lock (FileLock)
            {
                EnsurePromptTemplates();
                var result = PersistPromptEditorFiles(PromptsDir, ReadDictionary(payload, "files") ?? payload,
                    ReadDictionary(payload, "expectedFiles"));
                if (!ReadBool(result, "ok", false)) return result;
                LogOperational("prompts.save", new Dictionary<string, object> { ["saved"] = result["saved"], ["promptsDir"] = PromptsDir });
                DialogueService.InvalidatePromptRuntimeCache();
                // Return the exact persisted bytes, without another migration pass after the write.
                return result;
            }
        }

        internal static void EnsurePromptTemplates()
        {
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            lock (FileLock)
            {
                Directory.CreateDirectory(PromptsDir);
                DialogueService.RetireLegacyPromptTemplates(PromptsDir);
                MigrateLegacyNoblePromptTemplate();
				MigrateLegacyPortraitIdentity(defaults);
                MigrateLegacyPortraitPromptStyle(defaults);
                foreach (string name in PromptFileNames)
                {
                    string path = PromptPath(name);
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path, DefaultPromptText(name, defaults), Encoding.UTF8);
                    }
                }
                PortraitsService.RemoveSavedPortraitClothingColorGuidance();
            }
        }

        private static void ResetPromptTemplates()
        {
            Dictionary<string, string> defaults = DefaultPromptTemplates();
            lock (FileLock)
            {
                Directory.CreateDirectory(PromptsDir);
                DialogueService.RetireLegacyPromptTemplates(PromptsDir);
                MigrateLegacyNoblePromptTemplate();
                foreach (string name in PromptFileNames)
                {
                    File.WriteAllText(PromptPath(name), DefaultPromptText(name, defaults), Encoding.UTF8);
                }
                DialogueService.InvalidatePromptRuntimeCache();
            }
        }

        private static void MigrateLegacyNoblePromptTemplate()
        {
            string legacyPath = PromptPath("lord_lady_station.txt");
            if (!File.Exists(legacyPath))
            {
                return;
            }

            string noblePath = PromptPath("noble_prompt.txt");
            if (!File.Exists(noblePath))
            {
                File.Move(legacyPath, noblePath);
                LogOperational("prompts.noble_prompt_migrated", new Dictionary<string, object>
                {
                    ["source"] = legacyPath,
                    ["destination"] = noblePath,
                    ["preservedCustomContent"] = true
                });
            }
            else
            {
                string backupPath = noblePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".legacy_backup";
                File.Move(legacyPath, backupPath);
                LogOperational("prompts.noble_prompt_migration_conflict", new Dictionary<string, object>
                {
                    ["source"] = legacyPath,
                    ["destination"] = noblePath,
                    ["legacyBackup"] = backupPath,
                    ["preservedCustomContent"] = true
                });
            }

        }

        private static void MigrateLegacyPortraitPromptStyle(Dictionary<string, string> defaults)
        {
            string path = PromptPath("portrait_prompt_style.txt");
            if (!File.Exists(path) || defaults == null || !defaults.TryGetValue("portrait_prompt_style.txt", out string updated))
            {
                return;
            }

            const string legacy = @"Portrait style is changeable guidance layered around the protected model-specific image request rules.
Keep portraits grounded, human, and believable rather than fantasy illustration.
Preserve the character identity from the source capture: face, age, expression, hair, skin tone, facial structure, and cultural identity.
Prefer chest-up realistic portrait photography, natural skin texture, believable eyes, soft natural light, and culturally appropriate civilian clothing.
Avoid armor, weapons, helmets, chainmail, plastic skin, over-smoothed faces, CGI, cartoon, painterly style, and modern fashion.";
            string current = File.ReadAllText(path, Encoding.UTF8);
            if (string.Equals(current.Trim(), legacy.Trim(), StringComparison.Ordinal))
            {
                File.WriteAllText(path, updated, Encoding.UTF8);
            }
        }

		private static void MigrateLegacyPortraitIdentity(Dictionary<string, string> defaults)
		{
			string path = PromptPath("portrait_identity.txt");
			if (!File.Exists(path) || defaults == null || !defaults.TryGetValue("portrait_identity.txt", out string updated))
			{
				return;
			}

			const string legacy = @"IDENTITY AND SUBJECT
Use the provided source image as the visual identity reference, not as a style reference.
Create a portrait of [CHARACTER NAME], a [GENDER] of [CULTURE] culture who is [AGE] years old.
Preserve the source face, apparent age, expression, eye shape, hair color, hairstyle, skin tone, facial structure, scars, facial hair, and overall identity as closely as possible.
Treat the name, age, gender, and culture as private generation guidance. They describe the subject and must never be printed, captioned, labeled, or otherwise rendered in the image.";
			string current = File.ReadAllText(path, Encoding.UTF8);
			if (string.Equals(current.Trim(), legacy.Trim(), StringComparison.Ordinal))
			{
				File.WriteAllText(path, updated, Encoding.UTF8);
			}
		}

        internal static string LoadPromptTemplate(string name)
        {
            return DialogueService.LoadPromptTemplateFromRuntimeCache(name);
        }

        internal static string PromptPath(string name)
        {
            return Path.Combine(PromptsDir, Path.GetFileName(name));
        }

        private static List<string> ListPromptFileNames()
        {
            Directory.CreateDirectory(PromptsDir);
            HashSet<string> names = new HashSet<string>(PromptFileNames, StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.GetFiles(PromptsDir))
            {
                string name = Path.GetFileName(path);
                if (IsPromptFileNameAllowed(name))
                {
                    names.Add(name);
                }
            }

            return names.OrderBy(x => PromptSortKey(x)).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static bool IsPromptFileNameAllowed(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            if (DialogueService.RetiredPromptHashes.ContainsKey(name)) return false;
            string extension = Path.GetExtension(name).ToLowerInvariant();
            return extension == ".txt" || extension == ".json" || extension == ".md";
        }

        private static int PromptSortKey(string name)
        {
            for (int i = 0; i < PromptFileNames.Length; i++)
            {
                if (string.Equals(PromptFileNames[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 1000;
        }

        private static List<Dictionary<string, object>> PromptCatalog(List<string> promptNames)
        {
            Dictionary<string, Dictionary<string, object>> known = PromptMetadataByName();
            List<Dictionary<string, object>> catalog = new List<Dictionary<string, object>>();
            foreach (string name in promptNames)
            {
                if (known.TryGetValue(name, out Dictionary<string, object> meta))
                {
                    catalog.Add(meta);
                    continue;
                }

                bool hidden = name.EndsWith("_schema.json", StringComparison.OrdinalIgnoreCase) || name.Contains("output_schema");
                bool castleChat = name.StartsWith("castle_chat_", StringComparison.OrdinalIgnoreCase);
                hidden = hidden || castleChat;
                string category = castleChat ? "castle-chat" : name.StartsWith("action_", StringComparison.OrdinalIgnoreCase)
                    ? "action"
                    : (name.StartsWith("relationship_", StringComparison.OrdinalIgnoreCase)
                        ? "relationship"
                        : (name.IndexOf("portrait", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0 ? "image" : "general"));
                catalog.Add(PromptMeta(
                    name,
                    DialogueService.HumanizeKey(Path.GetFileNameWithoutExtension(name)),
                    category,
                    !hidden,
                    category == "action"
                        ? "Action-specific selector prompt for " + DialogueService.HumanizeKey(Path.GetFileNameWithoutExtension(name).Replace("action_", "")) + "."
                        : "Additional prompt file found in the server prompts folder."));
            }

            return catalog;
        }

        internal static Dictionary<string, Dictionary<string, object>> PromptMetadataByName()
        {
            List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>
            {
                PromptMeta("world_tone.txt", "World Tone", "general", true, "Global Bannerlord Reign atmosphere and world behavior."),
                PromptMeta("global_response_override.txt", "Global Response Override", "general", true, "Optional global instructions applied to dialogue and event responses. Empty by default."),
                PromptMeta("noble_prompt.txt", "Noble Prompt", "general", true, "Additional character layer applied only when the game profile has isLord=true."),
                PromptMeta("commoner_prompt.txt", "Commoner Prompt", "general", true, "World-aligned individual voice and livelihood for encountered commoners, applied during construction and every conversation, Homes visit, group exchange and letter. Existing noble and notable roles retain their own foundations."),
                PromptMeta("ambassador_role.txt", "Resident Ambassador Role", "general", true, "Non-overridable official-channel duties, relay rules, authority limits, and private-memory firewall for resident ambassadors."),
                PromptMeta("tavern_house_negotiation_role.txt", "Tavern House: Madam Negotiation", "general", true, "Character-led participant and price negotiation; payment and companion recruitment require native confirmation."),
                PromptMeta("tavern_house_arrival_image.txt", "Tavern House: Arrival Image", "image", true, "Non-explicit arrival scene with the player and all agreed participants. Tokens: {townName}, {participants}."),
                PromptMeta("tavern_house_look_again_summary.txt", "Tavern House: Look Again Summary", "image", true, "Summarizes the complete completed conversation into a faithful non-explicit visual moment."),
                PromptMeta("tavern_house_look_again_image.txt", "Tavern House: Look Again Image", "image", true, "Non-explicit scene from the conversation summary and every participant reference. Tokens: {townName}, {participants}, {summary}."),
                PromptMeta("tavern_house_portrait_clothing.txt", "Tavern House: Portrait Clothing", "image", true, "One clothing-only edit for every adult madam and worker, regardless of culture or gender. Uses the configured Adult Portraits provider after the identity portrait. Token: [TAVERN ROLE]."),
                PromptMeta(PortraitsService.OutfitPromptName, "Conversation: New Clothes", "image", true, "Dedicated clothing-only edit of the current full-body portrait. The player's exact outfit description is appended as quoted garment data. Normal Portraits at Physical Confidence 0-39; Adult Portraits at 40-100. No automatic fallback between profiles."),
                PromptMeta(PortraitsService.ChatViewSelectionPromptName, "Chat View: Moment Selection", "image", true, "Selects one evidenced high point from four complete exchanges using the selected dialogue model."),
                PromptMeta(PortraitsService.ChatViewPhotoPromptName, "Chat View: Documentary Photograph", "image", true, "Photographic rendering instructions for the selected instant and complete full-body identity references."),
                PromptMeta("context_pull_check_inventory_appearance.txt", "Check Inventory And Appearance", "context", true, "Selector-readable header for NPC inventory, equipment, and material presentation."),
                PromptMeta("context_pull_check_player_appearance_status.txt", "Check Player Appearance Status", "context", true, "Selector-readable header for the player's visible clothing, status, and disguise cues."),
                PromptMeta("context_pull_nearby_settlements.txt", "Nearby Settlements", "context", true, "Selector-readable header for nearby towns, castles, villages, and routes."),
                PromptMeta("context_pull_nearby_bandit_parties.txt", "Nearby Bandit Parties", "context", true, "Selector-readable header for nearby looters, bandits, raiders, and road danger."),
                PromptMeta("context_pull_nearby_lord_parties.txt", "Nearby Lord Parties", "context", true, "Selector-readable header for nearby lord parties, armies, allies, and enemies."),
                PromptMeta("context_pull_current_settlement_facts.txt", "Current Settlement Facts", "context", true, "Selector-readable header for the current town, castle, village, or local scene."),
                PromptMeta("context_pull_kingdom_diplomacy_status.txt", "Kingdom Diplomacy Status", "context", true, "Selector-readable header for wars, peace, allies, enemies, and surrender context."),
                PromptMeta("context_pull_clan_wealth_and_influence.txt", "Clan Wealth And Influence", "context", true, "Selector-readable header for clan wealth, renown, influence, and social credit."),
                PromptMeta("context_pull_appraise_trade_offer.txt", "Appraise Trade Offer", "context", true, "Selector-readable header for valuing gold, items, prisoners, workshops, settlements, ships, and package fairness."),
                PromptMeta("context_pull_verify_world_history.txt", "Verify World History", "context", true, "Selector-readable header for evidence-backed checks of historical claims, credit, blame, and alibis."),
                PromptMeta("context_pull_relevant_memory.txt", "Relevant Memory", "context", true, "Selector-readable header for durable memory, backstory, secrets, and prior promises."),
                PromptMeta("context_pull_relationship_history.txt", "Relationship History", "context", true, "Selector-readable header for relationship standing and recent dialogue history."),
                PromptMeta("dialogue_system.txt", "Dialogue System Message", "general", true, "Top-level system message for individual conversations."),
                PromptMeta("dialogue_live_turn_template.txt", "Dialogue Live Turn", "general", true, "Non-cacheable scene, memory, and latest-message template for individual conversations."),
                PromptMeta("dialogue_internal_posture.txt", "Dialogue Decision Policy", "general", true, "Private deliberation instructions and compact decision-brief limits. Raw reasoning is forbidden."),
                PromptMeta("jealousy_approach.txt", "Jealousy Approach", "general", true, "NPC-first opening using witnessed rival attention and authoritative neglect severity."),
                PromptMeta("rescue_opening.txt", "Rescue Opening", "general", true, "NPC-first response to verified battle or prison rescue evidence."),
                PromptMeta("dialogue_output_schema.json", "Dialogue Output Schema", "hidden", false, "Strict JSON contract for individual conversation replies."),
                PromptMeta("pull_out.txt", "Pull Out", "general", true, "Authoritative player-action prompt sent as a normal conversation turn after the player prevents an insemination pregnancy roll."),
                PromptMeta("action_planner_system.txt", "Hidden Action Planner System", "general", true, "System message for converting action gates into strict action JSON."),
                PromptMeta("action_planner_live_turn_template.txt", "Action Planner Live Turn", "general", true, "Non-cacheable commitment and conversation data for the hidden action planner."),
                PromptMeta("action_planner_output_schema.json", "Hidden Action Planner Output Schema", "hidden", false, "Strict JSON contract for hidden planner results."),
                PromptMeta("event_system.txt", "Event System Message", "general", true, "Top-level system message for social event scenes."),
                PromptMeta("event_live_turn_template.txt", "Event Live Turn", "general", true, "Non-cacheable event, memory, and latest-message template for social scenes."),
                PromptMeta("event_output_schema.json", "Event Output Schema", "hidden", false, "Strict JSON contract for social event replies."),
                PromptMeta("action_suggestion_rules.txt", "Action Suggestion Rules", "general", true, "Rules for when dialogue or events may suggest diplomacy, rebellion, army, or settlement actions."),
                PromptMeta("action_declare_war.txt", "Declare War", "action", true, "Action-specific prompt for kingdom war declarations."),
                PromptMeta("action_make_peace.txt", "Make Peace", "action", true, "Action-specific prompt for simple peace agreements."),
                PromptMeta("action_offer_tribute_peace.txt", "Offer Tribute Peace", "action", true, "Action-specific prompt for peace where the actor offers tribute."),
                PromptMeta("action_record_promise.txt", "Record Promise", "action", true, "Action-specific prompt for diplomatic promises and obligations."),
                PromptMeta("action_demand_reparations_peace.txt", "Demand Reparations Peace", "action", true, "Action-specific prompt for peace with gold or tribute reparations."),
                PromptMeta("action_demand_settlement_peace.txt", "Demand Settlement Peace", "action", true, "Action-specific prompt for settlement surrender terms."),
                PromptMeta("action_demand_surrender_peace.txt", "Demand Surrender Peace", "action", true, "Action-specific prompt for broad surrender terms."),
                PromptMeta("action_diplomatic_package.txt", "Diplomatic Package", "action", true, "Action-specific prompt for compound bargains with gold, prisoners, items, settlements, or treaty terms."),
                PromptMeta("action_trade_package.txt", "Trade Package", "action", true, "Action-specific prompt for appraised buy/sell/barter packages involving gold, items, prisoners, workshops, settlements, or ships."),
                PromptMeta("action_create_clan_accord.txt", "Create Clan Accord", "action", true, "Fixed player-to-NPC clan cooperation, explicitly accepted by the player and current noble."),
                PromptMeta("action_cancel_clan_accord.txt", "Cancel Clan Accord", "action", true, "Explicit player cancellation of one named active clan accord."),
                PromptMeta("action_recruit_and_recover.txt", "Recruit And Recover", "action", true, "Action-specific prompt for lord recruitment and recovery orders."),
                PromptMeta("action_form_army.txt", "Form Army", "action", true, "Action-specific prompt for army formation."),
                PromptMeta("action_attack_settlement.txt", "Attack Settlement", "action", true, "Action-specific prompt for raiding or besieging a target settlement."),
                PromptMeta("action_capture_settlement_plan.txt", "Capture Settlement Plan", "action", true, "Action-specific prompt for multi-stage settlement capture."),
                PromptMeta("action_start_ruling_clan_rebellion.txt", "Start Ruling Clan Rebellion", "action", true, "Action-specific prompt for claimant rebellions against a ruling clan."),
                PromptMeta("action_recruit_lord_to_rebellion.txt", "Recruit Lord To Rebellion", "action", true, "Action-specific prompt for an accepted lord recruitment into the player's active rebellion."),
                PromptMeta("action_join_rebellion.txt", "Join Rebellion", "action", true, "Action-specific prompt for the player joining an accepting NPC rebel leader."),
                PromptMeta("action_surrender_rebellion.txt", "Surrender Rebellion", "action", true, "Action-specific prompt for a named leader's explicit civil-war surrender."),
                PromptMeta("action_install_ruling_clan.txt", "Install Ruling Clan", "action", true, "Action-specific prompt for replacing the ruling clan after a successful internal challenge."),
                PromptMeta("memory_write_rules.txt", "Memory Write Rules", "general", true, "Rules for what should become durable character memory."),
                PromptMeta("correspondence_system.txt", "Correspondence System", "general", true, "System rules for NPC-authored letters."),
                PromptMeta("correspondence_live_turn_template.txt", "Correspondence Live Turn", "general", true, "Non-cacheable relationship, memory, and received-letter template."),
				PromptMeta("portrait_identity.txt", "Portrait Identity", "image", true, "Source-likeness and dynamic character identity guidance. Available tokens: [CHARACTER NAME], [AGE], [CULTURE], [GENDER], [CLAN TIER], [SOCIAL STATION], and [PHYSICAL CONFIDENCE]."),
                PromptMeta("portrait_composition.txt", "Portrait Composition", "image", true, "Framing, crop, camera distance, and portrait background guidance."),
                PromptMeta("portrait_prompt_style.txt", "Portrait Photographic Style", "image", true, "Realism, lighting, lens, skin, hair, and photographic finish guidance."),
                PromptMeta("portrait_output_rules.txt", "Portrait Output Exclusions", "image", true, "Elements that must not appear, including text, captions, labels, white footer panels, card layouts, watermarks, and war gear.")
            };
			entries.AddRange(PortraitsService.PortraitClothingPromptMetadata());
			entries.AddRange(PortraitsService.PortraitPhysicalConfidencePromptMetadata());
            entries.Add(PromptMeta(PortraitsService.PortraitBodyPromptFile, "Native Portrait Body Appearance", "image", true, "Independent native weight and muscle-build guidance for all character portrait stages; 0–1 values, not percentages."));
			entries.AddRange(RelationshipsService.RelationshipPromptMetadata());

            return entries.ToDictionary(x => ReadString(x, "name", ""), x => x, StringComparer.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> PromptMeta(string name, string label, string category, bool visible, string description)
        {
            return new Dictionary<string, object>
            {
                ["name"] = name,
                ["label"] = label,
                ["category"] = category,
                ["visible"] = visible,
                ["description"] = description
            };
        }

        internal static string DefaultPromptText(string name, Dictionary<string, string> defaults)
        {
            if (defaults != null && defaults.TryGetValue(name, out string value))
            {
                return value;
            }

            if (name.StartsWith("action_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultActionPromptTemplate(name);
            }

            return string.Empty;
        }

        private static string DefaultActionPromptTemplate(string name)
        {
            string id = Path.GetFileNameWithoutExtension(name).Replace("action_", "");
            string mappedType = MapCommandToActionType(id);
            string family = ActionFamilyForId(id);
            string requiredIds = ActionRequiredIdsForId(id, family);
            string extraRules = ActionPromptExtraRulesForId(id);

            return "id: " + id + @"
family: " + family + @"
maps_to: " + mappedType + @"
purpose: " + ActionPurposeForId(id) + @"
when_to_suggest: Use only when the character, court, event, or diplomatic scene supplies a believable motive and the required live game IDs are known.
do_not_suggest_when: Do not use for vague intent, casual talk, missing IDs, or situations where the character lacks standing to push the action. Discuss the idea in dialogue instead.
required_ids: " + requiredIds + @"
required_context: Pull only the compact context needed for this action: authority, current diplomacy, clan standing, known settlements, wealth, prisoners, or military pressure as relevant.
validation_notes: The game is authoritative. Never invent hero, clan, kingdom, settlement, or party IDs. Execution still goes through Bannerlord Reign validation.
output_shape: command=" + id + @"; include known ids when available; otherwise use exact name fields such as TargetSettlement, TargetHero, TargetClan, TargetKingdom, FromHero, ToHero, GoldAmount, Item, Amount, or PrisonerHero; include reason; include terms only when the command needs gold, duration, settlement lists, prisoner limits, allTargetFortifications, or third-kingdom targets; requiresAcceptance=true while the action is only proposed or being negotiated; requiresAcceptance=false only when the visible reply commits, accepts, signs, grants, or finalizes the action now.
" + extraRules + @"
consequence_model: If executed, write consequences into memory/history so future dialogue remembers the political cost, humiliation, obligation, debt, betrayal, or alliance.
token_budget: 520
privacy: Suggested action metadata is server/debug material until validated.";
        }

        internal static string ActionPromptExtraRulesForId(string id)
        {
            switch (id)
            {
                case "create_clan_accord":
                    return "action_rules: Only in supplied eligible clanAccords context for a player ruler. The current adult noble can bind their clan even when not clan leader. Both actual speakers must explicitly accept now; terms.playerConfirmed and terms.consentConfirmed are true only with visible acceptance. accordType is Trade, MutualWatch, Agricultural, Artisan or Garrison. Fixed benefits to each clan: 50 denars/day; 0.1 security/day per owned town/castle; 0.2 hearth/day per owned village; 0.1 prosperity/day per owned town; 2% garrison wage reduction. Player slots per type equal clan tier; NPC slots are unrestricted. One type per partner. Never invent amounts. Eyes and ears means MutualWatch security only, never reports or spy recruitment. This is not a kingdom trade treaty or government vote commitment. Tentative, refused, conditional or hypothetical speech cannot execute. actorHeroId is current NPC and TargetHero is player. Native state revalidates before success.\n";
                case "cancel_clan_accord":
                    return "action_rules: Only the player's explicit cancellation decision ends a named active accord from supplied current-partner clanAccords records. Copy accordId, never invent it. terms.playerConfirmed requires the player's actual present instruction. The NPC need not approve, but cannot autonomously cancel. Removes both clans' benefits and applies the fixed cancellation relation consequence; do not expose actual hidden relationship values. actorHeroId is current NPC and TargetHero is player.\n";
                case "attack_player_party":
                    return @"action_rules: AttackPlayerParty is an NPC-against-player act. actorHeroId is the current adult NPC and TargetHero is the player. Use only when the NPC visibly commits to immediate violence and their supplied native personal relation to the player is -40 or lower. Anger, suspicion, insults, warnings, or an unsupported threat do not by themselves authorize an attack. The game revalidates relation, party state, adulthood, and entity direction before execution.
";
                case "duel_player":
                    return @"action_rules: DuelPlayer is conversation-scoped. Use it only when the current adult one-on-one NPC visibly accepts or demands a duel with the player. actorHeroId is the current NPC/opponent and TargetHero is the player. The game will not teleport anyone; it arms a mission hook that starts after the conversation closes if both agents are present.
terms: duelMode must be ""practice"", ""training_together"", ""honor"" or ""lethal"". Practice is consensual, nonlethal sparring: duelPurpose=""practice"", lethal=false. Its result is remembered only by the NPC opponent; it creates no public combat record, kingdom news, reputation or automatic relationship change. Use practice for requests to practice or spar without reputational stakes. Honor is a nonlethal personal challenge with public win/loss evidence. Lethal is a fight to the death or possible death, implies duelPurpose=""lethal"", and may be emitted only when the supplied native personal relation is -80 or lower. Training together uses duelMode=""training_together"" and exactly the same consent, private memory, nonlethal purpose and social rules as practice. Use it when the player asks to train together. The legacy training mode remains supported for old actions. Optional terms: lethal, challengeTone, targetDeathRiskPercent, playerDeathRiskPercent, allowPlayerDeath, pauseNearbyAgents, pauseRadius, playerHealth, opponentHealth, trainingStopHealth. The game revalidates adulthood, personal relation, active one-to-one context, and entity direction before execution. Practice and training_together modes override stale hostile purpose and death-risk terms; never turn practice into an arrest or honor challenge.
";
                case "prepare_arrest":
                case "confirm_arrest":
                case "release_arrested_character":
                case "rescind_arrest_accusation":
                case "player_attack_party":
                    return @"action_rules: Arrest commands are player-against-current-NPC actions and use the supplied private arrestContext. A first arrest requires a stated accusation and only prepares the guards; confirm_arrest requires a later explicit take-them-away command. Release preserves the accusation. Rescission clears it and releases custody when applicable. player_attack_party is valid only after a field-party surrender refusal and intentionally uses Bannerlord's native hostile encounter path.
";
                case "issue_campaign_order":
                    return @"action_rules: Campaign orders are draft-first. Extract the complete ordered sequence into terms.steps even when the player uses one sentence: each step has objective plus its own targetSettlementStringId, targetPartyId/targetHeroStringId, region, durationHours, and recruitment thresholds as applicable. Never execute from the first readback; the campaign-command draft gate requires first confirmation and an immutable final confirmation. A partyless eligible lord is valid: the game inserts establish_party before the requested sequence. Never target the human main party. Step objectives must be exactly one of: move, hold_position, timed_hold, patrol, scout_report, escort, recruit_resupply, form_army, join_army, leave_army, disband_army, raid, besiege_capture, defend, relieve_siege, hunt_enemy_parties, engage_party, withdraw, return_home. If recruitment is requested, capture minimumTroops or minimumInfantry/minimumArchers/minimumCavalry; if absent, leave it absent so the NPC asks. Distinguish personal party from army: headcount or troop composition means party; explicit army/call lords/gather parties means army; vague forces remains unresolved. Army size is never guaranteed: form_army means the best native eligible army, and the engine waits for native gathering. Preserve geographic language in region and never invent IDs. Detaching a subordinate army member requires armyDetachmentAccepted=true only when that commander explicitly agrees.
";
                case "revise_campaign_order":
                case "respond_to_order_report":
                case "cancel_campaign_order":
                    return @"action_rules: Reference only an orderId supplied by campaign-command context or an urgent report. Never invent an order or report identifier. A report response must reduce to continue, adapt, hold, withdraw, or cancel.
";
                case "accept_temporary_party_guest":
                case "renew_temporary_party_guest":
                    return @"action_rules: Use only for the current lord, lady or encountered resident's own explicit agreement to travel temporarily in the player's party. Their clan and kingdom do not change. A resident soldier or guard must first receive authorized duty release. terms.purpose states the narrative purpose. terms.termKind describes only the schedule and must be exactly open_ended or fixed; never use temporary_party_guest or a social role such as independent ally, companion, retainer, or partner as termKind. Any agreement for a stated number of days is fixed and requires durationDays from 1 through 126, preserving fractional seasons using campaignCalendar. Open-ended means there is no end date: set termKind=open_ended, set reviewIntervalDays=30, and omit durationDays. terms.consentConfirmed is true only when the visible NPC reply clearly accepts. If attached to an army, armyExitAccepted separately requires explicit consent. For an agreed resident payment set the exact integer agreedGold. Permanent companion recruitment uses recruit_encountered_resident instead.
";
                case "recruit_encountered_resident":
                    return @"action_rules: Only the current known resident can agree to become the player's permanent companion. Require a present unconditional visible acceptance, agreementKind=permanent, consentConfirmed=true and exact agreedGold, including zero when free. State a negotiated paid price explicitly in denars. Never act on a proposal, refusal, condition, hypothetical, silence or recruitment of a third person. Preserve their body, equipped clothes, name and progression. Native capacity, gold and presence are revalidated. A guard or soldier first needs release_resident_from_duty by the actual source commander or home-owning clan leader. Never substitute join_clan or create a duplicate wanderer.
";
                case "release_resident_from_duty":
                    return @"action_rules: The actual source commander or home-owning clan leader must explicitly release the named resident from duty now. When the player holds that authority and speaks to the resident, actorHeroId and TargetHero are the resident. Otherwise the current NPC commander is actorHeroId and TargetHero is the named known resident. releaseConfirmed=true requires explicit present release, never a conditional promise. This grants release only; a separate explicit agreement recruits the person. Native execution consumes one source troop and rejects unauthorized release.
";
                case "end_temporary_party_guest":
                    return @"action_rules: Use when the current temporary guest explicitly chooses to leave, or both sides explicitly end the trip. This starts return travel and does not modify relationships, traits, reputation, or rumors.
";
                case "acknowledge_own_faction_combat_risk":
                    return @"action_rules: Use only during the mandatory hostility review when the current guest explicitly accepts that fighting their own faction will cause banishment and transfer to the player clan. terms.consentConfirmed must be true. Never infer consent from silence or continued conversation.
";
                case "start_ruling_clan_rebellion":
                    return @"action_rules: This is the player's unilateral declaration to the current ruler. Queue it immediately even if the ruler refuses in the visible reply. actorHeroId/actorClanId are the player; TargetHero is the current ruler. The player starts alone and recruits only after the rebel kingdom exists. NPC outbreaks never use this action.
";
                case "recruit_lord_to_rebellion":
                    return @"action_rules: Use only after the player has declared a rebellion and the current NPC unambiguously agrees to join. The current NPC may be any eligible lord in any realm; their clan transfers for the duration of the rebellion. Refusal, ambiguity, or mere discussion must not queue the action.
";
                case "join_rebellion":
                    return @"action_rules: Use only when the current NPC is the active NPC rebel leader and explicitly accepts the player clan into that rebellion.
";
                case "surrender_rebellion":
                    return @"action_rules: Surrender is unilateral. Queue a player's explicit surrender even if the listener refuses; queue an NPC surrender only when the NPC's visible reply explicitly gives up. Only the named rebel leader or challenged ruler can end the civil war this way.
";
                case "consent_government_reduction":
                    return @"action_rules: Use only when the player is the current kingdom ruler, clearly discloses a planned one-level reduction of the realm's government authority, directly asks the current NPC for consent, and the NPC visibly agrees. actorHeroId is the consenting current NPC and TargetHero is the player ruler. terms.consentConfirmed must be true. A consenting clan leader binds the entire clan; another NPC binds only themself. Refusal, concern, conditions, questions, silence, or hypothetical discussion never authorize this action. A pledge, payment, or deal already supplied and accepted in the current exchange can satisfy an earlier condition: if the NPC explicitly grants consent now and merely warns that the ruler must honor the resulting obligation later, classify the present consent as accepted rather than conditional.
";
                default:
                    return string.Empty;
            }
        }

        private static string ActionFamilyForId(string id)
        {
            switch (id)
            {
                case "create_clan_accord":
                case "cancel_clan_accord":
                    return "regular";
                case "recruit_and_recover":
                case "form_army":
                case "attack_settlement":
                case "capture_settlement_plan":
                    return "strategy";
                case "start_ruling_clan_rebellion":
                case "recruit_lord_to_rebellion":
                case "join_rebellion":
                case "surrender_rebellion":
                case "consent_government_reduction":
                    return "politics";
                case "install_ruling_clan":
                case "marriage_alliance":
                    return "actorKingdomId, actorClanId, targetClanId; execution must resolve and marry a suitable named hero pair";
                case "support_claimant":
                case "encourage_clan_defection":
                case "exile_clan":
                case "restore_exiled_clan":
                case "mediate_clan_dispute":
                    return "politics";
                case "follow":
                case "stop_following":
                case "go_to_settlement":
                case "patrol_around_settlement":
                case "wait_near_settlement":
                case "raid_village":
                case "besiege_settlement":
                case "create_party":
                case "show_the_way":
                case "attack_party":
                case "attack_player_party":
                case "prepare_arrest":
                case "confirm_arrest":
                case "release_arrested_character":
                case "rescind_arrest_accusation":
                case "player_attack_party":
                case "surrender_to_player":
                case "leave_player_alone":
                case "kill_character":
                case "duel_player":
                case "give_gold_to_player":
                case "transfer_gold":
                case "transfer_item":
                case "transfer_workshop":
                case "transfer_prisoner":
                case "trade_package":
                case "hire_player_as_mercenary":
                case "dismiss_player_mercenary":
                case "offer_player_vassalage":
                case "dismiss_player_vassal":
                case "join_clan":
                case "leave_clan":
                case "join_kingdom":
                case "leave_kingdom":
                case "hire_mercenary_clan":
                case "recruit_encountered_resident":
                case "release_resident_from_duty":
                case "accept_temporary_party_guest":
                case "renew_temporary_party_guest":
                case "end_temporary_party_guest":
                case "acknowledge_own_faction_combat_risk":
                    return "regular";
                default:
                    return "diplomacy";
            }
        }

        private static string ActionRequiredIdsForId(string id, string family)
        {
            switch (id)
            {
                case "create_clan_accord": return "actorHeroId (current noble), TargetHero (player), terms.accordType, terms.playerConfirmed and terms.consentConfirmed";
                case "cancel_clan_accord": return "actorHeroId (current noble), TargetHero (player), terms.accordId and terms.playerConfirmed";
                case "demand_settlement_peace":
                case "return_occupied_settlement":
                    return "actorKingdomId, targetKingdomId, targetSettlementId or terms.settlementIds";
                case "demand_surrender_peace":
                    return "actorKingdomId, targetKingdomId, terms.allTargetFortifications or terms.settlementIds";
                case "diplomatic_package":
                    return "actorKingdomId, targetKingdomId, at least one resolved package term: gold, item, prisonerHero, settlementIds, treatyKind, or promiseText";
                case "trade_package":
                    return "FromHero, ToHero, and at least one appraisable term: GoldAmount, Item, Amount, PrisonerHero, TargetSettlement, Workshop, terms.assetClass=naval, or terms.shipCount";
                case "pay_to_join_war":
                    return "actorKingdomId, targetKingdomId, terms.enemyKingdomId";
                case "recruit_and_recover":
                case "form_army":
                case "attack_settlement":
                case "capture_settlement_plan":
                    return "actorHeroId, targetSettlementId";
                case "start_ruling_clan_rebellion":
                case "recruit_lord_to_rebellion":
                case "join_rebellion":
                case "surrender_rebellion":
                    return "actorHeroId, actorKingdomId, actorClanId, and TargetHero when another lord's acceptance is involved";
                case "consent_government_reduction":
                    return "actorHeroId (the consenting current NPC), actorKingdomId, TargetHero (the player ruler), and terms.consentConfirmed; actorClanId is optional and expands consent to the clan only when the actor is its leader";
                case "install_ruling_clan":
                case "marriage_alliance":
                case "support_claimant":
                case "encourage_clan_defection":
                case "exile_clan":
                case "restore_exiled_clan":
                case "mediate_clan_dispute":
                    return "actorKingdomId, actorClanId";
                case "follow":
                case "attack_party":
                    return "actorHeroId plus TargetHero or terms.targetPartyId";
                case "prepare_arrest":
                    return "actorHeroId (player), TargetHero (current NPC), terms.accusation, terms.chargeCategory, terms.severity";
                case "confirm_arrest":
                case "release_arrested_character":
                case "rescind_arrest_accusation":
                case "player_attack_party":
                    return "actorHeroId (player), TargetHero (current NPC), terms.caseId";
                case "issue_campaign_order":
                    return "actorHeroId (current NPC commander), terms.steps (or one terms.objective), and each step's resolved settlement, region, party target, duration, or recruitment goal required by that capability";
                case "revise_campaign_order":
                case "respond_to_order_report":
                case "cancel_campaign_order":
                    return "terms.orderId; respond_to_order_report also requires terms.response";
                case "accept_temporary_party_guest":
                case "renew_temporary_party_guest":
                    return "actorHeroId (the consenting current NPC), terms.purpose, terms.termKind, terms.consentConfirmed, and durationDays when fixed";
                case "recruit_encountered_resident":
                    return "actorHeroId (current resident), terms.agreementKind=permanent, terms.consentConfirmed and exact terms.agreedGold";
                case "release_resident_from_duty":
                    return "actorHeroId (current speaker), TargetHero (known military resident), terms.releaseConfirmed";
                case "end_temporary_party_guest":
                    return "actorHeroId (the current temporary guest)";
                case "acknowledge_own_faction_combat_risk":
                    return "actorHeroId (the current temporary guest) and terms.consentConfirmed";
                case "go_to_settlement":
                case "patrol_around_settlement":
                case "wait_near_settlement":
                case "raid_village":
                case "besiege_settlement":
                    return "actorHeroId, targetSettlementId or TargetSettlement";
                case "stop_following":
                case "attack_player_party":
                case "surrender_to_player":
                case "leave_player_alone":
                    return "actorHeroId";
                case "create_party":
                    return "actorHeroId, optional targetSettlementId spawn point";
                case "show_the_way":
                case "duel_player":
                    return id == "duel_player" ? "actorHeroId for the current one-on-one NPC/opponent; active conversation and mission hook required" : "actorHeroId; scene hook required for execution";
                case "kill_character":
                    return "actorHeroId, TargetHero, optional KillerHero";
                case "give_gold_to_player":
                    return "actorHeroId, GoldAmount";
                case "transfer_gold":
                    return "FromHero, ToHero, GoldAmount";
                case "transfer_item":
                    return "FromHero, ToHero, Item, Amount";
                case "transfer_workshop":
                    return "Workshop plus ToHero or TargetHero";
                case "transfer_prisoner":
                    return "PrisonerHero, optional FromHero and ToHero";
                case "hire_player_as_mercenary":
                case "offer_player_vassalage":
                    return "targetKingdomId or actorKingdomId";
                case "dismiss_player_mercenary":
                case "dismiss_player_vassal":
                    return "player clan must currently hold that status";
                case "join_clan":
                case "leave_clan":
                    return "actorHeroId, targetClanId when relevant";
                case "join_kingdom":
                    return "actorClanId or actorHeroId, targetKingdomId";
                case "leave_kingdom":
                    return "actorClanId or actorHeroId";
                case "hire_mercenary_clan":
                    return "targetClanId or actorClanId, targetKingdomId or actorKingdomId";
                default:
                    return family == "diplomacy" ? "actorKingdomId, targetKingdomId" : "known game IDs required by the action catalog";
            }
        }

        private static string ActionPurposeForId(string id)
        {
            switch (id)
            {
                case "create_clan_accord": return "Commit the mutually accepted fixed clan benefit and preserve the named arrangers; no kingdom treaty, spy reports or vote support.";
                case "cancel_clan_accord": return "Cancel the player's named active clan accord, removing benefits and recording responsibility and the cancellation penalty.";
                case "sign_trade_agreement": return "Create a trade agreement between two kingdoms, using the native trade-agreement behavior when available and Bannerlord Reign's treaty ledger for memory.";
                case "sign_non_aggression_pact": return "Record a timed promise that two kingdoms will not attack one another, giving future diplomacy a betrayal or trust hook.";
                case "sign_alliance": return "Create a formal alliance between two kingdoms, using the native alliance behavior when available and the treaty ledger for continuity.";
                case "sign_defensive_pact": return "Record a defensive pact where kingdoms promise support if one is attacked.";
                case "break_treaty": return "Break one or more existing treaty records, including native trade agreements or alliances when applicable.";
                case "exchange_prisoners": return "Release noble prisoners held by both sides as a diplomatic exchange.";
                case "ransom_package": return "Trade gold for noble prisoner release as a negotiated ransom package.";
                case "hostage_guarantee": return "Record a private or public hostage-style guarantee that should influence later trust and fear.";
                case "war_indemnity": return "Transfer gold from a defeated or pressured kingdom as punishment or compensation.";
                case "recognize_conquest": return "Record formal recognition of a conquest claim or border settlement.";
                case "return_occupied_settlement": return "Return a known occupied town or castle to another kingdom.";
                case "demilitarized_border": return "Record a border restraint agreement that should shape future war pressure and accusations.";
                case "caravan_protection_agreement": return "Record a timed agreement requiring both kingdoms to protect and respect one another's caravans.";
                case "supply_agreement": return "Record or fund a supply agreement involving food, arms, horses, ships/naval assets, or war support.";
                case "loan_or_subsidy": return "Transfer gold as a loan, subsidy, or political backing.";
                case "pay_to_stay_neutral": return "Pay a kingdom to avoid joining a war or conflict.";
                case "pay_to_join_war": return "Pay a kingdom to declare war on a known third kingdom.";
                case "guarantee_independence": return "Record a guarantee that one kingdom will defend another's independence.";
                case "protectorate_or_vassalage": return "Record a protectorate or vassalage arrangement for a weaker power.";
                case "diplomatic_package": return "Validate and record a compound bargain such as gold for prisoners, prisoners for peace, gold for settlement surrender, resources for alliance, or tribute for neutrality.";
                case "trade_package": return "Appraise, validate, and execute an atomic buy/sell/barter package using game-side prices for gold, items, equipped gear, prisoners, workshops, settlements, and ships.";
                case "marriage_alliance": return "Complete a real marriage between a suitable named hero from each clan; never report success for an abstract understanding.";
                case "start_ruling_clan_rebellion": return "Execute the player's unilateral declaration against their current ruler and immediately create a leadership-bound civil war.";
                case "recruit_lord_to_rebellion": return "Move an accepting lord's clan from any realm into the player's active rebel kingdom until the rebellion ends.";
                case "join_rebellion": return "Move the player clan into an accepting NPC rebel leader's active rebellion.";
                case "surrender_rebellion": return "End an active rebellion when its named rebel leader or challenged ruler explicitly surrenders.";
                case "consent_government_reduction": return "Persist the current NPC's explicit, level-specific immunity from the ruler's next forced government reduction; a clan leader's consent covers their clan.";
                case "support_claimant": return "Record hidden or public support for a claimant against a ruling clan.";
                case "encourage_clan_defection": return "Move a clan from its current kingdom into the destination kingdom through defection.";
                case "exile_clan": return "Force a clan out of its current kingdom and remember the exile.";
                case "restore_exiled_clan": return "Restore an independent or exiled clan into a kingdom.";
                case "mediate_clan_dispute": return "Record a mediated settlement between rival clans inside a kingdom.";
                case "follow": return "Follow the resolved target in the current scene when present; otherwise follow or escort the resolved party on the campaign map.";
                case "stop_following": return "Stop the actor's current scene follow or campaign-map escort behavior.";
                case "go_to_settlement": return "Order a hero party to travel to a named town, castle, village, hideout, or current settlement.";
                case "patrol_around_settlement": return "Order a hero party to patrol around a named settlement.";
                case "wait_near_settlement": return "Order a hero party to wait near a named settlement.";
                case "raid_village": return "Order a hero party to raid a specific village.";
                case "besiege_settlement": return "Order a hero party or army leader to besiege a town or castle.";
                case "create_party": return "Create a lord party for a hero who currently has none, using a target or home settlement as the spawn point.";
                case "show_the_way": return "Record an intent to guide the player; mission/menu hooks execute it later.";
                case "attack_party": return "Order a hero party to attack a target mobile party.";
                case "attack_player_party": return "Order a hero party to attack the player's party.";
                case "surrender_to_player": return "Mark a party as willing to surrender to the player and stop its movement.";
                case "leave_player_alone": return "Stop a party from pursuing or threatening the player.";
                case "kill_character": return "Execute or kill a target hero through the native character death action.";
                case "duel_player": return "Arm a conversation-born practice duel (private, nonlethal, no reputation effects), nonlethal duel of honor, or duel to the death with the current one-on-one NPC.";
                case "prepare_arrest": return "Record the player's stated accusation and the current NPC's reaction while guards move into position, without beginning custody.";
                case "confirm_arrest": return "Confirm a prepared arrest on a later player turn and route custody to the correct dungeon, player party, surrender, duel, or battle path.";
                case "release_arrested_character": return "Release a player-arrested character while preserving the accusation and historical record.";
                case "rescind_arrest_accusation": return "Deactivate the player's accusation reputation, clear the accused's name, and release custody when that case remains its basis.";
                case "player_attack_party": return "Order the player's men to attack a refusing party leader through Bannerlord's native hostile encounter path.";
                case "give_gold_to_player": return "Transfer denars from an actor hero to the player.";
                case "transfer_gold": return "Transfer denars between two resolved heroes.";
                case "transfer_item": return "Transfer an item stack between two resolved hero parties.";
                case "transfer_workshop": return "Transfer ownership of a resolved workshop to a target hero.";
                case "transfer_prisoner": return "Release or transfer a resolved prisoner hero by agreement.";
                case "hire_player_as_mercenary": return "Put the player's clan into mercenary service for a target kingdom.";
                case "dismiss_player_mercenary": return "End the player's mercenary service.";
                case "offer_player_vassalage": return "Put the player's clan into vassalage under a target kingdom.";
                case "dismiss_player_vassal": return "Release the player's clan from regular vassalage.";
                case "join_clan": return "Move an eligible living hero into the resolved target clan after native validation.";
                case "leave_clan": return "Remove an eligible living hero from their current clan after native validation.";
                case "join_kingdom": return "Move a resolved clan into a target kingdom.";
                case "leave_kingdom": return "Move a resolved clan out of its current kingdom.";
                case "hire_mercenary_clan": return "Put a resolved clan into mercenary service for a target kingdom.";
                default: return "Execute or record the named Bannerlord Reign world action when validation confirms the supplied IDs and terms.";
            }
        }

        private static string EmbeddedPromptDefault(string name, string fallback)
        {
            string resourceName = "ReignBetaServer.PromptDefaults." + Path.GetFileName(name ?? string.Empty);
            using (Stream stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return fallback ?? string.Empty;
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
}
