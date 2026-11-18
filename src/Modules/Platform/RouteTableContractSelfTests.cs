using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // The route inventory of the former HandleClient if/else router (unstable e0a0ca9), in its match order:
        // "<method> <exact|prefix> <comparison> <path> <campaign gate: read|none|- for prefixes>[ court]".
        // "court" rows were compiled only without REIGN_EXCLUDE_COURT. Generated from the old router source; the
        // registered route table must keep exactly this set, these gate decisions and this precedence.
        private static readonly string[] PreTableRouteInventory =
        {
            "GET exact Ordinal / none",
            "GET exact Ordinal /index.html none",
            "GET exact Ordinal /assets/reign-icon.webp none",
            "GET exact Ordinal /assets/ReignLogo.png none",
            "GET exact Ordinal /character-editor/portrait read",
            "GET exact Ordinal /health none",
            "POST prefix Ordinal /chat-view/ -",
            "POST exact Ordinal /api/activate read",
            "GET exact Ordinal /api/settings read",
            "GET exact Ordinal /api/gameplay-options read",
            "GET exact Ordinal /api/global-keys read",
            "POST exact Ordinal /api/global-keys/save read",
            "POST exact Ordinal /api/global-keys/unbind read",
            "POST exact Ordinal /api/settings read",
            "POST exact Ordinal /api/shutdown none",
            "POST exact Ordinal /api/settings/clear-key read",
            "GET exact Ordinal /api/diagnostics read",
            "POST exact Ordinal /api/diagnostics/test-llm read",
            "GET exact Ordinal /api/codex/status read",
            "POST exact Ordinal /api/codex/start read",
            "POST exact Ordinal /api/codex/stop read",
            "POST exact Ordinal /api/codex/login/start read",
            "POST exact Ordinal /api/codex/logout read",
            "POST exact Ordinal /api/codex/models/refresh read",
            "POST exact Ordinal /api/codex/models/verify read",
            "GET exact Ordinal /api/telemetry/status read",
            "GET exact Ordinal /api/provider/status read",
            "GET exact Ordinal /api/background/status read",
            "GET exact Ordinal /memory/background/status read",
            "GET exact Ordinal /llm/prompt-cache/status read",
            "POST exact Ordinal /llm/prompt-cache/probe read",
            "GET exact Ordinal /api/prompts read",
            "POST exact Ordinal /api/prompts read",
            "POST exact Ordinal /api/castle-chat/prompts read",
            "GET exact Ordinal /api/logs read",
            "GET exact Ordinal /api/campaigns read",
            "GET exact Ordinal /api/campaigns/storage-audit read",
            "GET exact Ordinal /api/campaigns/export none",
            "POST exact Ordinal /api/campaigns/import none",
            "POST exact Ordinal /api/campaigns/delete none",
            "POST exact Ordinal /api/campaigns/delete-all none",
            "POST exact Ordinal /api/campaigns/cleanup-zero-save none",
            "POST exact Ordinal /save-sync/register none",
            "POST exact Ordinal /save-sync/finalize none",
            "POST exact Ordinal /save-sync/delete-native-save none",
            "POST exact Ordinal /save-sync/campaign-unloaded none",
            "POST exact Ordinal /save-sync/load none",
            "POST exact Ordinal /api/save-sync/preview none",
            "GET exact Ordinal /api/save-sync/status none",
            "GET exact Ordinal /api/diagnostics/conversations read",
            "GET exact Ordinal /api/diagnostics/conversations/trace read",
            "GET exact Ordinal /api/diagnostics/conversations/payload read",
            "GET exact Ordinal /api/diagnostics/conversations/capture read",
            "POST exact Ordinal /api/diagnostics/conversations/capture read",
            "POST exact Ordinal /api/diagnostics/conversations/clear read",
            "GET exact Ordinal /api/diagnostics/conversations/export read",
            "POST exact Ordinal /api/logs/clear read",
            "GET exact Ordinal /api/logs/export read",
            "POST exact Ordinal /api/support-bundle read",
            "POST exact Ordinal /audit/ingest read",
            "GET exact Ordinal /audit/prompt read",
            "GET exact Ordinal /audit/query read",
            "POST exact Ordinal /tests/npc-dialogue/request none",
            "POST exact Ordinal /tests/npc-dialogue/request/poll none",
            "POST exact Ordinal /tests/npc-dialogue/request/ack none",
            "POST exact Ordinal /tests/npc-dialogue/start none",
            "POST exact Ordinal /tests/npc-dialogue/turn none",
            "POST exact Ordinal /tests/npc-dialogue/scene none",
            "POST exact Ordinal /tests/npc-dialogue/finish none",
            "POST exact Ordinal /tests/npc-dialogue/cancel none",
            "GET exact Ordinal /tests/npc-dialogue/status none",
            "POST exact Ordinal /tests/party-dialogue/request none",
            "POST exact Ordinal /tests/party-dialogue/request/poll none",
            "POST exact Ordinal /tests/party-dialogue/request/ack none",
            "POST exact Ordinal /tests/party-dialogue/start none",
            "POST exact Ordinal /tests/party-dialogue/turn none",
            "POST exact Ordinal /tests/party-dialogue/scene none",
            "POST exact Ordinal /tests/party-dialogue/finish none",
            "POST exact Ordinal /tests/party-dialogue/cancel none",
            "GET exact Ordinal /tests/party-dialogue/status none",
            "POST exact Ordinal /tests/live/arm read",
            "GET exact Ordinal /tests/live/runtime read",
            "POST exact Ordinal /tests/live/targets/search read",
            "POST exact Ordinal /tests/live/run/start read",
            "POST exact Ordinal /tests/live/command/enqueue read",
            "POST exact Ordinal /tests/live/run/pause read",
            "POST exact Ordinal /tests/live/run/resume read",
            "POST exact Ordinal /tests/live/run/cancel read",
            "GET exact Ordinal /tests/live/run/status read",
            "GET exact Ordinal /tests/live/run/report read",
            "POST exact Ordinal /tests/live/game/heartbeat read",
            "POST exact Ordinal /tests/live/game/poll read",
            "POST exact Ordinal /tests/live/game/ack read",
            "POST exact Ordinal /tests/live/game/result read",
            "POST exact Ordinal /tests/final-gauntlet/catalog/build none",
            "POST exact Ordinal /tests/final-gauntlet/state/fingerprint none",
            "GET exact Ordinal /tests/final-gauntlet/catalog none",
            "POST exact Ordinal /tests/final-gauntlet/run/start none",
            "POST exact Ordinal /tests/final-gauntlet/run/promote none",
            "POST exact Ordinal /tests/final-gauntlet/run/resume none",
            "POST exact Ordinal /tests/final-gauntlet/run/pause none",
            "POST exact Ordinal /tests/final-gauntlet/case/lease none",
            "POST exact Ordinal /tests/final-gauntlet/case/result none",
            "POST exact Ordinal /tests/final-gauntlet/action/evidence none",
            "POST exact Ordinal /tests/final-gauntlet/fault/arm none",
            "POST exact Ordinal /tests/final-gauntlet/run/cancel none",
            "GET exact Ordinal /tests/final-gauntlet/run/status none",
            "GET exact Ordinal /tests/final-gauntlet/run/report none",
            "GET exact Ordinal /tests/final-gauntlet/review-pack none",
            "GET exact Ordinal /tests/social-balance/manifest none",
            "GET exact Ordinal /tests/campaign-command/manifest none",
            "POST exact Ordinal /tests/campaign-command/prepare none",
            "POST exact Ordinal /tests/campaign-command/save/verify none",
            "POST exact Ordinal /tests/campaign-command/profile/result none",
            "GET exact Ordinal /tests/campaign-command/status none",
            "GET exact Ordinal /tests/campaign-command/report none",
            "GET exact Ordinal /tests/campaign-command/replay none",
            "POST exact Ordinal /tests/campaign-command/resume none",
            "POST exact Ordinal /tests/campaign-command/cancel none",
            "POST exact Ordinal /tests/campaign-command/execution none",
            "GET exact Ordinal /tests/campaign-command/readiness none",
            "POST exact Ordinal /tests/social-balance/prepare none",
            "POST exact Ordinal /tests/social-balance/case/result none",
            "POST exact Ordinal /tests/social-balance/snapshot none",
            "POST exact Ordinal /tests/social-balance/affinity/set none",
            "POST exact Ordinal /tests/social-balance/affair/candidates none",
            "GET exact Ordinal /tests/social-balance/status none",
            "GET exact Ordinal /tests/social-balance/export none",
            "POST exact Ordinal /tests/live/readiness/reset read",
            "POST exact Ordinal /tests/live/readiness/marker read",
            "POST exact Ordinal /tests/live/readiness/migrate-build read",
            "POST exact Ordinal /tests/live/readiness/roster read",
            "GET exact Ordinal /tests/live/readiness read",
            "POST exact Ordinal /tests/live/readiness/evaluate-run read",
            "POST exact Ordinal /tests/live/fixtures/relationship-history read",
            "POST exact Ordinal /tests/live/fixtures/manipulation read",
            "POST exact Ordinal /tests/live/fixtures/conversation-benefit read",
            "GET exact Ordinal /tests/cases none",
            "POST exact Ordinal /tests/cases/save none",
            "POST exact Ordinal /tests/run none",
            "POST exact Ordinal /tests/run-suite none",
            "POST exact Ordinal /tests/capture-from-correlation none",
            "GET exact Ordinal /tests/runs none",
            "POST exact Ordinal /tests/replay-corpus/capture none",
            "GET exact Ordinal /tests/replay-corpus none",
            "POST exact Ordinal /tests/replay-corpus/evaluate none",
            "POST exact Ordinal /verification/run none",
            "GET exact Ordinal /verification/status none",
            "POST exact Ordinal /verification/cancel none",
            "GET exact Ordinal /verification/results none",
            "POST exact Ordinal /verification/replay none",
            "POST exact Ordinal /events/ingest read",
            "POST exact Ordinal /world-history/ingest-batch read",
            "POST exact Ordinal /world-history/timeline/open read",
            "POST exact Ordinal /initialization/readiness/seal read",
            "POST exact Ordinal /initialization/readiness/status read",
            "POST exact Ordinal /initialization/readiness/retry read",
            "GET exact Ordinal /world-history/query read",
            "GET prefix OrdinalIgnoreCase /world-history/event/ -",
            "GET prefix OrdinalIgnoreCase /world-history/correlation/ -",
            "POST exact Ordinal /world-history/verify read",
            "POST exact Ordinal /world-history/lie-check read",
            "GET exact Ordinal /world-history/lie-checks read",
            "GET exact Ordinal /world-history/export read",
            "POST exact Ordinal /world-history/tests read",
            "POST exact Ordinal /diplomacy/director/evaluate read",
            "POST exact Ordinal /diplomacy/debug/random-event read",
            "GET exact Ordinal /political-pressure/notices read",
            "POST exact Ordinal /political-pressure/evaluate read",
            "POST exact Ordinal /political-pressure/acknowledge read",
            "GET exact Ordinal /clan-conflicts/notices read",
            "POST exact Ordinal /clan-conflicts/acknowledge read",
            "POST exact Ordinal /rebellions/evaluate read",
            "GET exact Ordinal /rebellions/query read",
            "POST exact Ordinal /negotiations/draft read",
            "POST exact Ordinal /negotiations/respond read",
            "GET exact Ordinal /negotiations/query read",
            "GET exact Ordinal /rebellions/tests read",
            "POST exact Ordinal /world-test/heartbeat read",
            "GET exact Ordinal /world-test/campaigns read",
            "GET exact Ordinal /world-test/overview read",
            "GET exact Ordinal /world-test/export read",
            "GET exact Ordinal /world-test/details read",
            "GET exact Ordinal /world-test/checkpoints read",
            "GET exact Ordinal /world-test/comparison read",
            "GET exact Ordinal /world-test/tests read",
            "POST exact Ordinal /court/session/open read court",
            "POST exact Ordinal /court/session/tick read court",
            "POST exact Ordinal /court/session/close read court",
            "POST exact Ordinal /court/agenda/build read court",
            "POST exact Ordinal /court/home read court",
            "POST exact Ordinal /court/view read court",
            "POST exact Ordinal /court/matter/start read court",
            "POST exact Ordinal /court/matter/respond read court",
            "POST exact Ordinal /court/matter/resolve read court",
            "POST exact Ordinal /court/offices/assign read court",
            "POST exact Ordinal /court/offices/dismiss read court",
            "POST exact Ordinal /court/royal-council/respond read court",
            "POST exact Ordinal /court/ruler-petition/respond read court",
            "POST exact Ordinal /court/life/scene read court",
            "POST exact Ordinal /court/life/respond read court",
            "GET exact Ordinal /court/patronage/public-history read court",
            "POST exact Ordinal /court/life/interpret read court",
            "POST exact Ordinal /court/life/family read court",
            "POST exact Ordinal /court/life/international/pending read court",
            "POST exact Ordinal /court/life/international/resolve read court",
            "POST exact Ordinal /court/life/international/refer read court",
            "POST exact Ordinal /government/meeting/speaker-statement read court",
            "POST exact Ordinal /government/hearing/discussion read court",
            "POST exact Ordinal /government/political-profiles/sync read court",
            "POST exact Ordinal /government/crown/decision read court",
            "POST exact Ordinal /clan-accords/sync read court",
            "POST exact Ordinal /court/regent/assign read court",
            "POST exact Ordinal /court/regent/dismiss read court",
            "POST exact Ordinal /court/ambassadors/assign read court",
            "POST exact Ordinal /court/ambassadors/mission read court",
            "POST exact Ordinal /court/ambassadors/recall read court",
            "POST exact Ordinal /court/foreign-ambassadors/establish read court",
            "POST exact Ordinal /court/foreign-ambassadors/dismiss read court",
            "POST exact Ordinal /court/foreign-ambassadors/end read court",
            "POST exact Ordinal /court/foreign-ambassadors/location read court",
            "POST exact Ordinal /court/foreign-ambassadors/authority read court",
            "POST exact Ordinal /court/foreign-ambassadors/tick read court",
            "POST exact Ordinal /court/foreign-ambassadors/referrals/respond read court",
            "POST exact Ordinal /court/foreign-ambassadors/test read court",
            "POST exact Ordinal /court/intelligence/start read court",
            "POST exact Ordinal /court/intelligence/cancel read court",
            "POST exact Ordinal /court/spymaster/memory read court",
            "POST exact Ordinal /court/spymaster/social/status read court",
            "POST exact Ordinal /court/spymaster/social/apply read court",
            "POST exact Ordinal /court/spymaster/exposure read court",
            "POST exact Ordinal /court/spymaster/agents/tick read court",
            "POST exact Ordinal /court/spymaster/agents/status read court",
            "POST exact Ordinal /court/spymaster/agents/expose read court",
            "POST exact Ordinal /arrests/case/apply read court",
            "POST exact Ordinal /arrests/evidence/evaluate read court",
            "POST exact Ordinal /arrests/test/contracts read court",
            "POST exact Ordinal /court/obligations/query read court",
            "POST exact Ordinal /court/obligations/upsert read court",
            "POST exact Ordinal /court/obligations/resolve read court",
            "POST exact Ordinal /court/plots/query read court",
            "POST exact Ordinal /court/plots/upsert read court",
            "POST exact Ordinal /court/world-action/validate read court",
            "POST exact Ordinal /court/diplomacy/propose read court",
            "POST exact Ordinal /court/supply/validate read court",
            "POST exact Ordinal /court/native/validate read court",
            "GET exact Ordinal /court/tests read court",
            "GET exact Ordinal /diplomacy/events/next read",
            "POST exact Ordinal /diplomacy/events/acknowledge read",
            "POST exact Ordinal /memory/event read",
            "POST exact Ordinal /memory/conversation/start read",
            "POST exact Ordinal /memory/conversation/finish read",
            "POST exact Ordinal /memory/conversation/recover read",
            "POST exact Ordinal /memory/conversation_finished read",
            "POST exact Ordinal /memory/action_result read",
            "POST exact Ordinal /memory/build_context read",
            "POST exact Ordinal /memory/consolidate read",
            "GET exact Ordinal /memory/embeddings/status read",
            "POST exact Ordinal /memory/embeddings/reindex read",
            "POST exact Ordinal /memory/embeddings/retry-failed read",
            "POST exact Ordinal /rumors/submit read",
            "POST exact Ordinal /rumors/status read",
            "POST exact Ordinal /rumors/correct read",
            "POST exact Ordinal /social/catalog read",
            "GET exact Ordinal /social/catalog read",
            "POST exact Ordinal /relationships/evaluate read",
            "POST exact Ordinal /relationships/conversation/evaluate read",
            "POST exact Ordinal /relationships/conversation/native-receipt read",
            "GET exact Ordinal /relationships/conversation/recent read",
            "POST exact Ordinal /relationships/context read",
            "POST exact Ordinal /relationships/effective/batch read",
            "POST exact Ordinal /relationships/history/ensure read",
            "GET exact Ordinal /relationships/history/query read",
            "POST exact Ordinal /relationships/history/query read",
            "POST exact Ordinal /relationships/developments/poll read",
            "POST exact Ordinal /relationships/developments/resolve read",
            "POST exact Ordinal /relationships/director/snapshot read",
            "POST exact Ordinal /relationships/director/run read",
            "POST exact Ordinal /relationships/director/poll_actions read",
            "POST exact Ordinal /relationships/director/report_action read",
            "POST exact Ordinal /relationships/director/report_actions read",
            "POST exact Ordinal /relationships/director/status read",
            "POST exact Ordinal /relationships/ambient/snapshot read",
            "POST exact Ordinal /relationships/initialization/seed read",
            "POST exact Ordinal /relationships/ambient/native_sync/report read",
            "POST exact Ordinal /relationships/ambient/native_sync/pull read",
            "POST exact Ordinal /relationships/ambient/native_sync/receipts read",
            "GET exact Ordinal /relationships/ambient/status read",
            "POST exact Ordinal /relationships/ambient/status read",
            "POST exact Ordinal /relationships/simulation/census/status read",
            "POST exact Ordinal /relationships/simulation/start read",
            "POST exact Ordinal /relationships/simulation/stop read",
            "POST exact Ordinal /relationships/simulation/status read",
            "POST exact Ordinal /identity/synchronize read",
            "POST exact Ordinal /identity/encounter read",
            "POST exact Ordinal /identity/introduction read",
            "POST exact Ordinal /identity/query read",
            "POST exact Ordinal /identity/reset read",
            "POST exact Ordinal /identity/debug/know-everyone read",
            "POST exact Ordinal /family/verify_conception_attempt read",
            "POST exact Ordinal /family/pregnancy/sync read",
            "POST exact Ordinal /family/conception/report read",
            "POST exact Ordinal /family/parentage/reveal read",
            "POST exact Ordinal /correspondence/threads read",
            "POST exact Ordinal /correspondence/send read",
            "POST exact Ordinal /correspondence/tick read",
            "POST exact Ordinal /approaches/evaluate read",
            "POST exact Ordinal /approaches/decision read",
            "POST exact Ordinal /correspondence/autonomous/candidates read",
            "POST exact Ordinal /correspondence/autonomous/evaluate read",
            "POST exact Ordinal /correspondence/read read",
            "POST exact Ordinal /context/build read",
            "POST exact Ordinal /context/select read",
            "POST exact Ordinal /actions/route read",
            "POST exact Ordinal /actions/plan read",
            "POST exact Ordinal /characters/upsert read",
            "POST exact Ordinal /characters/upsert-batch read",
            "POST exact Ordinal /portrait-roster/upsert-batch read",
            "POST exact Ordinal /characters/construct read",
            "POST exact Ordinal /characters/construct-batch read",
            "POST exact Ordinal /characters/retry-failed read",
            "GET exact Ordinal /characters/profile-library/status read",
            "POST exact Ordinal /characters/profile-library/action read",
            "GET exact Ordinal /characters/schema read",
            "POST exact Ordinal /characters/diagnose read",
            "POST exact Ordinal /character-editor/list read",
            "POST exact Ordinal /character-editor/load read",
            "GET exact Ordinal /characters/narrative read",
            "POST exact Ordinal /character-editor/construct read",
            "POST exact Ordinal /character-editor/social/rumor/add read",
            "POST exact Ordinal /character-editor/social/reputation/add read",
            "POST exact Ordinal /character-editor/social/reputation/retry-reason read",
            "POST exact Ordinal /character-editor/save read",
            "POST exact Ordinal /character-editor/rollback read",
            "POST exact Ordinal /character-editor/portrait/upload read",
            "POST exact Ordinal /character-editor/portrait/select read",
            "POST exact Ordinal /character-editor/native/poll read",
            "POST exact Ordinal /character-editor/native/report read",
            "POST exact Ordinal /notables/mbti/initialize read",
            "POST exact Ordinal /notables/profiles/prewarm read",
            "POST exact Ordinal /notables/mbti/confirm-batch read",
            "POST exact Ordinal /dialogue/history read",
            "POST exact Ordinal /dialogue/respond read",
            "POST exact Ordinal /conversation-scene/hourly-tick read",
            "POST exact Ordinal /gossip/test read",
            "POST exact Ordinal /gossip/tick read",
            "POST prefix Ordinal /communications/ -",
            "GET exact Ordinal /communications/status read",
            "GET exact Ordinal /gossip/status read",
            "POST exact Ordinal /conversation-scene/formal-outfits/scan read",
            "POST prefix Ordinal /rulers/ -",
            "POST prefix Ordinal /npc-lives/ -",
            "POST prefix Ordinal /party-banter/ -",
            "POST exact Ordinal /party-chat/turn-plan read",
            "POST exact Ordinal /party-chat/respond read",
            "POST exact Ordinal /party-chat/history read",
            "POST exact Ordinal /tavern-house/respond read",
            "POST exact Ordinal /tavern-house/turn-plan read",
            "POST exact Ordinal /tavern-house/confirm read",
            "POST exact Ordinal /tavern-house/scene read",
            "POST exact Ordinal /tavern-house/close read",
            "POST exact Ordinal /events/social/start read",
            "POST exact Ordinal /events/social/approaches/roll read",
            "POST exact Ordinal /events/social/respond read",
            "POST exact Ordinal /events/social/turn read",
            "POST exact Ordinal /events/social/turn-plan read",
            "POST exact Ordinal /events/social/resolve read",
            "POST exact Ordinal /events/social/history read",
            "POST exact Ordinal /events/generated-wilderness/plan read",
            "POST exact Ordinal /portraits/register read",
            "GET exact Ordinal /portraits/vision/models read",
            "GET exact Ordinal /portraits/status read",
            "POST exact Ordinal /portraits/status read",
            "POST exact Ordinal /portraits/request read",
            "POST prefix Ordinal /portraits/outfit/ -",
            "POST exact Ordinal /portraits/generate read",
            "POST exact Ordinal /portraits/generate/status read",
            "POST exact Ordinal /castle-chat/scene/generate read",
            "POST exact Ordinal /family-chambers/scene/generate read",
            "POST exact Ordinal /family-chambers/childhood/mature read",
            "GET exact Ordinal /portraits/shared-generation/catalog read",
            "POST exact Ordinal /portraits/shared-generation/open-folder read",
            "POST exact Ordinal /portraits/shared-generation/generate-one read",
            "POST exact Ordinal /portraits/shared-generation/delete-one read",
            "POST exact Ordinal /portraits/shared-generation/build-missing read",
            "GET exact Ordinal /portraits/shared-generation/status read",
            "POST exact Ordinal /portraits/shared-generation/cancel read",
            "POST exact Ordinal /portraits/diagnose read",
            "POST exact Ordinal /portraits/derivatives/build-missing read",
            "POST exact Ordinal /portraits/derivatives/rebuild-fallback read",
            "GET exact Ordinal /portraits/derivatives/status read",
            "GET exact Ordinal /actions/catalog read",
            "GET exact Ordinal /actions/failures read",
            "GET exact Ordinal /actions/next read",
            "POST exact Ordinal /actions/next read",
            "POST exact Ordinal /actions/propose read",
            "POST exact Ordinal /actions/decide read",
            "POST exact Ordinal /actions/report read",
            "GET exact Ordinal /actions/terminal-report/v1 read",
            "POST exact Ordinal /actions/terminal-report/v1 read",
            "POST exact Ordinal /conversation/view-insertion read",
            "POST exact Ordinal /campaign-commands/report read",
            "POST exact Ordinal /campaign-commands/judge read",
            "POST exact Ordinal /llm/chat read",
        };

        // "<read|none> <path>": the former EnterCampaignRequestAccess decision for paths that are not routes.
        private static readonly string[] PreTableCampaignGateProbes =
        {
            "none /save-sync/x",
            "none /SAVE-SYNC/x",
            "read /save-sync",
            "none /api/save-sync/",
            "none /API/Save-Sync/y",
            "read /api/save-sync",
            "read /tests/live",
            "read /TESTS/LIVE",
            "read /tests/live/",
            "read /tests/Live/x",
            "none /tests/livex",
            "none /tests/",
            "none /Tests/run",
            "read /tests",
            "none /verification/",
            "none /VERIFICATION/x",
            "read /verification",
            "read /HEALTH",
            "read /health/",
            "read /Index.html",
            "read /api/shutdown/x",
            "read /API/SHUTDOWN",
            "read /api/campaigns/Export",
            "read ",
            "read /unknown",
            "read /npc-lives/opening",
            "read /notables/profiles/prewarm",
            "read /events/social/turn",
            "read /tests/live/game/heartbeat",
            "none /tests/run-suite",
        };

        private static List<Dictionary<string, object>> RunRouteTableContractSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => results.Add(new Dictionary<string, object>
            { ["caseId"] = id, ["suite"] = "interaction_architecture", ["passed"] = passed, ["summary"] = summary, ["data"] = data });
#if REIGN_EXCLUDE_COURT
            bool courtCompiled = false;
#else
            bool courtCompiled = true;
#endif
            ReignRouteTable table = ServerRoutes.Value;
            List<(ReignRoute Route, string Gate)> expected = new List<(ReignRoute, string)>();
            foreach (string row in PreTableRouteInventory)
            {
                string[] parts = row.Split(' ');
                if (parts.Length > 5 && parts[5] == "court" && !courtCompiled) continue;
                expected.Add((new ReignRoute
                {
                    Method = parts[0],
                    Match = parts[1] == "exact" ? RouteMatch.Exact : RouteMatch.Prefix,
                    Comparison = (StringComparison)Enum.Parse(typeof(StringComparison), parts[2]),
                    Path = parts[3]
                }, parts[4]));
            }

            List<string> expectedKeys = expected.Select(item => item.Route.ContractKey).OrderBy(key => key, StringComparer.Ordinal).ToList();
            List<string> actualKeys = table.Routes.Select(route => route.ContractKey).OrderBy(key => key, StringComparer.Ordinal).ToList();
            List<string> missing = expectedKeys.Except(actualKeys, StringComparer.Ordinal).ToList();
            List<string> added = actualKeys.Except(expectedKeys, StringComparer.Ordinal).ToList();
            add("route_table_inventory_matches_former_router",
                expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
                "The registered route table declares exactly the former router's methods, paths and match kinds.",
                new Dictionary<string, object>
                {
                    ["expected"] = expectedKeys.Count,
                    ["registered"] = actualKeys.Count,
                    ["servedByTable"] = table.Routes.Count(route => route.Handler != null),
                    ["servedByLegacyChain"] = table.Routes.Count(route => route.Handler == null),
                    ["missing"] = missing,
                    ["added"] = added
                });

            List<string> gateMismatches = new List<string>();
            foreach ((ReignRoute route, string gate) in expected)
            {
                if (route.Match != RouteMatch.Exact) continue;
                bool exempt = table.IsCampaignGateExempt(route.Path);
                if (exempt != (gate == "none")) gateMismatches.Add(route.ContractKey + " expected " + gate);
            }
            foreach (string probe in PreTableCampaignGateProbes)
            {
                int split = probe.IndexOf(' ');
                string path = probe.Substring(split + 1);
                bool exempt = table.IsCampaignGateExempt(path);
                if (exempt != (probe.Substring(0, split) == "none")) gateMismatches.Add("probe '" + path + "' expected " + probe.Substring(0, split));
            }
            foreach (ReignRoute route in table.Routes)
            {
                // A declared exemption must be the path's actual policy for every method registered on it.
                if (route.Match == RouteMatch.Exact
                    && (route.CampaignGate == CampaignGatePolicy.None) != table.IsCampaignGateExempt(route.Path)
                    && !table.ExemptAreas.Any(area => area.Contains(route.Path)))
                    gateMismatches.Add("declared " + route.ContractKey + " " + route.CampaignGate);
            }
            add("route_table_campaign_gate_matches_former_exemptions",
                gateMismatches.Count == 0,
                "Campaign-gate exemptions declared on routes and exempt areas reproduce the former path list, including unregistered paths and the gated /tests/live subtree.",
                new Dictionary<string, object> { ["mismatches"] = gateMismatches });

            List<string> precedenceMismatches = new List<string>();
            List<(string Method, string Path)> requests = expected
                .Select(item => (item.Route.Method, item.Route.Match == RouteMatch.Exact ? item.Route.Path : item.Route.Path + "contract-probe"))
                .ToList();
            requests.AddRange(new[] { ("PUT", "/health"), ("POST", "/health"), ("HEAD", "/"), ("OPTIONS", "/api/settings"),
                ("GET", "/no-such-route"), ("GET", "/HEALTH"), ("POST", "/chat-view"), ("POST", "/Communications/x"),
                ("GET", "/WORLD-HISTORY/EVENT/abc"), ("delete", "/api/campaigns/delete") });
            foreach ((string method, string path) in requests)
            {
                ReignRoute first = expected.Select(item => item.Route).FirstOrDefault(route => route.Matches(method, path));
                ReignRoute resolved = table.Resolve(method, path);
                string expectedKey = first?.ContractKey ?? "unknown";
                string resolvedKey = resolved?.ContractKey ?? "unknown";
                if (expectedKey != resolvedKey) precedenceMismatches.Add(method + " " + path + " expected " + expectedKey + " resolved " + resolvedKey);
            }
            Dictionary<string, int> formerOrder = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < expected.Count; i++)
            {
                if (!formerOrder.ContainsKey(expected[i].Route.ContractKey)) formerOrder[expected[i].Route.ContractKey] = i;
            }
            for (int i = 0; i < table.Routes.Count; i++)
            {
                for (int j = i + 1; j < table.Routes.Count; j++)
                {
                    ReignRoute earlier = table.Routes[i], later = table.Routes[j];
                    string laterProbe = later.Match == RouteMatch.Exact ? later.Path : later.Path + "contract-probe";
                    string earlierProbe = earlier.Match == RouteMatch.Exact ? earlier.Path : earlier.Path + "contract-probe";
                    bool overlap = earlier.Matches(later.Method, laterProbe) || later.Matches(earlier.Method, earlierProbe);
                    if (!overlap) continue;
                    if (!formerOrder.TryGetValue(earlier.ContractKey, out int a) || !formerOrder.TryGetValue(later.ContractKey, out int b) || a > b)
                        precedenceMismatches.Add("overlap " + earlier.ContractKey + " before " + later.ContractKey);
                }
            }
            add("route_table_lookup_preserves_former_precedence",
                precedenceMismatches.Count == 0,
                "Every former route, prefix probe and unknown method or path resolves to the same first match (or to no route, the 404) as the former if/else order, and overlapping routes keep their former relative order.",
                new Dictionary<string, object> { ["requests"] = requests.Count, ["mismatches"] = precedenceMismatches });

            List<string> orphanedLegacyRoutes = FindLegacyRoutesWithoutChainBranch(table, out string legacySourceError);
            add("route_table_legacy_routes_have_chain_branches",
                legacySourceError == null && orphanedLegacyRoutes.Count == 0,
                "Every route registered as Legacy(...) still has its branch in HandleClient's legacy chain (or in the module router that chain calls), so a migrated or deleted branch cannot leave a registration that silently returns 404.",
                new Dictionary<string, object>
                {
                    ["legacyRoutes"] = table.Routes.Count(route => route.Handler == null),
                    ["orphaned"] = orphanedLegacyRoutes,
                    ["sourceError"] = legacySourceError ?? ""
                });
            return results;
        }

        // Source scan of the legacy chain: HandleClient between the table dispatch and the 404 branch, plus the
        // module routers it delegates to. A chain branch matches a Legacy route when its condition names the path
        // (exact or prefix) and the method; a module router matches when the chain calls it and it names the path
        // and the method.
        private static List<string> FindLegacyRoutesWithoutChainBranch(ReignRouteTable table, out string error)
        {
            error = null;
            List<string> orphaned = new List<string>();
            string sourceRoot = FindVerificationSourceRoot();
            string serverRoot = string.IsNullOrWhiteSpace(sourceRoot) ? "" : System.IO.Path.Combine(sourceRoot, "ReignServer");
            string programPath = string.IsNullOrWhiteSpace(serverRoot) ? "" : VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src/Modules/Platform");
            if (!System.IO.File.Exists(programPath))
            {
                error = "Platform/Program.cs source was not found beneath '" + serverRoot + "'.";
                return orphaned;
            }
            string program = System.IO.File.ReadAllText(programPath).Replace("\r\n", "\n");
            int start = program.IndexOf("if (TryDispatchRegisteredRoute(request, routeContext, out response))", StringComparison.Ordinal);
            int end = start < 0 ? -1 : program.IndexOf("[\"error\"] = \"Unknown route.\"", start, StringComparison.Ordinal);
            if (start < 0 || end < 0)
            {
                error = "The HandleClient legacy chain could not be located in Platform/Program.cs.";
                return orphaned;
            }
            string chain = program.Substring(start, end - start);
            // Each branch condition runs from its "if (" to the opening brace line of its body.
            List<string> conditions = new List<string>();
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(chain,
                @"\n {20}(?:else )?if \((?<condition>[\s\S]*?)\n {20}\{"))
                conditions.Add(match.Groups["condition"].Value);
            Dictionary<string, string> routers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TryHandleSpymasterRoute"] = "Program.SpymasterRoutes.cs",
                ["TryHandleArrestRoute"] = "ArrestSystem.cs",
                ["TryHandleReputationRoute"] = "Program.ReputationRoutes.cs"
            };
            List<string> routerSources = new List<string>();
            foreach (KeyValuePair<string, string> router in routers)
            {
                if (!conditions.Any(condition => condition.Contains(router.Key + "(request, out response)", StringComparison.Ordinal))) continue;
                string path = VerificationSourceLocator.ResolveUnique(serverRoot, router.Value, "src/Modules");
                if (System.IO.File.Exists(path)) routerSources.Add(System.IO.File.ReadAllText(path));
            }
            foreach (ReignRoute route in table.Routes.Where(route => route.Handler == null))
            {
                string pathToken = route.Match == RouteMatch.Exact
                    ? "request.Path == \"" + route.Path + "\""
                    : "request.Path.StartsWith(\"" + route.Path + "\", StringComparison." + route.Comparison + ")";
                string methodToken = "request.Method == \"" + route.Method + "\"";
                bool inChain = conditions.Any(condition => condition.Contains(pathToken, StringComparison.Ordinal)
                    && condition.Contains(methodToken, StringComparison.Ordinal));
                bool inRouter = route.Match == RouteMatch.Exact && routerSources.Any(source =>
                    (source.Contains("case \"" + route.Path + "\":", StringComparison.Ordinal)
                        || source.Contains(pathToken, StringComparison.Ordinal))
                    && source.Contains("\"" + route.Method + "\"", StringComparison.Ordinal));
                if (!inChain && !inRouter) orphaned.Add(route.ContractKey);
            }
            return orphaned;
        }
    }
}
