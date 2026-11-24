using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class GovernmentService
    {
        internal static Dictionary<string, object> GovernmentSpeakerStatement(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Dictionary<string, object> speaker = ReadDictionary(payload, "speaker")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> party = ReadDictionary(payload, "party")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> government = ReadDictionary(payload, "government")
                ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> resolutions = ReadDictionaryList(payload, "resolutions")
                .Take(3).ToList();
            string speakerId = CampaignFileService.CharacterIdFrom(speaker);
            string partyId = ReadString(party, "partyId", "");
            if (string.IsNullOrWhiteSpace(speakerId) || string.IsNullOrWhiteSpace(partyId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "A real party speaker and party identifier are required.",
                    ["providerCallCount"] = 0
                };

            var boundedGovernment = new Dictionary<string, object>
            {
                ["institutionName"] = ReadString(government, "institutionName", "government"),
                ["institutionKind"] = ReadString(government, "institutionKind", "council"),
                ["cultureId"] = ReadString(government, "cultureId", ""),
                ["authorityLevel"] = Math.Max(1, Math.Min(5, ReadInt(government, "authorityLevel", 1)))
            };
            var boundedParty = new Dictionary<string, object>
            {
                ["partyId"] = partyId,
                ["name"] = DialogueService.LimitText(ReadString(party, "name", "Party"), 120),
                ["planks"] = ReadStringList(party, "planks").Take(4).ToList(),
                ["seatCount"] = Math.Max(0, ReadInt(party, "seatCount", 0))
            };
            var boundedResolutions = resolutions.Select(row => new Dictionary<string, object>
            {
                ["title"] = DialogueService.LimitText(ReadString(row, "title", "measurable relief"), 180),
                ["evidenceTag"] = DialogueService.LimitText(ReadString(row, "evidenceTag", "public concern"), 100),
                ["availableRoutes"] = AvailableGovernmentSpeakerRoutes(row),
                ["targetName"] = DialogueService.LimitText(ReadString(row, "targetName", ""), 120)
            }).ToList();
            if (resolutions.Any(row => ReadBool(row, "constructionAuthority", false)))
            {
                return GovernmentSpeakerReply(speakerId, partyId,
                    ReadString(boundedParty, "name", "Party") + " raises these recorded matters. "
                    + string.Join(" ", boundedResolutions.Select(RenderRecordedGovernmentSpeakerMatter)), providerCallCount: 0);
            }

            var prompt = new StringBuilder();
            prompt.AppendLine("Return only JSON with one field named reply.");
            prompt.AppendLine("You are the formally selected speaker for one political party in a fictional medieval realm's seasonal government meeting.");
            prompt.AppendLine("Speak for the party as a whole. Use two to four concise sentences. Describe only availableRoutes supplied for each recorded demand. When none are supplied, state the concern without proposing a remedy.");
            prompt.AppendLine("Blend every listed party plank naturally. Match the institution and culture without claiming exact historical quotation or using modern electoral terminology.");
            prompt.AppendLine("Use only supplied facts. Do not invent events, promise outcomes, queue actions, change votes, or alter relationships, reputation, public standing, political pressure, or native Influence.");
            prompt.AppendLine("Government: " + Json.Serialize(boundedGovernment));
            prompt.AppendLine("Party: " + Json.Serialize(boundedParty));
            prompt.AppendLine("Speaker: " + Json.Serialize(new Dictionary<string, object>
            {
                ["heroStringId"] = speakerId,
                ["name"] = DialogueService.LimitText(ReadString(speaker, "name", speakerId), 120),
                ["traits"] = ReadDictionary(speaker, "traits") ?? new Dictionary<string, object>(),
                ["cultureId"] = ReadString(speaker, "cultureId", "")
            }));
            prompt.AppendLine("Evidence-backed resolutions: " + Json.Serialize(boundedResolutions));

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["requestType"] = "government_party_speaker_statement",
                ["politicalAuthors"] = new[] { speaker },
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["correlationId"] = OperationsService.EnsureCorrelationId(payload),
                ["messages"] = DialogueService.BuildSimplePromptEnvelope(
                    "government_party_speaker_statement",
                    partyId,
                    "Return one bounded party-speaker statement as valid JSON.",
                    prompt.ToString()).Messages,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            };
            Dictionary<string, object> llm = LlmProviderService.ChatWithLlm(request);
            if (!ReadBool(llm, "ok", false))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = ReadString(llm, "error", "Government speaker statement failed."),
                    ["providerCallCount"] = 1,
                    ["speakerHeroStringId"] = speakerId,
                    ["partyId"] = partyId
                };
            Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""))
                ?? new Dictionary<string, object>();
            string reply = DialogueService.LimitText(ConversationResponseService.SanitizeVisibleReply(FirstNonEmpty(
                ReadString(parsed, "reply", ""), ReadString(llm, "content", ""))), 1600);
            return GovernmentSpeakerReply(speakerId, partyId, reply, providerCallCount: 1);
        }

        private static List<string> AvailableGovernmentSpeakerRoutes(Dictionary<string, object> row)
        {
            // A typed empty list is authoritative. Only older payloads without
            // the field use the legacy separate route strings.
            IEnumerable<string> routes = row.ContainsKey("availableRoutes") || ReadBool(row, "constructionAuthority", false)
                ? ReadStringList(row, "availableRoutes")
                : new[] { ReadString(row, "firstRoute", ""), ReadString(row, "secondRoute", "") };
            return routes.Where(route => !string.IsNullOrWhiteSpace(route)).Take(2)
                .Select(route => DialogueService.LimitText(route, 240)).ToList();
        }

        private static string RenderRecordedGovernmentSpeakerMatter(Dictionary<string, object> row)
        {
            string target = ReadString(row, "targetName", "");
            string matter = ReadString(row, "title", "measurable relief") + (target.Length == 0 ? "" : " at " + target);
            List<string> routes = ReadStringList(row, "availableRoutes");
            return matter + (routes.Count == 0 ? " has no available remedy." : ". Available remedies: " + string.Join(" or ", routes) + ".");
        }

        private static Dictionary<string, object> GovernmentSpeakerReply(string speakerId, string partyId, string reply,
            int providerCallCount) =>
            new Dictionary<string, object>
            {
                ["ok"] = !string.IsNullOrWhiteSpace(reply),
                ["reply"] = reply,
                ["speakerHeroStringId"] = speakerId,
                ["partyId"] = partyId,
                ["providerCallCount"] = providerCallCount,
                ["nativeActions"] = new ArrayList(),
                ["relationshipAssessments"] = new ArrayList(),
                ["reputationChanges"] = new ArrayList()
            };
    }
}
