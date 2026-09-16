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
            var targets = new Dictionary<string, string>
            {
                ["ConversationBehavior.cs"] = "RequestPortrait(conversationHero)",
                ["ReignIndividualChatScreenManager.cs"] = "RequestPortrait(hero)",
                ["ReignPartyChatScreenManager.cs"] = "RequestPortrait(hero)",
                ["ReignSocialEventScreenManager.cs"] = "RequestPortrait(hero)",
                ["ReignPartyChatScreenVM.cs"] = "RequestPortrait(attendee.Hero)",
                ["ReignSocialEventScreenVM.cs"] = "RequestPortrait(attendee.Hero)",
                ["ReignCourtPetitionScreenVM.cs"] = "ReignRulerPetitionSceneClient.Create(",
                ["ReignCourtNobleMatterScreenVM.cs"] = "ReignRulerPetitionSceneClient.Create(",
                ["ReignCourtLifeScreenVM.cs"] = "ReignRulerPetitionSceneClient.Create(",
                ["ReignBetaDebugActions.cs"] = "RequestPortrait(hero)",
                ["DiagnosticsRunner.cs"] = "RequestPortrait(Hero.MainHero)"
            };
            var failures = new List<string>();
            foreach (var target in targets)
            {
                string path = VerificationSourceLocator.ResolveUnique(clientRoot, target.Key, "src");
                string source = File.Exists(path) ? File.ReadAllText(path) : "";
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
                int method = source.IndexOf("private static void OpenPortraitRequestOverlay", StringComparison.Ordinal);
                if (method < 0) method = source.IndexOf("private void OpenPortraitRequestOverlay", StringComparison.Ordinal);
                if (method >= 0)
                {
                    int end = source.IndexOf("\n        private ", method + 20, StringComparison.Ordinal);
                    string body = end > method ? source.Substring(method, end - method) : source.Substring(method);
                    if (body.Contains("Close(false)") || body.Contains("EnterExternalOverlayMode()") || body.Contains("Encyclopedia"))
                        failures.Add(target.Key + ": generation changes screen or focus");
                }
            }
            string portraitRoot = Path.Combine(clientRoot, "src", "Modules", "Portraits");
            foreach (string path in Directory.GetFiles(portraitRoot, "*.cs", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(path), source = File.ReadAllText(path);
                if (source.Contains("GeneratePortraitProductAsync(") && name != "NanoGptClient.cs" && name != "ReignPortraitBridge.cs")
                    failures.Add(name + ": bypasses coordinator");
                if (source.Contains("PortraitRequestRegistry.Request(")) failures.Add(name + ": still arms render capture");
            }
            return new Dictionary<string, object> { ["ok"] = failures.Count == 0, ["entrypoints"] = targets.Keys.ToArray(), ["failures"] = failures.ToArray() };
        }
    }
}
