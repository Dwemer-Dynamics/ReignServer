using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> AuditBackgroundPortraitEntrypoints(string clientRoot)
        {
            // Keyed by the client type that owns each entry point, wherever its source file lives.
            var targets = new Dictionary<string, string>
            {
                ["ConversationBehavior"] = "RequestPortrait(conversationHero)",
                ["ReignIndividualChatScreenManager"] = "RequestPortrait(hero)",
                ["ReignPartyChatScreenManager"] = "RequestPortrait(hero)",
                ["ReignSocialEventScreenManager"] = "RequestPortrait(hero)",
                ["ReignPartyChatScreenVM"] = "RequestPortrait(attendee.Hero)",
                ["ReignSocialEventScreenVM"] = "RequestPortrait(attendee.Hero)",
                ["ReignCourtPetitionScreenVM"] = "ReignRulerPetitionSceneClient.Create(",
                ["ReignCourtNobleMatterScreenVM"] = "ReignRulerPetitionSceneClient.Create(",
                ["ReignCourtLifeScreenVM"] = "ReignRulerPetitionSceneClient.Create(",
                ["ReignBetaDebugActions"] = "RequestPortrait(hero)",
                ["DiagnosticsRunner"] = "RequestPortrait(Hero.MainHero)"
            };
            var failures = new List<string>();
            foreach (var target in targets)
            {
                string source = VerificationSourceLocator.ReadClientTypeSourceText(clientRoot, target.Key);
                if (!source.Contains(target.Value)) failures.Add(target.Key + ": missing background entry");
                // Court scenes assemble existing or free native references into one image.
                // Opening an audience must never fan out paid portrait generation.
                if (target.Value == "ReignRulerPetitionSceneClient.Create(")
                {
                    if (!source.Contains("_scene.Refresh(") && !source.Contains("_scene?.Refresh("))
                        failures.Add(target.Key + ": missing shared audience scene refresh");
                    if (source.Contains("RequestPortrait(") || source.Contains("TryRequestPortrait("))
                        failures.Add(target.Key + ": court scene initiates individual portrait generation");
                }
                if (source.Contains("private static void OpenPortraitRequestOverlay", StringComparison.Ordinal)
                    || source.Contains("private void OpenPortraitRequestOverlay", StringComparison.Ordinal))
                {
                    string body = VerificationSourceLocator.ReadClientMemberSourceText(
                        clientRoot, target.Key, "OpenPortraitRequestOverlay");
                    if (body.Contains("Close(false)") || body.Contains("EnterExternalOverlayMode()") || body.Contains("Encyclopedia"))
                        failures.Add(target.Key + ": generation changes screen or focus");
                }
            }
            // Only the provider client and the coordinator bridge may start generation, anywhere in the client.
            foreach (KeyValuePair<string, string> file in VerificationSourceLocator.ReadClientSourceFiles(clientRoot))
            {
                string name = Path.GetFileName(file.Key), source = file.Value;
                if (source.Contains("GeneratePortraitProductAsync(")
                    && !VerificationSourceLocator.SourceDeclaresType(source, "NanoGptClient")
                    && !VerificationSourceLocator.SourceDeclaresType(source, "ReignPortraitBridge"))
                    failures.Add(name + ": bypasses coordinator");
                if (source.Contains("PortraitRequestRegistry.Request(")) failures.Add(name + ": still arms render capture");
            }
            return new Dictionary<string, object> { ["ok"] = failures.Count == 0, ["entrypoints"] = targets.Keys.ToArray(), ["failures"] = failures.ToArray() };
        }
    }
}
