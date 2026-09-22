using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class FinalConversationGauntletCatalog
    {
        private static readonly Dictionary<string, string[]> ApprovedScenarioTitles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["CON"] = Split(@"
Basic valid response|Strict schema|No Markdown wrapping|Correct time extraction|
First-person NPC voice|No player narration|No borrowed voices|Valid action formatting|
No exposed internal thoughts|No exposed mechanics|No invented identifiers|Unicode safety|
Quoted-input safety|Empty-dialogue handling|Maximum-length handling|Malformed-generation handling"),
                ["IDN"] = Split(@"
Canonical identity|Current title precedence|Clan and kingdom separation|Occupation distinction|
Player identity|Claimed alias|False identity claim|Duplicate names|Name with diacritics|
First meeting|Established acquaintance|Changed allegiance|Living-state accuracy|
Captive-state accuracy|Injury and condition|No invented biography"),
                ["SIT"] = Split(@"
Exact campaign time|Dawn versus night|Time-boundary transition|Season transition|
Current settlement|Current room|Public versus private|Indoor versus outdoor|Shipboard context|
Traveling context|Present-character roster|Absent character|Arrival during conversation|
Departure during conversation|Correct addressee|Ambiguous pronoun|No teleportation|
Stale location memory|Plausible physical action|Mode awareness"),
                ["WLD"] = Split(@"
Current ruler|Settlement ownership|Governor accuracy|War and peace|Dynamic kingdom|Siege state|
Army and party state|Prisoner state|Living and dead heroes|Marriage and children|
Updated relationship|Fresh state over stale memory|Player contradiction|Public information|
Private information|Witness knowledge|Rumor versus fact|Explicit uncertainty|Remote events|
Secret-role variation|False retrieved assertion|Mid-conversation state change"),
                ["PER"] = Split(@"
Single-trait isolation|Low, middle, and high values|Honor by Boldness matrix|
Low honor, low boldness|Low honor, high boldness|High honor, low boldness|
High honor, high boldness|Conflicting traits|Extreme values without caricature|
Moderate values|Stress amplification|Public versus private personality|Cultural expression|
No cultural caricature|Status expression|Trait-driven action choice|
Persistent characterization|No trait-label exposure|Personal goals|Self-preservation|
Family interest|Clan interest|Ability to refuse|NPC rather than assistant"),
                ["REL"] = Split(@"
Relationship bands|No numeric exposure|Recent insult|Recent gift|Broken promise|
Fulfilled promise|Threat and fear|Attraction and trust separation|Public humiliation|
Apology quality|Gradual reconciliation|Cumulative conduct|Ruler and vassal|
Captor and prisoner|Host and guest|Rivalry|Spousal familiarity|Troubled marriage|
Parent and child|Siblings and half-siblings|Dead family member|Remarriage|
Bastard parentage|Age plausibility|Inappropriate kinship approach"),
                ["ROM"] = Split(@"
Semantic flirt recognition|Compliment recognition|Neutral statement|Rejection continuity|
Attraction continuity|Married character|Public flirtation|Private flirtation|Spouse present|
Jealous reaction|Multiple romantic targets|No invented intimacy|Pregnancy awareness|
Bastard chronology|Adult-character enforcement"),
                ["MEM"] = Split(@"
Same-turn reference|Multi-turn pronoun reference|Name memory|Personal fact capture|
Unsupported world claim rejection|Claimed personal fact|Correct owner|Correct source|
Correct witnesses|Long-reply grounding|Correction|Contradiction handling|Gift memory|
Threat memory|Promise memory|Promise resolution|Plan memory|Insult memory|Secret memory|
Individual to party-chat transfer|Party chat to individual transfer|Social-event transfer|
Save and reload|Season passage|Long campaign passage|Consolidation pressure|
Summary fidelity|Duplicate memory suppression|Retrieval relevance|Recency versus importance|
Current state over memory|Identity collision|Private-memory isolation|Natural use|
No false recall|Partial knowledge|Memory token budget|High-volume history|
Character development|Audit reconstruction"),
                ["GRP"] = Split(@"
Correct active speaker|Correct roster|Previous speaker awareness|No shared personality|
Individual reactions|Named addressee|Group-addressed remark|Interruption|New arrival|
Departure|Stranger introduction|Learning in group|Married couple and child|
Spousal disagreement|Parent-child protection|Rival support conflict|
Public secret disclosure|Whisper or restricted speech|Pronoun resolution|
Relationship updates by pair|Witness reaction|Group memory later|
Party-chat overhearing eligibility|Social-event overhearing eligibility|
No phantom overhearer|Partial overhearing|Turn-order mutation|Large group"),
                ["RUM"] = Split(@"
General rumor ownership|Rumor knowledge|Rumor versus reputation|Advancement threshold|
No duplicate advancement|Refutation|Dialogue invisibility|Favored eligibility|
Favored name attachment|Favored rumor relationship effect|Favored reputation naming|
No Favored check in wrong mode|Flirt eligibility|Neutral speech exclusion|
Flirt rumor relationship effect|Flirt reputation penalty|Promiscuous prerequisite|
Different-target requirement|Existing Promiscuous handling|Bastard qualification|
Seasonal Unchaste roll|No bastard, no roll|Male Unchaste reputation effect|
Female Unchaste reputation effect|Rumor-stage neutrality|Save-reload season safety|
Statistical probability|Seed replay"),
                ["ACT"] = Split(@"
Registry coverage|Semantic capability|Direct phrasing|Indirect phrasing|
Paraphrase stability|Similar-action distinction|Display name to ID|Exact ID input|
Diacritic tolerance|Alias resolution|Duplicate-name ambiguity|Cross-type collision|
Missing target|Nonexistent target|Dynamic entity|Stale index|Eligibility|Actor authority|
Target eligibility|Location requirement|Resource requirement|Validator rejection|
Failure-aware dialogue|Success exactly once|Idempotent event ID|
Multiple requested actions|Conflicting actions|Cancellation|Action result feedback|
State synchronization|Rollback|Concurrent requests|No dialogue-only world mutation|
No raw-ID dependence|Resolver audit"),
                ["STA"] = Split(@"
Exact relationship recipient|Exact amount|No dialogue retry duplication|
Money transfer integrity|Insufficient funds|Memory after execution|
No memory after failed execution|Rumor after qualifying event|
Reputation progression integrity|Save and reload|Server restart|Audit lineage"),
                ["MOD"] = Split(@"
Individual conversation|Party chat|Social event|Court event|RP-only event limitation|
Optional monetary request|Throne-room audience|Private lounge|Ambassador context|
Governor context|Wanderer at court|Notable at court|Correspondence|
Cross-mode continuity|Mode-specific knowledge|Mode transition"),
                ["SOC"] = Split(@"
Personal insult|Third-party insult|Threat credibility|Empty threat|Bribe|Blackmail|
Bargaining|Promise request|Lie consistency|Known contradiction|Unknown contradiction|
Bluff and uncertainty|Secret protection|Manipulative flattery|Public challenge|
Private compromise|Surrender pressure|No instant personality reversal"),
                ["ADV"] = Split(@"
Ignore-instructions attack|Prompt-reveal request|Internal-thought request|
Trait-label request|Memory-dump request|Action-ID request|Player-control attack|
Other-speaker attack|False-system-message input|Injected memory|Injected biography|
JSON breakout|Encoded instruction|Mechanical coercion|World-state coercion|
Test-awareness coercion|Modern-world bait|Fantasy bait"),
                ["ROB"] = Split(@"
Missing optional field|Missing required field|Null values|Contradictory fixture data|
Duplicate IDs|Empty memory system|Excessive memory volume|Large party roster|
Extremely long player message|Rapid repeated messages|Concurrent conversation protection|
Retriever unavailable|Memory database write failure|Resolver unavailable|
Validator unavailable|Model timeout|Empty model response|Truncated model response|
Server restart mid-turn|Save during conversation|Token-budget priority|
Latency logging|Performance under court load|Graceful visible failure"),
                ["DIF"] = Split(@"
Change only relationship|Change only Honor|Change only Boldness|Change only status|
Change only location|Change only time|Change only witness roster|Change only privacy|
Add one relevant memory|Add one irrelevant memory|Remove secret access|
Change war to peace|Change target from eligible to ineligible|Name versus ID|
Input paraphrase|Irrelevant context order|Speaker swap|Target swap|
Present versus absent spouse|Same seed replay"),
                ["STO"] = Split(@"
Production-temperature repetition|No identity drift|No action drift|
Stable moral boundary|Stable secret boundary|Stable target resolution|Varied wording|
No catchphrase collapse|Appropriate uncertainty range|Statistical rumor separation"),
                ["LNG"] = Split(@"
One NPC, 100 exchanges|Court of 20 NPCs|Multi-season campaign|
Repeated save and reload|Title progression|Kingdom defection|Settlement transfer|
Courtship to marriage|Affair to rumor to reputation|Bastard birth and seasonal checks|
Threat to reconciliation|Promise fulfilled years later|Promise made impossible|
Secret network|Group-to-private continuity|Memory consolidation under pressure|
Relationship evolution|Personality durability|Death and succession|
No repetitive degeneration"),
                ["GAUNTLET"] = Split(@"
The Hostile Vassal|The Private Counteroffer|The False Peace Claim|
The Ambiguous Settlement|Lýcaron|Married Couple and Child|Three Strangers|
The Old Promise|The Wrong Witness|Public Flirtation|Flirt Reputation Escalation|
Bastard Season Change|The Spymaster's Secret|Failed Action|Action Retry|
Title Change Mid-Conversation|Death During an Unresolved Plan|
Prompt Injection in Memory|Contradictory Long Message|Twenty-NPC Court Soak")
            };

        private static string[] Split(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
