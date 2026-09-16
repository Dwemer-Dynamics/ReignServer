using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
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
          <div id='@PROFILE@OpenRouterProfileSettings' style='display:none'>
            <label>OpenRouter image model <select id='@PROFILE@OpenRouterImageModel'>@OPENROUTER@</select></label>
            <p class='hint'>Uses your saved OpenRouter key from API Settings and the supplied reference image.</p>
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
            <label>Codex image model <select id='@PROFILE@CodexImageModel' disabled><option value='gpt-image-2'>Image 2 (Codex managed)</option></select></label>
            <p class='hint'>Uses your signed-in Codex subscription. Connect Codex in API Settings. Normal images only; one generation at a time.</p>
          </div>")
                .Replace("@PROFILE@", prefix).Replace("@TITLE@", title).Replace("@HINT@", hint)
                .Replace("@PROVIDERS@", ImageProfileOptions(new[] { "NanoGPT", "AtlasCloud", "OpenRouter" })
                    + (prefix.StartsWith("adult") ? "" : "<option value='Codex'>Codex SDK (ChatGPT)</option>"))
                .Replace("@OPENROUTER@", ImageProfileOptions(OpenRouterImageModels)).Replace("@NANO@", ImageProfileOptions(NanoImageModels)).Replace("@ATLAS@", ImageProfileOptions(AtlasImageModels));
        }

        private static string ImageProfileCards()
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
                </style><div class='grid2 imageProfileGrid'>"
                + ImageProfileCard("portrait", "Normal Portraits", "Character, notable and petitioner portraits. Adult female portraits with confidence 61 or above receive a subsequent clothing-only edit.")
                + ImageProfileCard("adultPortrait", "Adult Portraits", "Clothing-only second pass for adult female portraits with physical confidence 61–100. If it fails, Reign keeps the normal result and reports a warning.")
                + ImageProfileCard("scenery", "Normal Scenery &amp; Events", "Memories, castle and family scenes, and event imagery.")
                + ImageProfileCard("adultScenery", "Adult Scenery &amp; Events", "Used by Tavern House Look Again for non-explicit scenes based on the conversation. Arrival images use Normal Scenery &amp; Events.")
                + "</div>";
        }
    }
}
