using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    public enum ReignGovernmentResolutionAction
    {
        TreasuryPayment,
        FoodDelivery,
        GoodsDelivery,
        LoyaltyTarget,
        ProsperityTarget,
        HearthTarget,
        SecurityTarget,
        GarrisonMinimum,
        GarrisonMaximum,
        RecruitTroops,
        FieldArmyMinimum,
        PatrolDays,
        GuardCaravansDays,
        DefeatBanditParties,
        HuntRaiderLord,
        MakePeace,
        DeclareWar,
        WinEnemyEngagements,
        EndVillageRaidsDays,
        SpymasterInvestigation,
        SpymasterCounterintelligence,
        IdentifyCulprit,
        CaptureCulprit,
        ImproveForeignRelation,
        SignTradeAgreement,
        SignNonAggressionPact,
        ExchangePrisoners,
        ReleasePrisoners,
        RansomPrisoners,
        CompensateClan,
        ImproveClanRelation,
        GrantFief,
        ReturnFief,
        ReplaceGovernor,
        EnactPolicy,
        RepealPolicy,
        CompleteConstruction,
        ChangeConstruction,
        HaltConstructionDays,
        TaxReliefDays,
        ReduceTariffsDays,
        CollectTaxMinimum,
        WaiveObligationDays,
        SupplyArmy,
        HoldFeast,
        CreateMilitia,
        ResolveIssueCount
    }

    public enum ReignGovernmentResolutionUnit
    {
        Gold,
        Goods,
        Loyalty,
        Prosperity,
        Hearth,
        Security,
        Troops,
        Days,
        Parties,
        Engagements,
        Relations,
        Actions,
        Issues
    }

    public sealed class ReignGovernmentResolutionRoute
    {
        public ReignGovernmentResolutionRoute(
            ReignGovernmentResolutionAction action,
            string description,
            int target,
            ReignGovernmentResolutionUnit unit,
            int deadlineDays)
        {
            Action = action;
            Description = description ?? string.Empty;
            Target = target;
            Unit = unit;
            DeadlineDays = deadlineDays;
        }

        public ReignGovernmentResolutionAction Action { get; }
        public string Description { get; }
        public int Target { get; }
        public ReignGovernmentResolutionUnit Unit { get; }
        public int DeadlineDays { get; }
    }

    public sealed class ReignGovernmentResolutionTemplate
    {
        public ReignGovernmentResolutionTemplate(
            string id,
            string category,
            string title,
            string evidenceTag,
            ReignGovernmentResolutionScale scale,
            IReadOnlyList<ReignGovernmentPlank> supportingPlanks,
            ReignGovernmentResolutionRoute firstRoute,
            ReignGovernmentResolutionRoute secondRoute)
        {
            Id = id ?? string.Empty;
            Category = category ?? string.Empty;
            Title = title ?? string.Empty;
            EvidenceTag = evidenceTag ?? string.Empty;
            Scale = scale;
            SupportingPlanks = supportingPlanks ?? Array.Empty<ReignGovernmentPlank>();
            FirstRoute = firstRoute ?? throw new ArgumentNullException(nameof(firstRoute));
            SecondRoute = secondRoute ?? throw new ArgumentNullException(nameof(secondRoute));
        }

        public string Id { get; }
        public string Category { get; }
        public string Title { get; }
        public string EvidenceTag { get; }
        public ReignGovernmentResolutionScale Scale { get; }
        public IReadOnlyList<ReignGovernmentPlank> SupportingPlanks { get; }
        public ReignGovernmentResolutionRoute FirstRoute { get; }
        public ReignGovernmentResolutionRoute SecondRoute { get; }
    }

    public readonly struct ReignGovernmentResolutionReward
    {
        public ReignGovernmentResolutionReward(
            int loyalty,
            int prosperity,
            int hearthOrSecurity,
            int speakerRelation,
            int governmentTrust)
        {
            Loyalty = loyalty;
            Prosperity = prosperity;
            HearthOrSecurity = hearthOrSecurity;
            SpeakerRelation = speakerRelation;
            GovernmentTrust = governmentTrust;
        }

        public int Loyalty { get; }
        public int Prosperity { get; }
        public int HearthOrSecurity { get; }
        public int SpeakerRelation { get; }
        public int GovernmentTrust { get; }
    }

    public static class ReignGovernmentResolutionCatalog
    {
        private static readonly string[] Rows =
        {
            "R001|settlement_relief|Emergency relief for a disloyal town|low_loyalty|PopularWelfare,RepresentativeAuthority|Standard|TreasuryPayment|Pay an emergency civic grant|LoyaltyTarget|Restore loyalty through successful local issue work",
            "R002|settlement_relief|Rebuild a town after prolonged hardship|low_prosperity|Infrastructure,Trade,PopularWelfare|Major|TreasuryPayment|Finance reconstruction directly|ProsperityTarget|Restore the town economy to the demanded threshold",
            "R003|settlement_relief|Aid a neglected castle community|low_castle_loyalty|Security,PopularWelfare|Standard|TreasuryPayment|Fund the castle households|LoyaltyTarget|Resolve local needs until loyalty recovers",
            "R004|settlement_relief|Relieve an impoverished village|low_hearth|Agriculture,PopularWelfare|Minor|TreasuryPayment|Give the village a relief grant|HearthTarget|Raise village hearths through protection and recovery",
            "R005|settlement_relief|Repair a recently captured town|recent_capture|Infrastructure,Security|Major|TreasuryPayment|Pay immediate occupation relief|SecurityTarget|Restore public order without a cash grant",
            "R006|settlement_relief|Answer a notable's unresolved petitions|open_issues|Justice,RepresentativeAuthority|Minor|ResolveIssueCount|Complete local notable issues|TreasuryPayment|Fund local agents to settle the petitions",
            "R007|settlement_relief|Restore confidence in a frontier settlement|border_hardship|Security,LocalAutonomy|Standard|SecurityTarget|Raise security to the promised level|GarrisonMinimum|Bolster the garrison to reassure residents",
            "R008|settlement_relief|Hold a realm relief assembly|realmwide_low_loyalty|PopularWelfare,Faith|Major|HoldFeast|Fund a public relief assembly|LoyaltyTarget|Raise loyalty in the affected settlements",

            "R009|food_agriculture|Feed a town facing shortage|low_food|PopularWelfare,Agriculture|Standard|FoodDelivery|Deliver food to the town reserve|TreasuryPayment|Buy emergency food at the hard price",
            "R010|food_agriculture|Replenish a raided village granary|village_raided|Agriculture,PopularWelfare|Minor|FoodDelivery|Give a one-time granary donation|WaiveObligationDays|Require less food and tax from its farmers",
            "R011|food_agriculture|Protect the next harvest|low_hearth|Agriculture,Security|Standard|PatrolDays|Patrol the village lands through harvest|HearthTarget|Restore the village before the deadline",
            "R012|food_agriculture|Supply seed grain after crop failure|crop_failure|Agriculture,PopularWelfare|Minor|FoodDelivery|Deliver seed grain|TreasuryPayment|Pay farmers to acquire seed locally",
            "R013|food_agriculture|Stabilize grain prices|high_food_price|Trade,PopularWelfare|Standard|FoodDelivery|Release grain into the market|ReduceTariffsDays|Suspend food tariffs for the required period",
            "R014|food_agriculture|Provision a hungry garrison|garrison_food_shortage|Security,Agriculture|Standard|FoodDelivery|Deliver military provisions|GarrisonMaximum|Reduce the garrison to a supportable strength",
            "R015|food_agriculture|Restore herds lost to war|livestock_loss|Agriculture,PopularWelfare|Minor|GoodsDelivery|Deliver replacement livestock|TreasuryPayment|Compensate affected households",
            "R016|food_agriculture|Create a strategic grain reserve|war_food_risk|Agriculture,MilitaryStrength|Major|FoodDelivery|Stockpile the required grain|TreasuryPayment|Purchase a contracted reserve",

            "R017|trade|Revive a stagnant town market|low_prosperity|Trade,PopularWelfare|Standard|GoodsDelivery|Seed the market with trade goods|ProsperityTarget|Raise prosperity through ordinary commerce",
            "R018|trade|Protect caravans on a dangerous route|caravan_losses|Trade,Security|Standard|GuardCaravansDays|Escort caravans for the required period|DefeatBanditParties|Destroy the bandit parties threatening trade",
            "R019|trade|Open commerce with a neighboring realm|missing_trade_agreement|Trade,Peace|Major|SignTradeAgreement|Conclude a trade agreement|ProsperityTarget|Grow domestic prosperity instead",
            "R020|trade|Reduce burdens at the town gates|high_tariffs|Trade,RepresentativeAuthority|Minor|ReduceTariffsDays|Reduce tariffs temporarily|TreasuryPayment|Reimburse merchants for the burden",
            "R021|trade|Replace goods destroyed in a raid|raid_trade_loss|Trade,PopularWelfare|Standard|GoodsDelivery|Deliver replacement goods|TreasuryPayment|Pay the merchants' assessed losses",
            "R022|trade|Fund a merchant road|poor_trade_route|Infrastructure,Trade|Major|CompleteConstruction|Complete the linked road or market project|TreasuryPayment|Pay the full infrastructure grant",
            "R023|trade|Settle a caravan guild dispute|merchant_issue|Trade,Justice|Minor|ResolveIssueCount|Resolve the guild issues|ImproveClanRelation|Reconcile the rival commercial patrons",
            "R024|trade|Secure a wartime supply corridor|wartime_trade_loss|Trade,MilitaryStrength|Major|GuardCaravansDays|Guard the supply caravans|SupplyArmy|Personally provision the field army",

            "R025|security|Suppress bandits around a town|bandit_activity|Security,Justice|Standard|DefeatBanditParties|Defeat the nearby bandit parties|SecurityTarget|Raise town security through other lawful measures",
            "R026|security|Restore order after unrest|low_security|Security,RoyalAuthority|Standard|SecurityTarget|Raise security to the demanded threshold|GarrisonMinimum|Increase the local garrison",
            "R027|security|Reduce an oppressive garrison|garrison_excess|RepresentativeAuthority,PopularWelfare|Standard|GarrisonMaximum|Reduce the garrison below the limit|LoyaltyTarget|Prove public confidence by restoring loyalty",
            "R028|security|Create a village watch|repeated_raids|Security,LocalAutonomy|Minor|CreateMilitia|Raise local militia strength|PatrolDays|Assign patrols until danger passes",
            "R029|security|Answer organized crime in a town|criminal_activity|Justice,Security|Standard|SpymasterInvestigation|Order a criminal investigation|SecurityTarget|Raise security by direct administration",
            "R030|security|Protect a threatened notable|threatened_notable|Security,Justice|Minor|PatrolDays|Guard the notable and settlement|IdentifyCulprit|Identify the source of the threat",
            "R031|security|Secure a rebellious district|rebellious_state|Security,RoyalAuthority|Major|GarrisonMinimum|Reinforce the settlement garrison|LoyaltyTarget|Restore loyalty without military expansion",
            "R032|security|End extortion on a trade road|extortion_history|Security,Trade|Standard|DefeatBanditParties|Defeat the extortionists|SpymasterInvestigation|Use the Spymaster to dismantle the network",

            "R033|defense|Bolster a frontier garrison|enemy_border|MilitaryStrength,Security|Standard|GarrisonMinimum|Raise the garrison to the required strength|TreasuryPayment|Pay for allied defensive support",
            "R034|defense|Reduce a ruinously large peacetime garrison|peace_garrison_cost|Trade,PopularWelfare|Standard|GarrisonMaximum|Reduce the garrison to the limit|TreasuryPayment|Pay the extra upkeep from the treasury",
            "R035|defense|Raise recruits for an exposed castle|weak_garrison|MilitaryStrength,Security|Major|RecruitTroops|Add trained troops to the garrison|GarrisonMinimum|Reach the full defensive threshold by transfers",
            "R036|defense|Form an army against invasion|enemy_invasion|MilitaryStrength,RoyalAuthority|Major|FieldArmyMinimum|Assemble an army of the required strength|WinEnemyEngagements|Win the required defensive engagements",
            "R037|defense|Provision the realm's field army|army_supply_shortage|MilitaryStrength,Agriculture|Standard|SupplyArmy|Deliver provisions to the army|TreasuryPayment|Fund contracted army supplies",
            "R038|defense|Fortify a threatened settlement|siege_risk|MilitaryStrength,Infrastructure|Major|CompleteConstruction|Complete the defensive construction|GarrisonMinimum|Reinforce the settlement instead",
            "R039|defense|Protect villages while armies muster|wartime_village_risk|Security,PopularWelfare|Standard|PatrolDays|Maintain defensive patrols|EndVillageRaidsDays|Keep every affected village unraided",
            "R040|defense|Demobilize after peace|postwar_burden|Peace,Trade|Standard|GarrisonMaximum|Reduce oversized garrisons|WaiveObligationDays|Release settlements from wartime obligations",

            "R041|war_peace|Seek peace after costly losses|war_exhaustion|Peace,PopularWelfare|Major|MakePeace|Conclude peace with the named enemy|TreasuryPayment|Fund the demanded defensive recovery while war continues",
            "R042|war_peace|Answer an enemy invasion with war|enemy_attack|MilitaryStrength,Security|Major|DeclareWar|Declare war on the aggressor|SignNonAggressionPact|Secure a binding non-aggression pact",
            "R043|war_peace|Punish a hostile frontier realm|repeated_hostility|Expansion,MilitaryStrength|Major|DeclareWar|Declare the demanded punitive war|ImproveForeignRelation|Repair relations enough to remove the threat",
            "R044|war_peace|Prove progress in a stagnant war|stalled_war|MilitaryStrength,Expansion|Major|WinEnemyEngagements|Win the required engagements|MakePeace|End the unproductive war",
            "R045|war_peace|End simultaneous wars|multiple_wars|Peace,Trade|Major|MakePeace|End one named war|FieldArmyMinimum|Assemble enough strength to sustain both fronts",
            "R046|war_peace|Defend an ally's appeal|ally_under_attack|Justice,MilitaryStrength|Major|DeclareWar|Enter the ally's war|TreasuryPayment|Provide a major subsidy instead",
            "R047|war_peace|Avoid an unaffordable offensive|low_treasury_war|Peace,PopularWelfare|Standard|MakePeace|End the costly offensive|TreasuryPayment|Restore the war treasury to the demanded reserve",
            "R048|war_peace|Recover honor after military defeat|recent_defeat|MilitaryStrength,Justice|Major|WinEnemyEngagements|Win compensating engagements|HoldFeast|Fund a public oath and veteran relief assembly",

            "R049|raids_retribution|Aid a freshly raided village|village_raided|PopularWelfare,Agriculture|Standard|FoodDelivery|Restore the village granary|TreasuryPayment|Pay full relief to the village",
            "R050|raids_retribution|Hunt the lord who razed the village|known_raider_lord|MilitaryStrength,Justice|Major|HuntRaiderLord|Defeat or capture the named raider|CompensateClan|Pay restitution while diplomats pursue the offender",
            "R051|raids_retribution|Expose who ordered a covert raid|suspected_proxy_raid|Espionage,Justice|Standard|SpymasterInvestigation|Investigate the raid's sponsor|IdentifyCulprit|Produce verified proof by any lawful route",
            "R052|raids_retribution|End raids across a vulnerable district|repeated_raids|Security,PopularWelfare|Major|EndVillageRaidsDays|Keep every named village unraided|PatrolDays|Maintain patrol coverage for the full period",
            "R053|raids_retribution|Rebuild homes after raiders depart|raid_hearth_loss|Infrastructure,Agriculture|Standard|HearthTarget|Restore village hearths|GoodsDelivery|Deliver rebuilding materials",
            "R054|raids_retribution|Recover stolen trade wealth|raid_trade_loss|Trade,Justice|Standard|HuntRaiderLord|Defeat the responsible raider|TreasuryPayment|Compensate the affected market",
            "R055|raids_retribution|Prevent reckless retaliation|retaliation_risk|Peace,Justice|Standard|ImproveForeignRelation|Obtain formal foreign redress|SecurityTarget|Restore local security without retaliation",
            "R056|raids_retribution|Escalate unanswered border raids|unanswered_raids|Expansion,MilitaryStrength|Major|DeclareWar|Declare war on the responsible realm|HuntRaiderLord|Defeat the named offender without general war",

            "R057|spymaster|Investigate sabotage in a village|subterfuge_village|Espionage,Security|Standard|SpymasterInvestigation|Order a Spymaster hunt|TreasuryPayment|Fund public recovery and private local inquiry",
            "R058|spymaster|Find the culprit behind poisoned stores|subterfuge_food|Espionage,Justice|Major|IdentifyCulprit|Identify the responsible agent|FoodDelivery|Replace the stores and stabilize the settlement",
            "R059|spymaster|Harden the capital against agents|capital_spy_risk|Espionage,Security|Major|SpymasterCounterintelligence|Complete a counterintelligence operation|SecurityTarget|Raise capital security through overt measures",
            "R060|spymaster|Capture a known hostile operative|known_agent|Espionage,Justice|Standard|CaptureCulprit|Capture the named operative|ImproveForeignRelation|Obtain the operative's recall diplomatically",
            "R061|spymaster|Expose corruption in the tax office|corruption_evidence|Justice,RepresentativeAuthority|Standard|SpymasterInvestigation|Investigate the officials|ReplaceGovernor|Replace the responsible administrator",
            "R062|spymaster|Protect military plans from leaks|war_plan_leak|Espionage,MilitaryStrength|Major|SpymasterCounterintelligence|Stop the intelligence leak|WinEnemyEngagements|Act before the leak causes defeat",
            "R063|spymaster|Answer false rumors harming loyalty|hostile_rumors|Espionage,PopularWelfare|Standard|IdentifyCulprit|Expose the rumor source|LoyaltyTarget|Restore loyalty through transparent relief",
            "R064|spymaster|Audit a suspicious noble household|noble_intrigue|Espionage,NoblePrivilege|Major|SpymasterInvestigation|Conduct a bounded investigation|ImproveClanRelation|Resolve the suspicion through reconciliation",

            "R065|diplomacy|Repair relations with a trading neighbor|foreign_relation_low|Trade,Peace|Standard|ImproveForeignRelation|Reach the required relation|SignTradeAgreement|Bind the relationship through commerce",
            "R066|diplomacy|Secure the frontier without war|border_tension|Peace,Security|Major|SignNonAggressionPact|Conclude a non-aggression pact|ImproveForeignRelation|Raise relations enough to defuse the crisis",
            "R067|diplomacy|Ransom subjects held abroad|foreign_prisoners|PopularWelfare,Justice|Standard|RansomPrisoners|Pay to free the named prisoners|ExchangePrisoners|Arrange a balanced exchange",
            "R068|diplomacy|Compensate a clan harmed by policy|clan_grievance|Justice,NoblePrivilege|Standard|CompensateClan|Pay the assessed compensation|ImproveClanRelation|Repair the relationship through concessions",
            "R069|diplomacy|Open markets after a closed border|closed_border|Trade,Peace|Major|SignTradeAgreement|Open trade formally|ReduceTariffsDays|Unilaterally reduce border tariffs",
            "R070|diplomacy|Answer an ambassador's warning|ambassador_warning|Security,Peace|Standard|ImproveForeignRelation|Satisfy the diplomatic concern|GarrisonMinimum|Prepare the named frontier instead",
            "R071|diplomacy|Mediate between rival clans|clan_conflict|Justice,Peace|Major|ImproveClanRelation|Reconcile both clan leaders|HoldFeast|Host and fund a formal settlement assembly",
            "R072|diplomacy|Win recognition after conquest|unrecognized_conquest|Expansion,Trade|Major|ImproveForeignRelation|Secure foreign recognition|TreasuryPayment|Fund the demanded reparations package",

            "R073|justice_nobles|Redress a lord's confiscated fief|fief_grievance|Justice,NoblePrivilege|Major|ReturnFief|Return the disputed fief|CompensateClan|Pay major compensation instead",
            "R074|justice_nobles|Reward a landless loyal clan|landless_loyal_clan|NoblePrivilege,RoyalAuthority|Major|GrantFief|Grant an available fief|TreasuryPayment|Give the clan a major crown grant",
            "R075|justice_nobles|Remove an abusive governor|governor_abuse|Justice,RepresentativeAuthority|Standard|ReplaceGovernor|Replace the governor|LoyaltyTarget|Restore loyalty enough to prove reform",
            "R076|justice_nobles|Reconcile a slighted clan leader|low_clan_relation|NoblePrivilege,Peace|Standard|ImproveClanRelation|Raise the ruler's relation with the leader|CompensateClan|Pay formal compensation",
            "R077|justice_nobles|Release prisoners held without cause|unjust_prisoners|Justice,PopularWelfare|Standard|ReleasePrisoners|Release the named prisoners|ResolveIssueCount|Complete a formal review of their cases",
            "R078|justice_nobles|Answer demands for legal reform|justice_grievance|Justice,RepresentativeAuthority|Major|EnactPolicy|Enact the named reform policy|LoyaltyTarget|Restore confidence through proven administration",
            "R079|justice_nobles|Repeal a hated privilege|unpopular_policy|RepresentativeAuthority,PopularWelfare|Major|RepealPolicy|Repeal the named policy|TreasuryPayment|Compensate those carrying its burden",
            "R080|justice_nobles|Protect the lawful privileges of clans|privilege_threat|NoblePrivilege,ClanPrivilege|Major|EnactPolicy|Enact the protective policy|ImproveClanRelation|Reach accord with every affected clan leader",

            "R081|construction|Complete stalled irrigation works|stalled_project|Infrastructure,Agriculture|Standard|CompleteConstruction|Complete the irrigation project|TreasuryPayment|Pay the settlement to finish it",
            "R082|construction|Build defenses before invasion|siege_risk|Infrastructure,MilitaryStrength|Major|CompleteConstruction|Complete the fortification project|GarrisonMinimum|Reinforce the settlement instead",
            "R083|construction|Change a ruinous vanity project|bad_project|PopularWelfare,Trade|Standard|ChangeConstruction|Switch to the demanded useful project|TreasuryPayment|Fund local needs while it continues",
            "R084|construction|Pause construction during famine|food_shortage_project|PopularWelfare,Agriculture|Standard|HaltConstructionDays|Halt the project temporarily|FoodDelivery|Supply enough food to continue safely",
            "R085|construction|Expand a town marketplace|trade_growth|Trade,Infrastructure|Major|CompleteConstruction|Complete the marketplace project|ProsperityTarget|Reach the prosperity target organically",
            "R086|construction|Repair a castle after siege|recent_siege|Infrastructure,Security|Major|CompleteConstruction|Complete the repair project|TreasuryPayment|Pay the assessed reconstruction grant",
            "R087|construction|Improve village access to market|isolated_village|Infrastructure,Agriculture|Standard|CompleteConstruction|Complete the linked infrastructure|HearthTarget|Grow the village despite current access",
            "R088|construction|Fund public works across the realm|realm_stagnation|Infrastructure,PopularWelfare|Major|TreasuryPayment|Pay a realmwide public works grant|ProsperityTarget|Raise affected settlement prosperity",

            "R089|prisoners_households|Ransom captured village defenders|captured_defenders|PopularWelfare,Justice|Standard|RansomPrisoners|Ransom the named defenders|ExchangePrisoners|Secure their release by exchange",
            "R090|prisoners_households|Release low-value prisoners during shortage|prisoner_food_cost|PopularWelfare,Peace|Minor|ReleasePrisoners|Release the named prisoners|FoodDelivery|Supply the gaol and retain them",
            "R091|prisoners_households|Recover a captured clan leader|captured_clan_leader|NoblePrivilege,ClanPrivilege|Major|RansomPrisoners|Pay the demanded ransom|WinEnemyEngagements|Capture leverage for an exchange",
            "R092|prisoners_households|Exchange prisoners after peace|postwar_prisoners|Peace,Justice|Standard|ExchangePrisoners|Complete a reciprocal exchange|ReleasePrisoners|Release the realm's prisoners unilaterally",
            "R093|prisoners_households|Aid households displaced by siege|displaced_households|PopularWelfare,Infrastructure|Major|TreasuryPayment|Pay resettlement grants|GoodsDelivery|Deliver rebuilding goods",
            "R094|prisoners_households|Support families of fallen soldiers|battle_casualties|PopularWelfare,MilitaryStrength|Standard|TreasuryPayment|Pay survivors' benefits|HoldFeast|Fund a memorial and household relief assembly",
            "R095|prisoners_households|Return hostages after accord|hostage_obligation|Justice,Peace|Standard|ReleasePrisoners|Release the named hostages|ImproveForeignRelation|Renegotiate their lawful status",
            "R096|prisoners_households|Relieve an overburdened noble household|household_debt|NoblePrivilege,PopularWelfare|Minor|CompensateClan|Provide household relief|ImproveClanRelation|Settle the dispute through a personal accord",

            "R097|taxation_obligations|Cut taxes in a disloyal town|low_loyalty_tax|PopularWelfare,RepresentativeAuthority|Standard|TaxReliefDays|Grant the demanded tax holiday|TreasuryPayment|Pay an equivalent civic grant",
            "R098|taxation_obligations|Reduce farmers' grain obligation|village_food_burden|Agriculture,PopularWelfare|Minor|WaiveObligationDays|Reduce the obligation temporarily|FoodDelivery|Make a one-time granary donation",
            "R099|taxation_obligations|Collect overdue revenue for defense|low_treasury_defense|RoyalAuthority,MilitaryStrength|Major|CollectTaxMinimum|Collect the required revenue|TreasuryPayment|Cover the need from the ruler's treasury",
            "R100|taxation_obligations|Suspend wartime levies after peace|postwar_levies|Peace,PopularWelfare|Standard|WaiveObligationDays|Suspend levies for the required period|GarrisonMaximum|Demobilize the troops supported by them",
            "R101|taxation_obligations|Lower merchant tariffs|merchant_burden|Trade,RepresentativeAuthority|Standard|ReduceTariffsDays|Reduce tariffs temporarily|ProsperityTarget|Raise prosperity enough to justify current rates",
            "R102|taxation_obligations|Fund an emergency levy fairly|emergency_levy|Justice,MilitaryStrength|Major|TreasuryPayment|Pay the levy from crown funds|ImproveClanRelation|Win assent from every affected clan leader",
            "R103|taxation_obligations|Forgive obligations after a raid|raid_tax_burden|PopularWelfare,Agriculture|Standard|WaiveObligationDays|Forgive taxes and food dues|TreasuryPayment|Pay direct compensation instead",
            "R104|taxation_obligations|Restore trust after broken fiscal promises|broken_tax_promise|Justice,RepresentativeAuthority|Major|TaxReliefDays|Honor the promised tax reduction|LoyaltyTarget|Restore loyalty through equivalent measurable relief"
        };

        private static readonly IReadOnlyList<ReignGovernmentResolutionTemplate> TemplatesInternal =
            Array.AsReadOnly(Rows.Select(Parse).ToArray());

        public static IReadOnlyList<ReignGovernmentResolutionTemplate> Templates => TemplatesInternal;

        public static ReignGovernmentResolutionTemplate? Find(string id)
        {
            return TemplatesInternal.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static int TargetFor(ReignGovernmentResolutionAction action, ReignGovernmentResolutionScale scale)
        {
            int index = Math.Max(1, Math.Min(3, (int)scale)) - 1;
            switch (action)
            {
                case ReignGovernmentResolutionAction.TreasuryPayment:
                case ReignGovernmentResolutionAction.CompensateClan: return new[] { 10000, 25000, 60000 }[index];
                case ReignGovernmentResolutionAction.FoodDelivery:
                case ReignGovernmentResolutionAction.GoodsDelivery:
                case ReignGovernmentResolutionAction.SupplyArmy: return new[] { 50, 150, 350 }[index];
                case ReignGovernmentResolutionAction.GarrisonMinimum:
                case ReignGovernmentResolutionAction.GarrisonMaximum:
                case ReignGovernmentResolutionAction.RecruitTroops:
                case ReignGovernmentResolutionAction.FieldArmyMinimum:
                case ReignGovernmentResolutionAction.CreateMilitia: return new[] { 25, 75, 150 }[index];
                case ReignGovernmentResolutionAction.LoyaltyTarget:
                case ReignGovernmentResolutionAction.SecurityTarget: return new[] { 5, 10, 20 }[index];
                case ReignGovernmentResolutionAction.ProsperityTarget: return new[] { 150, 400, 800 }[index];
                case ReignGovernmentResolutionAction.HearthTarget: return new[] { 25, 75, 150 }[index];
                case ReignGovernmentResolutionAction.ImproveForeignRelation:
                case ReignGovernmentResolutionAction.ImproveClanRelation: return new[] { 5, 10, 20 }[index];
                case ReignGovernmentResolutionAction.DefeatBanditParties:
                case ReignGovernmentResolutionAction.WinEnemyEngagements:
                case ReignGovernmentResolutionAction.ResolveIssueCount: return new[] { 1, 2, 3 }[index];
                default: return 1;
            }
        }

        public static int DeadlineDays(ReignGovernmentResolutionScale scale)
        {
            switch (scale)
            {
                case ReignGovernmentResolutionScale.Minor: return 7;
                case ReignGovernmentResolutionScale.Standard: return 14;
                default: return 21;
            }
        }

        public static ReignGovernmentResolutionReward CompletionReward(ReignGovernmentResolutionScale scale)
        {
            switch (scale)
            {
                case ReignGovernmentResolutionScale.Minor: return new ReignGovernmentResolutionReward(1, 50, 5, 2, 3);
                case ReignGovernmentResolutionScale.Standard: return new ReignGovernmentResolutionReward(2, 125, 10, 4, 6);
                default: return new ReignGovernmentResolutionReward(4, 250, 20, 8, 10);
            }
        }

        public static bool IsEligible(
            ReignGovernmentResolutionTemplate template,
            IEnumerable<string> evidenceTags,
            IEnumerable<ReignGovernmentPlank> governingPlanks)
        {
            if (template == null) return false;
            var evidence = new HashSet<string>(evidenceTags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (!evidence.Contains(template.EvidenceTag)) return false;
            var planks = new HashSet<ReignGovernmentPlank>(governingPlanks ?? Array.Empty<ReignGovernmentPlank>());
            return template.SupportingPlanks.Count == 0 || template.SupportingPlanks.Any(planks.Contains);
        }

        private static ReignGovernmentResolutionTemplate Parse(string row)
        {
            string[] parts = (row ?? string.Empty).Split('|');
            if (parts.Length != 10) throw new FormatException("Government resolution row must contain ten fields: " + row);
            ReignGovernmentResolutionScale scale = (ReignGovernmentResolutionScale)Enum.Parse(
                typeof(ReignGovernmentResolutionScale), parts[5], true);
            ReignGovernmentPlank[] planks = parts[4].Split(',')
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => (ReignGovernmentPlank)Enum.Parse(typeof(ReignGovernmentPlank), x.Trim(), true))
                .ToArray();
            ReignGovernmentResolutionAction first = (ReignGovernmentResolutionAction)Enum.Parse(
                typeof(ReignGovernmentResolutionAction), parts[6], true);
            ReignGovernmentResolutionAction second = (ReignGovernmentResolutionAction)Enum.Parse(
                typeof(ReignGovernmentResolutionAction), parts[8], true);
            return new ReignGovernmentResolutionTemplate(
                parts[0], parts[1], parts[2], parts[3], scale, planks,
                Route(first, parts[7], scale), Route(second, parts[9], scale));
        }

        private static ReignGovernmentResolutionRoute Route(
            ReignGovernmentResolutionAction action,
            string description,
            ReignGovernmentResolutionScale scale)
        {
            return new ReignGovernmentResolutionRoute(
                action,
                description,
                TargetFor(action, scale),
                UnitFor(action),
                DeadlineDays(scale));
        }

        private static ReignGovernmentResolutionUnit UnitFor(ReignGovernmentResolutionAction action)
        {
            switch (action)
            {
                case ReignGovernmentResolutionAction.TreasuryPayment:
                case ReignGovernmentResolutionAction.CompensateClan: return ReignGovernmentResolutionUnit.Gold;
                case ReignGovernmentResolutionAction.FoodDelivery:
                case ReignGovernmentResolutionAction.GoodsDelivery:
                case ReignGovernmentResolutionAction.SupplyArmy: return ReignGovernmentResolutionUnit.Goods;
                case ReignGovernmentResolutionAction.LoyaltyTarget: return ReignGovernmentResolutionUnit.Loyalty;
                case ReignGovernmentResolutionAction.ProsperityTarget: return ReignGovernmentResolutionUnit.Prosperity;
                case ReignGovernmentResolutionAction.HearthTarget: return ReignGovernmentResolutionUnit.Hearth;
                case ReignGovernmentResolutionAction.SecurityTarget: return ReignGovernmentResolutionUnit.Security;
                case ReignGovernmentResolutionAction.GarrisonMinimum:
                case ReignGovernmentResolutionAction.GarrisonMaximum:
                case ReignGovernmentResolutionAction.RecruitTroops:
                case ReignGovernmentResolutionAction.FieldArmyMinimum:
                case ReignGovernmentResolutionAction.CreateMilitia: return ReignGovernmentResolutionUnit.Troops;
                case ReignGovernmentResolutionAction.PatrolDays:
                case ReignGovernmentResolutionAction.GuardCaravansDays:
                case ReignGovernmentResolutionAction.EndVillageRaidsDays:
                case ReignGovernmentResolutionAction.HaltConstructionDays:
                case ReignGovernmentResolutionAction.TaxReliefDays:
                case ReignGovernmentResolutionAction.ReduceTariffsDays:
                case ReignGovernmentResolutionAction.WaiveObligationDays: return ReignGovernmentResolutionUnit.Days;
                case ReignGovernmentResolutionAction.DefeatBanditParties: return ReignGovernmentResolutionUnit.Parties;
                case ReignGovernmentResolutionAction.WinEnemyEngagements: return ReignGovernmentResolutionUnit.Engagements;
                case ReignGovernmentResolutionAction.ImproveForeignRelation:
                case ReignGovernmentResolutionAction.ImproveClanRelation: return ReignGovernmentResolutionUnit.Relations;
                case ReignGovernmentResolutionAction.ResolveIssueCount: return ReignGovernmentResolutionUnit.Issues;
                default: return ReignGovernmentResolutionUnit.Actions;
            }
        }
    }
}
