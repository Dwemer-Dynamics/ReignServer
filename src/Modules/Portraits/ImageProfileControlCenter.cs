using System.Linq;

namespace ReignBetaServer
{
    internal static partial class PortraitsService
    {
        private static string ImageProfileOptions(string[] values)
        {
            return string.Join("", values.Select(value => "<option value='" + value + "'>" + value + "</option>"));
        }

        private static string ImageProfileCard(string prefix, string title, string hint)
        {
            return @"        <div class='card imageProfileCard'>
          <h2>@TITLE@</h2>
          <p class='hint'>@HINT@</p>
          <label>Image provider
            <select id='@PROFILE@Provider' onchange='updateImageProfileUi()'>@PROVIDERS@</select>
          </label>
          @CODEX@
          <div id='@PROFILE@LocalModelProfileSettings' style='display:none'>
            <label>Local image model <input id='@PROFILE@LocalModelImageModel' value='@LOCAL_MODEL@' readonly></label>
            <label>Output size <input id='@PROFILE@LocalModelImageSize' placeholder='@LOCAL_SIZE@' pattern='[0-9]{3,4}x[0-9]{3,4}' spellcheck='false'></label>
            <p class='hint'>Enter width x height in pixels. Both must be multiples of 32 from 256 to 2048.</p>
            <label>Inference steps <input id='@PROFILE@LocalModelSteps' type='number' min='1' max='100' step='1'></label>
            <label>Guidance scale <input id='@PROFILE@LocalModelCfg' type='number' min='0.1' max='20' step='any'></label>
            <details>
              <summary>Local Model advanced settings</summary>
              <label>Sampler <select id='@PROFILE@LocalModelSampler'>@LOCAL_SAMPLERS@</select></label>
              <label>Scheduler <select id='@PROFILE@LocalModelScheduler'>@LOCAL_SCHEDULERS@</select></label>
              <label>Negative prompt <textarea id='@PROFILE@LocalModelNegativePrompt' rows='4' maxlength='16000'></textarea></label>
              <label>Reference resolution <input id='@PROFILE@LocalModelReferenceResolution' type='number' min='0' max='2048' step='32'></label>
              <p class='hint'>For reference edits, use 0 to keep the reference resolution or a multiple of 32 from 256 to 2048.</p>
              <label>Fit output to reference <input id='@PROFILE@LocalModelFitReference' type='checkbox'></label>
              <p class='hint'>Fitted edits can exceed the canvas dimensions. Reduce reference resolution if an unusually shaped image is too large. Edits that require an exact output size use their fixed canvas.</p>
              <label>Seed <input id='@PROFILE@LocalModelSeed' inputmode='numeric' pattern='[0-9]{1,20}' maxlength='20' placeholder='Random'></label>
            </details>
          </div>
          <div id='@PROFILE@OpenRouterProfileSettings' style='display:none'>
            <label>OpenRouter image model <select id='@PROFILE@OpenRouterImageModel'>@OPENROUTER@</select></label>
            <p class='hint'>Uses your saved OpenRouter key from API Settings. Character portraits follow the selected generation method; image edits retain their reference.</p>
          </div>
          <div id='@PROFILE@NanoGptProfileSettings'>
            <label>NanoGPT image model
              <select id='@PROFILE@NanoGptImageModel'>@NANO@</select>
            </label>
            <p id='@PROFILE@NanoPresetHint' class='hint'>Preset request shape: model-specific NanoGPT route.</p>
          </div>
          <div id='@PROFILE@AtlasProfileSettings'>
            <label>AtlasCloud image model
              <select id='@PROFILE@AtlasImageModel'>@ATLAS@</select>
            </label>
            <p id='@PROFILE@AtlasPresetHint' class='hint'>Preset request shape: model-specific AtlasCloud route.</p>
            <details>
              <summary>WAN Negative Prompt</summary>
              <label>Prompt preset
                <select id='@PROFILE@AtlasWanNegativePromptPreset' onchange='applyWanNegativePromptPreset(""@PROFILE@"")'>
                  <option value=''>Current / custom</option><option value='photoreal'>Strong photorealism</option><option value='antiArt'>Anti-art and CGI only</option><option value='minimal'>Minimal safety and text cleanup</option>
                </select>
              </label>
              <label>Negative prompt <textarea id='@PROFILE@AtlasWanNegativePrompt' rows='6' oninput='markWanNegativePromptCustom(""@PROFILE@"")'></textarea></label>
            </details>
          </div>
          <div id='@PROFILE@RequestParameters'>
            <h3>Request Parameters</h3>
            <p class='hint'>Used only by models that accept them.</p>
            <label>Image strength <input id='@PROFILE@ImageStrength' type='number' min='0.4' max='1' step='0.01'></label>
            <label>Inference steps <input id='@PROFILE@InferenceSteps' type='number' min='10' max='50'></label>
            <label>Guidance scale <input id='@PROFILE@GuidanceScale' type='number' min='1' max='10' step='0.1'></label>
          </div>
        </div>
"
                .Replace("@CODEX@", prefix.StartsWith("adult") ? "" : @"<div id='@PROFILE@CodexProfileSettings' style='display:none'>
            <label>Codex image model <select id='@PROFILE@CodexImageModel' disabled><option value='runtime-default'>Runtime default</option></select></label>
            <p class='hint'>Uses your signed-in Codex subscription. Connect Codex in API Settings. Normal images only; one generation at a time.</p>
          </div>")
                .Replace("@PROFILE@", prefix).Replace("@TITLE@", title).Replace("@HINT@", hint)
                .Replace("@PROVIDERS@", ImageProfileOptions(new[] { "NanoGPT", "AtlasCloud", "OpenRouter" })
                    + "<option value='LocalModel'>Local Model</option>"
                    + (prefix.StartsWith("adult") ? "" : "<option value='Codex'>Codex SDK (ChatGPT)</option>"))
                .Replace("@LOCAL_MODEL@", DefaultLocalModelImageModel)
                .Replace("@LOCAL_SIZE@", prefix == "scenery" || prefix == "adultScenery" ? "1536x1024" : "1024x1536")
                .Replace("@LOCAL_SAMPLERS@", ImageProfileOptions(LocalModelImageSamplers))
                .Replace("@LOCAL_SCHEDULERS@", ImageProfileOptions(LocalModelImageSchedulers))
                .Replace("@OPENROUTER@", ImageProfileOptions(OpenRouterImageModels)).Replace("@NANO@", ImageProfileOptions(NanoImageModels)).Replace("@ATLAS@", ImageProfileOptions(AtlasImageModels));
        }

        internal static string ImageProfileCards()
        {
            return @"<style>
                .imageProfileCard { background:#121211FF; border:1px solid #7E6A4DFF; color:#C5BDAFFF; box-shadow:none; font-family:Georgia,serif; min-width:0; }
                .imageProfileCard h2,.imageProfileCard h3 { color:#A88A54FF; text-shadow:none; }
                .imageProfileCard .hint { color:#8A8883FF; }
                .imageProfileCard label { color:#C5BDAFFF; grid-template-columns:minmax(100px,160px) minmax(0,1fr); }
                .imageProfileCard input,.imageProfileCard select,.imageProfileCard textarea { background:#0B0B0BFF; color:#C5BDAFFF; border-color:#352C20FF; min-width:0; box-shadow:none; }
                .imageProfileCard input:focus,.imageProfileCard select:focus,.imageProfileCard textarea:focus { border-color:#A88A54FF; outline:1px solid #A88A54FF; }
                .imageProfileCard input:disabled,.imageProfileCard select:disabled { color:#4A4945FF; }
                @media(max-width:700px) { .imageProfileGrid { grid-template-columns:1fr; } .imageProfileCard label { grid-template-columns:1fr; } }
                </style><div class='card imageProfileCard' id='portraitGenerationOptions'>
                  <h2>Character Portrait Generation</h2>
                  <p id='portraitLocalModelReferenceHint' class='hint' style='display:none'>Local Model uses the native reference directly. Choose at least 768 x 1024 pixels and a portrait aspect ratio from 2:3 to 3:4.</p>
                  <div id='portraitGenerationMethodSettings'>
                    <label>Generation method <select id='portraitGenerationMethod' onchange='updatePortraitGenerationUi()'>
                      <option value='text'>Text based (recommended)</option><option value='image'>Image based</option></select></label>
                    <p class='hint'>Text based describes the native face, then creates a photographic portrait from those traits and current age, weight and build. Image based sends the native source to the normal image provider.</p>
                  </div>
                  <div id='portraitVisionSettings'>
                    <label>Vision provider <select id='portraitVisionProvider' onchange='updatePortraitGenerationUi();loadPortraitVisionModels()'><option value='Codex'>Codex SDK (ChatGPT)</option><option value='NanoGPT'>NanoGPT subscription</option></select></label>
                    <label id='portraitCodexVisionModelLabel'>Vision model <select id='portraitVisionModel'><option value='runtime-default'>Runtime default</option></select></label>
                    <label id='portraitNanoVisionModelLabel' style='display:none'>Vision model <select id='portraitVisionNanoGptModel'><option value='deepseek/deepseek-v4.1-flash'>DeepSeek V4.1 Flash</option></select></label>
                    <p class='hint'>Codex uses your ChatGPT connection and image-capable model catalog. NanoGPT uses your saved text API key and current subscription-included vision models. Both describe the source before the normal image provider generates from text.</p>
                    <p id='portraitVisionCatalogStatus' class='hint'></p>
                  </div>
                  <p class='hint'>The normal portrait is created first. Adult female portraits with physical confidence 61–100 then receive the configured adult clothing edit. Existing portrait age edits keep their current image as the reference.</p>
                </div><div class='grid2 imageProfileGrid'>"
                + ImageProfileCard("portrait", "Normal Portraits", "Character, notable and petitioner portraits. Adult female portraits with confidence 61 or above receive a subsequent clothing-only edit.")
                + ImageProfileCard("adultPortrait", "Adult Portraits", "Clothing-only second pass for adult female portraits with physical confidence 61–100. If it fails, Reign keeps the normal result and reports a warning.")
                + ImageProfileCard("scenery", "Normal Scenery &amp; Events", "Memories, castle and family scenes, and event imagery.")
                + ImageProfileCard("adultScenery", "Adult Scenery &amp; Events", "Used by Tavern House Look Again for non-explicit scenes based on the conversation. Arrival images use Normal Scenery &amp; Events.")
                + "</div>";
        }
    }
}
