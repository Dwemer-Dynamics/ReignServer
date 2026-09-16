using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCodexImageProviderContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> check = (id, ok, detail) => AddSharedPortraitSelfTest(rows, "codex_image_" + id, ok, detail);
            Func<Action, bool> rejects = action => { try { action(); return false; } catch { return true; } };
            var settings = new Dictionary<string, object> { ["portraitProvider"] = "Codex SDK", ["sceneryProvider"] = "Codex",
                ["sceneryImageProfileInitialized"] = true, ["adultPortraitProvider"] = "AtlasCloud", ["adultSceneryProvider"] = "NanoGPT" };
            NormalizePortraitImageSettings(settings);
            var restored = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
            check("routing_roundtrip", new[] { "portrait", "scenery", "castle_scene", "memory", "social_event" }.All(purpose =>
                IsNormalCodexImageProfile(ResolveImageGenerationProfile(restored, purpose))
                && ResolveImageGenerationProfile(restored, purpose).Provider == CodexImageProvider)
                && ResolveImageGenerationProfile(restored, "adultPortrait").Provider == "AtlasCloud"
                && ResolveImageGenerationProfile(restored, "adultScenery").Provider == "NanoGPT", "Normal purposes survive settings serialization; adult providers remain independent.");
            foreach (string purpose in new[] { "adultPortrait", "adultScenery", "ADULTPORTRAIT", "adult_scene" })
            {
                var blocked = ResolveImageGenerationProfile(settings, purpose, "Codex");
                var call = CallImageProvider(settings, blocked, "Must never be submitted", null, "");
                check("reject_" + purpose, !call.Ok && call.Error.Contains("adult"), "Forged adult overrides fail before reference decoding or provider startup.");
            }
            check("settings_gate", !string.IsNullOrEmpty(ValidateCodexImageSettings(new Dictionary<string, object> { ["adultSceneryProvider"] = "codex_subscription" }))
                && !string.IsNullOrEmpty(ValidateCodexImageSettings(new Dictionary<string, object> { ["adultPortraitProvider"] = "Codex" }))
                && string.IsNullOrEmpty(ValidateCodexImageSettings(settings)), "Adult settings are rejected before persistence; normal settings remain valid.");
            check("normal_failure_queue_release", !GenerateCodexImage(settings, ResolveImageGenerationProfile(settings, "portrait"), "normal", new byte[] { 1 }, "").Ok
                && CodexImageGate.CurrentCount == 1, "Malformed references fail without a provider and release the queue slot.");

            string root = Path.Combine(Path.GetTempPath(), "reign-codex-image-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                byte[] png;
                png = LinuxImageCodec.Fixture(32, 24);
                File.WriteAllBytes(Path.Combine(root, "reference.png"), png);
                var item = new Dictionary<string, object> { ["type"] = "imageGeneration", ["status"] = "completed", ["result"] = Convert.ToBase64String(png) };
                check("base64_png", ReadCodexImageResult(item, root).SequenceEqual(png), "Native base64 result preserves exact PNG bytes.");
                item["result"] = "data:image/png;base64," + Convert.ToBase64String(png);
                check("data_url_png", ReadCodexImageResult(item, root).SequenceEqual(png), "Explicit PNG data URL is accepted.");
                item["result"] = ""; item["savedPath"] = Path.Combine(root, "reference.png");
                check("confined_file_png", ReadCodexImageResult(item, root).SequenceEqual(png), "The file fallback requires a local, job-confined PNG.");
                foreach (string path in new[] { Path.Combine(root, "..", "outside.png"), Path.Combine(root + "-neighbor", "image.png"), "relative.png", Path.Combine(root, "fake.jpg") })
                {
                    item["savedPath"] = path;
                    check("reject_path_" + rows.Count, rejects(() => ReadCodexImageResult(item, root)), "Traversal, prefix siblings, relative paths and non-PNG files are rejected.");
                }
                foreach (string data in new[] { "https://example.invalid/image.png", "not-base64", Convert.ToBase64String(new byte[] { 1, 2, 3 }) })
                {
                    item["result"] = data;
                    check("reject_data_" + rows.Count, rejects(() => ReadCodexImageResult(item, root)), "Text, remote URLs and non-image bytes are never used as image results.");
                }
                item["result"] = Convert.ToBase64String(png); item["status"] = "failed";
                check("failed_item", rejects(() => ReadCodexImageResult(item, root)), "Image status must be completed even when bytes exist.");
                item["status"] = "completed"; item["failure"] = new Dictionary<string, object> { ["type"] = "usageLimitExceeded" };
                check("usage_limit", rejects(() => ReadCodexImageResult(item, root)), "Usage-limit failure cannot be imported as a success.");
                item.Remove("failure");
                byte[] oversized = (byte[])png.Clone(); oversized[16] = 0x7f;
                check("pixel_limit", rejects(() => ValidateCodexPng(oversized)), "Dimension bounds are enforced before image decoding.");
                check("truncated_png", rejects(() => ValidateCodexPng(png.Take(33).ToArray())), "A PNG signature alone is insufficient.");

                var calls = new List<string>();
                Dictionary<string, object> thread = null, turn = null;
                string mode = "ok";
                Func<string, Dictionary<string, object>, Dictionary<string, object>> rpc = (method, parameters) => {
                    calls.Add(method);
                    switch (method)
                    {
                        case "account/read": return new Dictionary<string, object> { ["account"] = new Dictionary<string, object> { ["type"] = mode == "api" ? "apiKey" : "chatgpt" } };
                        case "modelProvider/capabilities/read": return new Dictionary<string, object> { ["imageGeneration"] = mode != "unavailable" };
                        case "config/read": return new Dictionary<string, object> { ["config"] = new Dictionary<string, object> {
                            ["mcp_servers"] = new Dictionary<string, object> { ["fixture.with.dot"] = new Dictionary<string, object> { ["command"] = "never-run", ["enabled"] = true } } } };
                        case "skills/list": return new Dictionary<string, object> { ["data"] = new[] { new Dictionary<string, object> { ["skills"] = new[] {
                            new Dictionary<string, object> { ["name"] = "imagegen", ["scope"] = mode == "untrusted_skill" ? "repo" : "system", ["enabled"] = true, ["path"] = Path.Combine(root, "SKILL.md") } } } } };
                        case "thread/start": thread = parameters; return new Dictionary<string, object> { ["thread"] = new Dictionary<string, object> { ["id"] = "fixture-thread" } };
                        case "turn/start": turn = parameters; return new Dictionary<string, object> { ["turn"] = new Dictionary<string, object> { ["id"] = "fixture-turn" } };
                        default: return new Dictionary<string, object>();
                    }
                };
                var received = RunCodexImageExchange(root, "Retain face and clothing", "768x1024", rpc, (threadId, turnId) => item);
                var config = ReadDictionary(thread, "config");
                var policy = ReadDictionary(turn, "sandboxPolicy");
                var inputs = ReadDictionaryList(turn, "input");
                check("exchange_identity", ReferenceEquals(received, item) && inputs.Any(x => ReadString(x, "type", "") == "localImage" && ReadString(x, "path", "") == Path.Combine(root, "reference.png"))
                    && inputs.Any(x => ReadString(x, "type", "") == "skill")
                    && inputs.Any(x => ReadString(x, "text", "").Contains("Retain face and clothing")), "The production exchange sends an explicit system skill, exact reference and composed brief.");
                check("exchange_isolation", ReadString(thread, "approvalPolicy", "") == "never" && ReadBool(thread, "ephemeral", false)
                    && !ReadBool(config, "features.shell_tool", true) && !ReadBool(config, "features.multi_agent", true)
                    && !ReadBool(ReadDictionary(ReadDictionary(config, "mcp_servers"), "fixture.with.dot"), "enabled", true)
                    && !ReadBool(policy, "networkAccess", true) && ReadBool(policy, "excludeTmpdirEnvVar", false)
                    && ReadString(thread, "cwd", "") == root && !thread.ContainsKey("model") && !turn.ContainsKey("model"),
                    "The image model is runtime-owned; the isolated thread disables inherited MCP, shell, agents and network.");
                check("interrupt_on_success", calls.Last() == "turn/interrupt", "Turn cleanup is requested after success.");
                calls.Clear();
                bool timedOut = rejects(() => RunCodexImageExchange(root, "normal", "", rpc, (a, b) => { throw new TimeoutException("fixture timeout"); }));
                check("interrupt_on_timeout", timedOut && calls.Last() == "turn/interrupt", "A failed/timed-out exchange still requests interruption.");
                foreach (string refusal in new[] { "api", "unavailable", "untrusted_skill" })
                {
                    mode = refusal; calls.Clear();
                    bool rejected = rejects(() => RunCodexImageExchange(root, "normal", "", rpc, (a, b) => item));
                    check("preflight_" + refusal, rejected && !calls.Contains("turn/start"), "Auth, capability and official-skill failures cannot consume an image turn.");
                }
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                File.WriteAllText(Path.Combine(root, "nested", "fixture.txt"), "cleanup");
                DeleteCodexImageJob(root);
                check("cleanup", !Directory.Exists(root), "Only the isolated job tree is removed after helper shutdown.");
            }
            finally { if (Directory.Exists(root)) DeleteCodexImageJob(root); }
            return rows;
        }

        // Explicit live-llm suite only; never called by aggregate all/quick/offline image checks.
        private static void RunCodexImageLiveVerification(List<Dictionary<string, object>> checks, string sandbox, Dictionary<string, object> options)
        {
            string output = Path.Combine(sandbox, "codex-images");
            Directory.CreateDirectory(output);
            byte[] reference;
            reference = LinuxImageCodec.Fixture(256, 256);
            File.WriteAllBytes(Path.Combine(output, "reference.png"), reference);
            string[] purposes = { "portrait", "scenery", "social_event" };
            string[] briefs = {
                "Use the reference only as a muted color palette. A fictional adult medieval merchant, fully clothed in a simple tunic and trousers, full body, neutral pose, photorealistic, no text or frame.",
                "Use the reference only as a muted color palette. A peaceful medieval stone castle courtyard in morning light, wide landscape composition, photorealistic, no text or frame.",
                "Use the reference only as a muted color palette. Fully clothed adult medieval villagers share a harvest feast in a town square, wide landscape, photorealistic, no text or frame."
            };
            var evidence = new List<Dictionary<string, object>>();
            int cap = Math.Min(3, Math.Max(1, ReadInt(options, "liveCaseCap", 1)));
            for (int index = 0; index < cap && !ShouldCancelVerification(); index++)
            {
                var profile = ResolveImageGenerationProfile(new Dictionary<string, object>(), purposes[index], CodexImageProvider);
                var call = GenerateCodexImage(LoadSettings(), profile, briefs[index], reference, index == 0 ? "768x1024" : "2048x1152", ShouldCancelVerification);
                string imagePath = Path.Combine(output, purposes[index] + ".png");
                if (call.Ok) File.WriteAllBytes(imagePath, call.ImageBytes);
                var row = new Dictionary<string, object> { ["purpose"] = purposes[index], ["profile"] = profile.Name,
                    ["provider"] = CodexImageProvider, ["model"] = call.Model, ["adapter"] = call.Adapter, ["ok"] = call.Ok,
                    ["durationMs"] = call.DurationMs, ["error"] = call.Error, ["imagePath"] = call.Ok ? imagePath : "",
                    ["imageSha256"] = call.Ok ? Sha256Hex(call.ImageBytes) : "", ["referenceSha256"] = Sha256Hex(reference),
                    ["visualAcceptance"] = "pending human review; synthetic palette reference does not prove native likeness" };
                evidence.Add(row);
                AddVerificationCheck(checks, "live_llm.codex_image_" + purposes[index], "portraits_images", call.Ok,
                    call.Ok ? "Codex SDK returned a validated PNG; visual acceptance remains separate." : call.Error, row);
                if (!call.Ok) break; // Do not spend more usage after a failed compatibility attempt.
            }
            WriteJsonObject(Path.Combine(output, "report.json"), new Dictionary<string, object> { ["schema"] = "reign-codex-image-compatibility-v1",
                ["providerCalls"] = evidence.Count, ["caseCap"] = cap, ["campaignMutations"] = 0, ["cases"] = evidence });
        }
    }
}
