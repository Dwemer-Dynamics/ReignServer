using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Actions
{
    public static partial class ReignActionCatalog
    {
        // An explicit schema for every legacy adapter. Descriptive catalog prose never
        // decides required fields, selector types, consent, or executable parameters.
        private static List<ActionParameter> ExplicitParameters(string id)
        {
            string fields;
            switch (id)
            {
                case "declare_war": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "back_rebellion": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.rebelKingdomId:kingdom terms.movementId:rebellion"; break;
                case "resolve_civil_war": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.movementId:rebellion"; break;
                case "make_peace": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "offer_tribute_peace": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.dailyTribute:money terms.durationDays:number"; break;
                case "record_promise": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "demand_reparations_peace": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.reparationsGold:money terms.dailyTribute:money? terms.durationDays:number?"; break;
                case "demand_settlement_peace": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom targetSettlementId:settlement"; break;
                case "demand_surrender_peace": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.allTargetFortifications:bool targetSettlementId:settlement? terms.reparationsGold:money? terms.dailyTribute:money?"; break;
                case "sign_trade_agreement": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "sign_non_aggression_pact": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "sign_alliance": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "sign_defensive_pact": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "break_treaty": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "exchange_prisoners": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "ransom_package": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.gold:quantity"; break;
                case "hostage_guarantee": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "war_indemnity": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.gold:quantity"; break;
                case "recognize_conquest": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom"; break;
                case "return_occupied_settlement": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom targetSettlementId:settlement"; break;
                case "demilitarized_border": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "caravan_protection_agreement": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "supply_agreement": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.asset:inventoryItem terms.amount:quantity"; break;
                case "loan_or_subsidy": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.gold:quantity"; break;
                case "pay_to_stay_neutral": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.gold:quantity terms.durationDays:number"; break;
                case "pay_to_join_war": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.enemyKingdomId:kingdom terms.gold:quantity"; break;
                case "guarantee_independence": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "protectorate_or_vassalage": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.durationDays:number"; break;
                case "diplomatic_package": fields = "actorKingdomId:kingdom? targetKingdomId:kingdom terms.gold:money? terms.item:inventoryItem? terms.amount:quantity? terms.prisonerHero:hero? targetSettlementId:settlement?"; break;
                case "trade_package": fields = "GoldAmount:money? Item:inventoryItem? Amount:quantity? PrisonerHero:hero? TargetSettlement:settlement? Workshop:workshop? terms.assetClass:text? terms.shipCount:quantity?"; break;
                case "follow": fields = "TargetHero:hero? targetPartyId:party?"; break;
                case "stop_following": fields = ""; break;
                case "go_to_settlement": fields = "targetSettlementId:settlement"; break;
                case "patrol_around_settlement": fields = "targetSettlementId:settlement"; break;
                case "wait_near_settlement": fields = "targetSettlementId:settlement"; break;
                case "raid_village": fields = "targetSettlementId:settlement"; break;
                case "besiege_settlement": fields = "targetSettlementId:settlement"; break;
                case "create_party": fields = "targetSettlementId:settlement"; break;
                case "show_the_way": fields = "terms.destinationLocationId:sceneLocation"; break;
                case "attack_party": fields = "targetPartyId:party"; break;
                case "attack_player_party": fields = ""; break;
                case "surrender_to_player": fields = ""; break;
                case "leave_player_alone": fields = ""; break;
                case "kill_character": fields = "TargetHero:hero"; break;
                case "duel_player": fields = "terms.duelMode:text"; break;
                case "prepare_arrest": fields = "TargetHero:hero terms.accusation:text terms.chargeCategory:text terms.severity:text"; break;
                case "confirm_arrest": fields = "TargetHero:hero terms.caseId:arrestCase"; break;
                case "release_arrested_character": fields = "TargetHero:hero terms.caseId:arrestCase"; break;
                case "rescind_arrest_accusation": fields = "TargetHero:hero terms.caseId:arrestCase"; break;
                case "player_attack_party": fields = "TargetHero:hero terms.caseId:arrestCase"; break;
                case "issue_campaign_order": fields = "terms.objective:text targetSettlementId:settlement? terms.targetPartyId:party? terms.targetHeroStringId:hero? terms.region:region? terms.durationHours:number? terms.minimumTroops:quantity? terms.minimumFoodDays:number? terms.positionX:number? terms.positionY:number? terms.armyDetachmentAccepted:bool?"; break;
                case "revise_campaign_order": fields = "terms.orderId:campaignOrder terms.objective:text? terms.region:region? targetSettlementId:settlement? terms.durationHours:number?"; break;
                case "respond_to_order_report": fields = "terms.orderId:campaignOrder terms.response:text"; break;
                case "cancel_campaign_order": fields = "terms.orderId:campaignOrder"; break;
                case "recruit_encountered_resident": fields = "terms.agreedGold:money"; break;
                case "create_clan_accord": fields = "terms.accordType:text"; break;
                case "cancel_clan_accord": fields = "terms.accordId:clanAccord"; break;
                case "release_resident_from_duty": fields = "TargetHero:hero"; break;
                case "accept_temporary_party_guest": fields = "terms.purpose:text? terms.termKind:text terms.durationDays:number? terms.armyExitAccepted:bool? terms.agreedGold:money? terms.wageGold:money? terms.wagePeriodDays:number? terms.wageRecipientHeroStringId:hero?"; break;
                case "renew_temporary_party_guest": fields = "terms.purpose:text? terms.termKind:text terms.durationDays:number? terms.armyExitAccepted:bool? terms.agreedGold:money? terms.wageGold:money? terms.wagePeriodDays:number? terms.wageRecipientHeroStringId:hero?"; break;
                case "end_temporary_party_guest": fields = "terms.departureReason:text?"; break;
                case "acknowledge_own_faction_combat_risk": fields = ""; break;
                case "give_gold_to_player": fields = "GoldAmount:quantity"; break;
                case "transfer_gold": fields = "GoldAmount:quantity"; break;
                case "transfer_item": fields = "Item:inventoryItem Amount:quantity"; break;
                case "transfer_workshop": fields = "terms.workshopId:workshop"; break;
                case "transfer_prisoner": fields = "terms.prisonerHeroStringId:hero"; break;
                case "hire_player_as_mercenary": fields = "targetKingdomId:kingdom terms.minimumStayDays:number? terms.awardMultiplier:quantity?"; break;
                case "dismiss_player_mercenary": fields = "targetKingdomId:kingdom?"; break;
                case "offer_player_vassalage": fields = "targetKingdomId:kingdom terms.minimumStayDays:number? terms.awardMultiplier:quantity?"; break;
                case "dismiss_player_vassal": fields = "targetKingdomId:kingdom?"; break;
                case "join_clan": fields = "targetClanId:clan"; break;
                case "leave_clan": fields = ""; break;
                case "join_kingdom": fields = "actorClanId:clan? targetKingdomId:kingdom terms.minimumStayDays:number? terms.awardMultiplier:quantity?"; break;
                case "leave_kingdom": fields = "actorClanId:clan?"; break;
                case "hire_mercenary_clan": fields = "actorClanId:clan? targetKingdomId:kingdom terms.minimumStayDays:number? terms.awardMultiplier:quantity?"; break;
                case "recruit_and_recover": fields = "targetSettlementId:settlement? minimumTroops:quantity"; break;
                case "form_army": fields = "targetSettlementId:settlement desiredStrength:quantity?"; break;
                case "attack_settlement": fields = "targetSettlementId:settlement"; break;
                case "capture_settlement_plan": fields = "targetSettlementId:settlement minimumTroops:quantity? desiredStrength:quantity?"; break;
                case "start_ruling_clan_rebellion": fields = "actorKingdomId:kingdom actorClanId:clan"; break;
                case "pledge_rebellion_support": fields = "actorKingdomId:kingdom terms.challengedRulerId:hero"; break;
                case "recruit_lord_to_rebellion": fields = "actorKingdomId:kingdom? actorClanId:clan? TargetHero:hero targetClanId:clan?"; break;
                case "join_rebellion": fields = "actorKingdomId:kingdom? actorClanId:clan? TargetHero:hero"; break;
                case "surrender_rebellion": fields = "actorKingdomId:kingdom? actorClanId:clan? terms.movementId:rebellion?"; break;
                case "resolve_rebellion_pledge": fields = "actorKingdomId:kingdom? actorClanId:clan? targetHeroId:hero targetClanId:clan? terms.decision:text"; break;
                case "resolve_rebellion_summons": fields = "actorKingdomId:kingdom? actorClanId:clan? targetHeroId:hero terms.playerResponse:text terms.rulerVerdict:text"; break;
                case "consent_government_reduction": fields = "actorKingdomId:kingdom?"; break;
                case "install_ruling_clan": fields = "actorKingdomId:kingdom? actorClanId:clan"; break;
                case "marriage_alliance": fields = "terms.marriageHero1StringId:hero? terms.marriageHero2StringId:hero?"; break;
                case "support_claimant": fields = "actorKingdomId:kingdom? actorClanId:clan"; break;
                case "encourage_clan_defection": fields = "actorKingdomId:kingdom? actorClanId:clan"; break;
                case "exile_clan": fields = "actorKingdomId:kingdom? actorClanId:clan"; break;
                case "restore_exiled_clan": fields = "actorKingdomId:kingdom? actorClanId:clan"; break;
                case "mediate_clan_dispute": fields = "actorKingdomId:kingdom? actorClanId:clan? targetClanId:clan"; break;
                default: throw new InvalidOperationException("Missing explicit action schema: " + id);
            }
            var parameters = Fields(fields);
            if (id == "renew_temporary_party_guest" || id == "end_temporary_party_guest")
            {
                var bindings = Fields("terms.agreementId:text? terms.expectedReviewDueDay:number? terms.expectedAgreementRevision:integer?");
                foreach (var binding in bindings)
                {
                    binding.Automatic = true;
                    if (binding.Kind == "number") binding.Maximum = double.MaxValue;
                    if (binding.Kind == "integer") { binding.Minimum = 0; binding.Maximum = int.MaxValue - 1; }
                }
                parameters.AddRange(bindings);
            }
            foreach (var p in parameters)
            {
                if (p.Key == "terms.accordType") p.Choices = new[] { "Trade", "MutualWatch", "Agricultural", "Artisan", "Garrison" };
                if (p.Key == "terms.termKind") p.Choices = new[] { "fixed", "open_ended" };
                if (p.Key == "terms.duelMode") p.Choices = new[] { "practice", "honor", "lethal" };
                if (p.Key == "terms.response") p.Choices = new[] { "continue", "adapt", "hold", "withdraw", "cancel" };
                if (p.Key == "terms.objective") p.Choices = CampaignObjectives;
                if (p.Key == "terms.durationDays" && (id == "accept_temporary_party_guest" || id == "renew_temporary_party_guest"))
                { p.Minimum = 1; p.Maximum = 126; }
                if (p.Key == "terms.severity") p.Choices = new[] { "minor", "serious", "grave", "capital" };
            }
            return parameters;
        }
        private static List<ActionParameter> Fields(string fields) => fields.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(field =>
        {
            var parts = field.Split(':'); string kind = parts[1].TrimEnd('?'); bool required = !parts[1].EndsWith("?", StringComparison.Ordinal);
            var p = P(parts[0], ParameterLabel(parts[0]), kind, required);
            if (kind == "bool") p.Choices = new[] { "false", "true" };
            if (kind == "number" && !parts[0].EndsWith("positionX", StringComparison.Ordinal) && !parts[0].EndsWith("positionY", StringComparison.Ordinal))
            { p.Minimum = 0.001; p.Maximum = 87600; }
            return p;
        }).ToList();

        private static string ParameterLabel(string key)
        {
            switch (key)
            {
                case "actorKingdomId": return "Acting kingdom";
                case "targetKingdomId": case "TargetKingdom": return "Kingdom";
                case "actorClanId": return "Affected clan";
                case "targetClanId": case "TargetClan": return "Clan";
                case "targetHeroId": case "TargetHero": return "Character";
                case "targetSettlementId": case "TargetSettlement": return "Destination";
                case "terms.durationDays": return "Duration in days";
                case "terms.durationHours": return "Duration in hours";
                case "terms.agreedGold": case "GoldAmount": return "Denars";
                case "maxCost": return "Maximum operation cost in denars";
                case "socialItemId": return "Rumor or reputation";
                case "fabricatedText": return "Statement to circulate";
                case "terms.dailyTribute": return "Daily tribute in denars";
                case "terms.armyExitAccepted": case "terms.armyDetachmentAccepted": return "May leave the current army";
                case "terms.termKind": return "Length of stay";
            }
            string label = Regex.Replace(key.Replace("terms.", ""), "(StringId|Id)$", "");
            label = Regex.Replace(label, "([a-z])([A-Z])", "$1 $2");
            return label.Length == 0 ? key : char.ToUpperInvariant(label[0]) + label.Substring(1).ToLowerInvariant();
        }

        public static string[] CampaignObjectives => new[] { "establish_party", "move", "hold_position", "timed_hold", "patrol", "scout_report", "escort", "recruit_resupply", "form_army", "join_army", "leave_army", "disband_army", "raid", "besiege_capture", "defend", "relieve_siege", "hunt_enemy_parties", "engage_party", "withdraw", "return_home" };

        private static void AddGovernmentActions(List<ActionDefinition> actions)
        {
            void Add(string id, string label, string fields, string description)
            {
                actions.Add(new ActionDefinition { Id = "government." + id, Family = "government", Label = label,
                    Adapter = "government:" + id, Source = "ReignGovernmentActionAgreements", Description = description,
                    Parameters = Fields(fields) });
            }
            Add("recommend", "Recommend an outcome", "businessId:governmentBusiness optionId:governmentOption", "Record the ruler's exact recommendation without replacing members' votes.");
            Add("introduce_proposal", "Introduce a government proposal", "kingdomId:kingdom proposalId:governmentProposal", "Introduce the exact policy, fief review or clan-expulsion hearing; it still follows the government's actual voting rules.");
            Add("contribute_goods", "Supply a government resolution", "resolutionId:governmentResolution quantity:quantity", "Deliver the exact number of eligible goods from the acting party to an active government resolution.");
            Add("adopt_petition", "Sponsor a petition", "businessId:governmentBusiness", "Place a pending petition before the government.");
            Add("postpone", "Postpone a hearing", "businessId:governmentBusiness", "Use the hearing's one permitted seven-day recess.");
            Add("reconvene", "Reconvene a hearing", "businessId:governmentBusiness", "Reopen a postponed hearing.");
            Add("call_vote", "Call a government vote", "businessId:governmentBusiness", "Record the government's real individual ballots under its current authority rules.");
            Add("resolve", "Decide a government hearing", "businessId:governmentBusiness optionId:governmentOption overrideOpposition:bool", "Apply the exact selected outcome after the required vote and reconsideration period.");
            Add("increase_authority", "Grant more governing authority", "kingdomId:kingdom", "Increase governing authority by one level with the native Reign consequences.");
            Add("reduce_authority", "Reduce governing authority", "kingdomId:kingdom force:bool", "Request a one-level reduction, with a forced reduction only when explicitly selected.");
            Add("fulfill_resolution", "Fulfill a government resolution", "resolutionId:governmentResolution routeId:governmentRoute", "Accept one of the resolution's exact routes; payment waits for recorded completion of its objective.");
            Add("decline_resolution", "Decline a government resolution", "resolutionId:governmentResolution", "Decline a pending resolution under the government's current decision rules.");
        }

        private static void ConfigureSupervisedAction(ActionDefinition definition)
        {
            switch (definition.Id)
            {
                case "go_to_settlement": definition.NativeObjective = "move"; break;
                case "patrol_around_settlement": definition.NativeObjective = "patrol"; break;
                case "wait_near_settlement": definition.NativeObjective = "timed_hold"; break;
                case "raid_village": definition.NativeObjective = "raid"; break;
                case "besiege_settlement": case "capture_settlement_plan": case "attack_settlement": definition.NativeObjective = "besiege_capture"; break;
                case "create_party": definition.NativeObjective = "establish_party"; break;
                case "attack_party": definition.NativeObjective = "engage_party"; break;
                case "recruit_and_recover": definition.NativeObjective = "recruit_resupply"; break;
                case "form_army": definition.NativeObjective = "form_army"; break;
            }
            if (definition.NativeObjective.Length == 0) return;
            definition.MovesActor = true;
            if (definition.NativeObjective == "patrol" || definition.NativeObjective == "timed_hold")
            {
                definition.Parameters.AddRange(Fields("terms.durationHours:number"));
                definition.DurationParameter = "terms.durationHours";
            }
        }

        private static void AddCourtActions(List<ActionDefinition> actions)
        {
            void Add(string id, string label, string fields, string description) => actions.Add(new ActionDefinition
            { Id = id, Label = label, Family = "court", Adapter = "court:" + id, Source = "ReignCourtActionAgreements", Description = description, Parameters = Fields(fields) });
            Add("court.serve_office", "Serve as a court officer", "office:courtOffice", "The consenting NPC takes the selected office in the player's court through its server-validated appointment service.");
            Add("court.leave_office", "Leave your court office", "", "Dismiss the consenting NPC's current court office through the existing service.");
            Add("court.serve_regent", "Serve as regent", "", "Appoint the consenting NPC as the player's regent under existing court eligibility rules.");
            Add("court.leave_regent", "Relinquish the regency", "", "Dismiss the consenting current regent.");
            Add("court.serve_ambassador", "Serve as ambassador", "targetKingdomId:kingdom missionType:ambassadorMission", "Post the consenting NPC to the selected realm; fulfillment waits for the posting's actual arrival.");
            Add("court.recall_ambassador", "Return from your ambassador posting", "", "Recall the consenting NPC's active ambassador posting.");
            Add("court.set_ambassador_mission", "Change your ambassador mission", "missionType:ambassadorMission", "Set public reporting or relationship-building on the consenting NPC's existing posting.");
            Add("court.serve_chancellor", "Serve as Chancellor", "activeDailySalary:money", "Appoint the consenting eligible NPC as the player's Chancellor, initially inactive, at the exact salary for each active day.");
            Add("court.leave_chancellor", "Relinquish the Chancellorship", "", "Dismiss the consenting current Chancellor through the native office lifecycle.");
            Add("court.set_chancellor_active", "Change your Chancellor duties", "active:bool", "Schedule the consenting current Chancellor's active or inactive duties for the next day. Emergency and handoff coverage cannot be overridden.");
            foreach (string spec in new[] {
                "land_intelligence|Gather settlement intelligence", "counterintelligence|Conduct a counterintelligence sweep",
                "person_skills|Investigate a person's skills", "person_relationships|Investigate a person's relationships", "person_rumors|Investigate rumors about a person",
                "disrupt_food|Disrupt a settlement's food supply", "disrupt_construction|Disrupt construction", "disrupt_security|Disrupt security", "disrupt_loyalty|Disrupt loyalty",
                "assassinate_governor|Attempt to assassinate a governor", "assassinate_person|Attempt to assassinate a person",
                "mitigate_own_rumor|Mitigate a rumor about me", "mitigate_own_reputation|Mitigate my reputation", "mitigate_target_rumor|Mitigate a rumor about a person", "mitigate_target_reputation|Mitigate a person's reputation",
                "fabricate_positive|Promote a positive reputation", "fabricate_moderate|Circulate a harmful rumor", "fabricate_severe|Circulate a severe reputation" })
            {
                string[] parts = spec.Split('|'); string id = parts[0];
                bool land = id == "land_intelligence" || id == "counterintelligence" || id.StartsWith("disrupt_", StringComparison.Ordinal) || id == "assassinate_governor";
                string fields = (land ? "targetSettlementId:settlement" : "targetHeroId:hero") + " maxCost:money";
                if (id.StartsWith("mitigate_", StringComparison.Ordinal)) fields += " socialItemId:spySocialItem";
                if (id.StartsWith("fabricate_", StringComparison.Ordinal)) fields += " fabricatedText:text";
                Add("spymaster." + id, parts[1], fields, "The player's appointed Spymaster undertakes this existing intelligence mission. Its normal treasury cost, duration, success and detection risks apply. Contingent bargain payment requires recorded success, not dispatch.");
            }
            foreach (var d in actions.Where(d => d.Family == "court")) foreach (var p in d.Parameters)
            {
                if (p.Kind == "courtOffice") p.Choices = new[] { "Steward", "EconomicAdvisor", "ForeignAdvisor", "Marshal", "Spymaster" };
                if (p.Kind == "ambassadorMission") p.Choices = new[] { "public_information", "improve_relations" };
            }
        }

        private static void AddCampaignActions(List<ActionDefinition> actions)
        {
            foreach (string objective in CampaignObjectives)
            {
                var d = new ActionDefinition { Id = "campaign." + objective, Family = "campaign", Label = char.ToUpperInvariant(objective[0]) + objective.Substring(1).Replace('_', ' '),
                    Adapter = "RegularIssueCampaignOrder", NativeObjective = objective, MovesActor = true,
                    Source = "ReignCampaignCommandCapabilityRegistry." + objective, Description = "Perform the supervised " + objective.Replace('_', ' ') + " objective and observe its actual completion." };
                bool settlement = new[] { "move", "scout_report", "form_army", "raid", "besiege_capture", "defend", "relieve_siege", "patrol" }.Contains(objective);
                if (settlement) d.Parameters.AddRange(Fields("targetSettlementId:settlement"));
                else if (new[] { "establish_party", "recruit_resupply", "withdraw", "return_home", "timed_hold" }.Contains(objective)) d.Parameters.AddRange(Fields("targetSettlementId:settlement?"));
                if (new[] { "escort", "join_army", "engage_party" }.Contains(objective)) d.Parameters.AddRange(Fields("terms.targetPartyId:party"));
                if (objective == "hold_position") d.Parameters.AddRange(Fields("terms.positionX:number terms.positionY:number"));
                if (objective == "leave_army") d.Parameters.AddRange(Fields("terms.armyDetachmentAccepted:bool"));
                if (new[] { "timed_hold", "patrol", "scout_report", "escort", "defend", "hunt_enemy_parties" }.Contains(objective))
                { d.Parameters.AddRange(Fields("terms.durationHours:number")); d.DurationParameter = "terms.durationHours"; }
                if (objective == "recruit_resupply") d.Parameters.AddRange(Fields("terms.minimumTroops:quantity terms.minimumFoodDays:number?"));
                actions.Add(d);
            }
        }
    }
}
