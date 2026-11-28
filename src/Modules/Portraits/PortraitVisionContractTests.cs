using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private static List<Dictionary<string, object>> RunPortraitVisionContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => AddSharedPortraitSelfTest(rows, "portrait_vision_" + id, passed, id);
            Func<Action, bool> rejects = action => { try { action(); return false; } catch { return true; } };
            var settings = new Dictionary<string, object> { ["portraitProvider"] = "OpenRouter" };
            check("legacy_default_text", PortraitGenerationMethod(settings) == "text" && PortraitGenerationMethod(SettingsService.DefaultSettings()) == "text");
            check("strong_nano_default", ReadString(SettingsService.DefaultSettings(), "portraitVisionNanoGptModel", "") == "deepseek/deepseek-v4.1-flash");
            settings["portraitGenerationMethod"] = "image";
            check("image_roundtrip", PortraitGenerationMethod(Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings))) == "image");
            check("invalid_method", ValidatePortraitVisionSettings(new Dictionary<string, object> { ["portraitGenerationMethod"] = "invalid" }).Length > 0);
            check("invalid_provider", ValidatePortraitVisionSettings(new Dictionary<string, object> { ["portraitVisionProvider"] = "other" }).Length > 0);
            check("invalid_model", ValidatePortraitVisionSettings(new Dictionary<string, object> { ["portraitVisionModel"] = "" }).Length > 0);
            var description = PortraitVisionFields.ToDictionary(x => x, x => (object)"unknown");
            description["face"] = "Oval face, rounded chin, natural asymmetry";
            description["eyes"] = "Green eyes, dark straight brows";
            description["hair"] = "Short brown waves with a left side part";
            description["outfit"] = "Opaque wool coat";
            string json = Json.Serialize(description);
            check("strict_description", ParsePortraitVision(json).Count == 7);
            foreach (string invalid in new[] { "", "{}", "I cannot describe this.", "```json\n" + json + "\n```", new string('a', 16001), json.Replace("\"face\":", "\"invented\":") })
                check("invalid_description_" + rows.Count, rejects(() => ParsePortraitVision(invalid)));
            var inputs = new List<Dictionary<string, object>>();
            bool visionBound = false, noTools = false, selectedModel = false, interrupted = false;
            Func<string, Dictionary<string, object>, Dictionary<string, object>> rpc = (method, args) => {
                inputs.Add(args);
                switch (method)
                {
                    case "account/read": return TestDict("account", TestDict("type", "chatgpt"));
                    case "model/list": return TestDict("data", new[] { TestDict("model", "fixture-vision", "isDefault", true, "inputModalities", new[] { "text", "image" }), TestDict("model", "fixture-text", "inputModalities", new[] { "text" }) });
                    case "config/read": return TestDict("config", TestDict("mcp_servers", TestDict("fixture", TestDict("enabled", true))));
                    case "thread/start":
                        selectedModel = ReadString(args, "model", "") == "fixture-vision" && ReadBool(args, "ephemeral", false);
                        var config = ReadDictionary(args, "config");
                        noTools = !ReadBool(config, "features.image_generation", true) && !ReadBool(config, "features.shell_tool", true)
                            && !ReadBool(ReadDictionary(ReadDictionary(config, "mcp_servers"), "fixture"), "enabled", true);
                        return TestDict("thread", TestDict("id", "vision-thread"));
                    case "turn/start":
                        var content = ReadDictionaryList(args, "input");
                        visionBound = content.Count == 2 && ReadString(content[0], "type", "") == "localImage"
                            && ReadString(content[0], "path", "") == Path.Combine("fixture-job", "reference.png") && ReadDictionary(args, "outputSchema") != null;
                        return TestDict("turn", TestDict("id", "vision-turn"));
                    case "turn/interrupt": interrupted = true; return TestDict();
                    default: throw new InvalidOperationException("Unexpected vision method: " + method);
                }
            };
            var evidence = RunPortraitVisionExchange("fixture-job", "runtime-default", rpc, (thread, turn) => json);
            check("vision_transport", selectedModel && noTools && visionBound && interrupted && ReadString(evidence, "model", "") == "fixture-vision");
            check("selected_model", ReadString(RunPortraitVisionExchange("fixture-job", "fixture-vision", rpc, (a,b) => json), "model", "") == "fixture-vision");
            int before = inputs.Count;
            check("nonvision_rejected_before_turn", rejects(() => RunPortraitVisionExchange("fixture-job", "fixture-text", rpc, (a,b) => json))
                && inputs.Count == before + 2);
            check("unknown_model_rejected", rejects(() => RunPortraitVisionExchange("fixture-job", "missing", rpc, (a,b) => json)));
            interrupted = false;
            check("timeout_interrupt", rejects(() => RunPortraitVisionExchange("fixture-job", "fixture-vision", rpc, (a,b) => { throw new TimeoutException("fixture"); })) && interrupted);
            check("malformed_stops_pipeline", rejects(() => RunPortraitVisionExchange("fixture-job", "fixture-vision", rpc, (a,b) => "{}")));
            var payload = TestDict("ageYears", 19, "gender", "female", "cultureId", "battania", "culture", "Battanian", "clanTier", 3,
                "resolvedPortraitVision", evidence);
            string identity = PortraitVisionIdentityPrompt(payload);
            check("source_traits_age_culture", identity.Contains("19-year-old female") && identity.Contains("Green eyes") && identity.Contains("Celtic")
                && !identity.Contains("{age}") && !identity.Contains("[AGE]"));
            check("outfit_not_used_for_noble", !identity.Contains("Opaque wool coat"));
            check("no_missing_vision_fallback", rejects(() => PortraitVisionIdentityPrompt(TestDict())));
            foreach (var model in OpenRouterImageModels)
                check("openrouter_text_" + model, !BuildOpenRouterImagePayload(model, "written traits", null, "768x1024").ContainsKey("input_references"));
            check("nano_text", !BuildNanoPortraitTextPayload("gpt-image-1.5", "written traits", "768x1024").ContainsKey("imageDataUrl"));
            var nanoModels = new[] { TestDict("id", DefaultNanoPortraitVisionModel, "capabilities", TestDict("vision", true, "structured_output", true)),
                TestDict("id", "text-only", "capabilities", TestDict("vision", false)), TestDict("id", "unknown-modality") };
            check("nano_catalog_filter", FilterNanoPortraitVisionModels(TestDict("data", nanoModels)).Count == 1);
            byte[] png = LinuxImageCodec.Fixture(32, 24);
            // Missing model settings must choose the stronger catalog-validated default.
            var nanoSettings = TestDict("nanoGptApiKey", "fixture-text-key", "portraitNanoGptApiKey", "fixture-image-key");
            bool nanoWire = false;
            var nanoEvidence = DescribeNanoPortraitSource(nanoSettings, png, () => nanoModels.ToList(), (url, key, bodyJson, s) => {
                var body = Json.Deserialize<Dictionary<string, object>>(bodyJson);
                var messages = ReadDictionaryList(body, "messages");
                var content = ReadDictionaryList(messages[1], "content");
                nanoWire = url == NanoPortraitVisionChatUrl && key == "fixture-text-key" && !body.ContainsKey("provider") && !body.ContainsKey("tools")
                    && ReadString(body, "model", "") == "deepseek/deepseek-v4.1-flash" && ReadString(ReadDictionary(body, "response_format"), "type", "") == "json_object"
                    && !ReadDictionary(body, "response_format").ContainsKey("json_schema")
                    && ReadString(ReadDictionary(content[1], "image_url"), "detail", "") == "high"
                    && DecodeImageBase64(ReadString(ReadDictionary(content[1], "image_url"), "url", "")).SequenceEqual(png);
                return Json.Serialize(TestDict("choices", new[] { TestDict("finish_reason", "stop", "message", TestDict("content", json)) }));
            });
            check("nano_subscription_transport", nanoWire && ReadBool(nanoEvidence, "subscriptionOnly", false) && ReadString(nanoEvidence, "provider", "") == "NanoGPT");
            foreach (bool structured in new[] { true, false })
            {
                bool formatBound = false;
                var alternativeSettings = new Dictionary<string, object>(nanoSettings) { ["portraitVisionNanoGptModel"] = "fixture-other-vision" };
                DescribeNanoPortraitSource(alternativeSettings, png,
                    () => new List<Dictionary<string, object>> { TestDict("id", "fixture-other-vision", "capabilities", TestDict("vision", true, "structured_output", structured)) },
                    (url, key, bodyJson, s) => {
                        var body = Json.Deserialize<Dictionary<string, object>>(bodyJson);
                        formatBound = structured ? ReadString(ReadDictionary(body, "response_format"), "type", "") == "json_schema"
                            && ReadDictionary(ReadDictionary(body, "response_format"), "json_schema") != null : !body.ContainsKey("response_format");
                        return Json.Serialize(TestDict("choices", new[] { TestDict("finish_reason", "stop", "message", TestDict("content", json)) }));
                    });
                check("nano_other_format_" + structured, formatBound);
            }
            bool sent = false;
            check("nano_not_included_blocks_inference", rejects(() => DescribeNanoPortraitSource(nanoSettings, png, () => new List<Dictionary<string, object>>(), (a,b,c,d) => { sent = true; return ""; })) && !sent);
            check("nano_no_key", rejects(() => DescribeNanoPortraitSource(TestDict(), png, () => nanoModels.ToList(), (a,b,c,d) => "")));
            foreach (string invalid in new[] { "{}", Json.Serialize(TestDict("choices", new[] { TestDict("finish_reason", "length", "message", TestDict("content", json)) })),
                Json.Serialize(TestDict("choices", new[] { TestDict("finish_reason", "stop", "message", TestDict("content", "{}")) })),
                Json.Serialize(TestDict("choices", new[] { TestDict("finish_reason", "stop", "message", TestDict("content", json, "refusal", "refused")) })) })
                check("nano_invalid_response_" + rows.Count, rejects(() => DescribeNanoPortraitSource(nanoSettings, png, () => nanoModels.ToList(), (a,b,c,d) => invalid)));
            check("nano_quota_no_fallback", rejects(() => DescribeNanoPortraitSource(nanoSettings, png, () => nanoModels.ToList(), (a,b,c,d) => { throw new InvalidOperationException("429 subscription quota"); })));
            foreach (string model in AtlasImageModels)
            {
                var body = BuildAtlasPortraitTextPayload(model, "written traits", "768x1024", "cartoon");
                check("atlas_text_" + model, !new[] { "image", "images", "image_url", "image_urls" }.Any(body.ContainsKey)
                    && (ReadString(body, "model", "") != model || model == AtlasModelFluxKontextDev) && ReadString(body, "prompt", "") == "written traits");
            }
            bool imageHasNoReference = false, imageModelUnspecified = false;
            Func<string, Dictionary<string, object>, Dictionary<string, object>> imageRpc = (method, args) => {
                switch (method) {
                    case "account/read": return TestDict("account", TestDict("type", "chatgpt"));
                    case "modelProvider/capabilities/read": return TestDict("imageGeneration", true);
                    case "config/read": return TestDict("config", TestDict());
                    case "skills/list": return TestDict("data", new[] { TestDict("skills", new[] { TestDict("name", "imagegen", "scope", "system", "enabled", true, "path", Path.Combine(Path.GetTempPath(), "SKILL.md")) }) });
                    case "thread/start": imageModelUnspecified = !args.ContainsKey("model"); return TestDict("thread", TestDict("id", "image-thread"));
                    case "turn/start": var content = ReadDictionaryList(args, "input"); imageHasNoReference = content.Count == 2
                        && content.All(x => ReadString(x, "type", "") != "localImage") && ReadString(content[1], "text", "").Contains("written description alone")
                        && !args.ContainsKey("model"); return TestDict("turn", TestDict("id", "image-turn"));
                    case "turn/interrupt": return TestDict();
                    default: throw new InvalidOperationException(method);
                }
            };
            RunCodexImageExchange("fixture-job", "written traits", "768x1024", imageRpc, (a,b) => TestDict("type", "imageGeneration", "status", "completed"), false);
            check("codex_text_no_reference_runtime_image_model", imageHasNoReference && imageModelUnspecified);
            CheckLocalTextPortraitContract(settings, payload, description, png, check, rejects);
            // Existing RunAdultPortraitContractTests supplies the full threshold/handoff/failure contracts.
            check("adult_threshold_unchanged", !UsesAdultPortraitClothingEdit(TestDict("promptPurpose", "portrait", "gender", "female", "ageYears", 18, "physicalConfidence", TestDict("score", 60)))
                && UsesAdultPortraitClothingEdit(TestDict("promptPurpose", "portrait", "gender", "female", "ageYears", 18, "physicalConfidence", TestDict("score", 61)))
                && !UsesAdultPortraitClothingEdit(TestDict("promptPurpose", "portrait", "gender", "female", "ageYears", 17, "physicalConfidence", TestDict("score", 100))));
            return rows;
        }
        private static void CheckLocalTextPortraitContract(Dictionary<string, object> settings,
            Dictionary<string, object> payload, Dictionary<string, object> description, byte[] source,
            Action<string, bool> check, Func<Action, bool> rejects)
        {
            settings["portraitGenerationMethod"] = LocalTextPortraitMethod;
            var restored = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
            check("local_text_roundtrip_is_independent", ValidatePortraitVisionSettings(restored) == ""
                && PortraitGenerationMethod(restored) == LocalTextPortraitMethod
                && ResolveImageGenerationProfile(restored, "portrait").Provider == "OpenRouter");
            var normal = ResolveImageGenerationProfile(restored, "portrait", LocalModelImageProviderId);
            var subject = new Dictionary<string, object>(payload)
            {
                ["resolvedPortraitPhysique"] = TestDict("bodyLayer", "Fixture authorized physique"),
                ["physicalConfidence"] = TestDict("score", 72)
            };
            string template = BuildLocalTextPortraitTemplate(subject, "preview");
            string confidence = ExpandPortraitIdentityTokens(subject,
                PromptService.LoadPromptTemplate(SelectPortraitPhysicalConfidencePromptFile(subject)).Trim());
            check("local_text_reuses_normal_text_prompt", template.Contains("19-year-old female")
                && confidence.Length > 0 && template.Contains(confidence) && template.Contains("Fixture authorized physique")
                && !template.Contains(LocalTextVisionPlaceholder("outfit"))
                && PortraitVisionFields.Where(x => x != "outfit").All(x => template.Contains(LocalTextVisionPlaceholder(x))));
            var resident = new Dictionary<string, object>(subject)
            {
                ["campaignId"] = "fixture", ["heroStringId"] = "resident",
                ["nativeCharacterSnapshot"] = TestDict("schema", "reign-native-portrait-snapshot-v1",
                    "campaignId", "fixture", "heroStringId", "resident", "portraitSourceProfile", ResidentOutfitRenderContract)
            };
            check("local_text_includes_resident_outfit_once", BuildLocalTextPortraitTemplate(resident, "preview")
                .Split(LocalTextVisionPlaceholder("outfit"), StringSplitOptions.None).Length == 2);
            var residentSnapshot = ReadDictionary(resident, "nativeCharacterSnapshot");
            residentSnapshot["age"] = 21;
            residentSnapshot["tavernHouse"] = TestDict("schema", 1, "heroStringId", "resident",
                "castId", "reign_tavern_fixture", "madam", true);
            string tavern = BuildLocalTextPortraitTemplate(resident, "preview");
            string wardrobe = ExpandPortraitIdentityTokens(resident, PromptService.LoadPromptTemplate("tavern_house_portrait_clothing.txt").Trim())
                .Replace("[TAVERN ROLE]", "madam");
            check("local_text_tavern_clothing_in_single_pass", wardrobe.Length > 0 && tavern.Contains(wardrobe)
                && !tavern.Contains("Retain exactly the clothes"));

            using var request = BuildLocalTextPortraitRequest(normal, source, template);
            var body = Json.Deserialize<Dictionary<string, object>>(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            check("local_text_one_pass_loopback_wire", request.Method.Method == "POST"
                && request.RequestUri.AbsoluteUri == "http://127.0.0.1:18500/v1/portraits/generations"
                && request.Headers.Authorization == null && body.Count == 4 && !body.ContainsKey("clothing_edit")
                && ReadString(body, "vision_prompt", "") == PortraitVisionInstructions
                && ReadString(body, "prompt_template", "") == template
                && DecodeImageBase64(ReadString(body, "imageDataUrl", "")).SequenceEqual(source)
                && ReadString(ReadDictionary(body, "image_settings"), "model", "") == DefaultLocalModelImageModel);

            string effective = template;
            foreach (var field in PortraitVisionFields) effective = effective.Replace(LocalTextVisionPlaceholder(field), (string)description[field]);
            byte[] output = LinuxImageCodec.Fixture(768, 1024);
            string image = Convert.ToBase64String(output);
            var response = TestDict("model", DefaultLocalModelImageModel, "vision_model", LocalTextPortraitVisionModel,
                "description", description, "prompt", effective, "data", new[] { TestDict("b64_json", image) },
                "timing", TestDict("vision_ms", 20, "image_ms", 40));
            LocalTextPortraitResult captured = ReadLocalTextPortraitResponse(Json.Serialize(response), template, source, 100, 200);
            check("local_text_uses_verified_effective_prompt", captured.Normal.Ok && captured.Normal.ImageBytes.SequenceEqual(output)
                && captured.EffectivePrompt == effective && ReadString(captured.Vision, "sourceSha256", "") == Sha256Hex(source)
                && ReadLong(captured.Vision, "durationMs", 0) == 20 && captured.ImageMs == 40);
            response["prompt"] = "altered";
            check("local_text_altered_prompt_refused", rejects(() => ReadLocalTextPortraitResponse(
                Json.Serialize(response), template, source, 100, 200)));
            response["prompt"] = effective;
            response["vision_model"] = "another-model";
            check("local_text_unexpected_vision_model_refused", rejects(() => ReadLocalTextPortraitResponse(
                Json.Serialize(response), template, source, 100, 200)));
            response["vision_model"] = LocalTextPortraitVisionModel;
            response["data"] = new object[] { TestDict("b64_json", image), null };
            check("local_text_extra_image_refused", rejects(() => ReadLocalTextPortraitResponse(
                Json.Serialize(response), template, source, 100, 200)));
            response["data"] = new[] { TestDict("b64_json", "invalid") };
            check("local_text_invalid_png_refused", rejects(() => ReadLocalTextPortraitResponse(
                Json.Serialize(response), template, source, 100, 200)));
        }
    }
}
