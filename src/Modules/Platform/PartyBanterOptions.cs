using System;
using System.Collections.Generic;
using System.Globalization;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> BanterOptions(Dictionary<string, object> settings) => TestDict(
            "enabled", ReadBool(settings, "banterEnabled", true),
            "maxResponses", Clamp(ReadInt(settings, "banterMaxResponses", PartyBanterPolicy.DefaultResponses), 2, 12),
            "maxCharacters", Clamp(ReadInt(settings, "banterMaxCharacters", PartyBanterPolicy.DefaultCharacters), 80, 1000));

        private static string ValidateBanterOptions(Dictionary<string, object> incoming)
        {
            if (incoming == null) return "";
            if (incoming.TryGetValue("banterEnabled", out var enabled) && !(enabled is bool)) return "Party banter must be on or off.";
            foreach (var bounds in new[] { new[] { "banterMaxResponses", "2", "12" }, new[] { "banterMaxCharacters", "80", "1000" } })
            {
                if (!incoming.TryGetValue(bounds[0], out var value)) continue;
                if (value == null || value is bool || !double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var number) || number != Math.Truncate(number)
                    || number < int.Parse(bounds[1]) || number > int.Parse(bounds[2]))
                    return bounds[0] + " must be a whole number from " + bounds[1] + " to " + bounds[2] + ".";
            }
            if (incoming.ContainsKey("banterProvider") && !string.IsNullOrWhiteSpace(ReadString(incoming, "banterProvider", ""))
                && Array.IndexOf(ChatProviders, ReadString(incoming, "banterProvider", "")) < 0) return "Select a supported banter provider.";
            return "";
        }

        private static Dictionary<string, object> BanterProviderSettings(Dictionary<string, object> payload, Dictionary<string, object> settings)
        {
            if (ReadString(payload, "requestType", "") != "party_banter") return settings;
            var result = DeepCloneProfileDictionary(settings);
            string provider = ReadString(settings, "banterProvider", "");
            if (!string.IsNullOrWhiteSpace(provider))
            {
                result["llmProvider"] = provider;
                var bank = ReadDictionary(ReadDictionary(settings, "llmModelProfiles"), provider);
                foreach (string key in ChatModelKeys) result[key] = ReadString(bank, key, "");
            }
            if (!string.IsNullOrWhiteSpace(ReadString(settings, "banterModel", ""))) result["dialogueModel"] = settings["banterModel"];
            // Banter is a short performance. Keep ordinary conversation reasoning settings intact.
            result["reasoningMode"] = "off";
            return result;
        }

        private static string BanterOptionsHtml() => @"
      <div class='xpOptions' style='margin-top:24px'>
        <h3>Party banter</h3>
        <label class='xpRow'><input id='banterEnabled' type='checkbox' checked onchange='renderBanterOptions()'> Enable party banter</label>
        <p class='xpHelp'>Party members talk while traveling. After an exchange ends, another can start after eight game hours. Open the dialog symbol to join their conversation.</p>
        <div class='xpRow'><label for='banterMaxResponses'>Maximum responses per exchange</label><input id='banterMaxResponses' type='number' min='2' max='12' step='1' value='6'></div>
        <div class='xpRow'><label for='banterMaxCharacters'>Characters per response</label><input id='banterMaxCharacters' type='number' min='80' max='1000' step='1' value='360'></div>
        <div class='xpRow'><label for='banterProvider'>Banter provider</label><select id='banterProvider'><option value=''>Use dialogue provider</option><option value='nanogpt'>NanoGPT</option><option value='openrouter'>OpenRouter</option><option value='openai_compatible'>OpenAI compatible</option><option value='codex_subscription'>Codex subscription</option></select></div>
        <div class='xpRow'><label for='banterModel'>Banter model</label><input id='banterModel' type='text' placeholder='Use dialogue model'></div>
        <p class='xpHelp'>Each response is one character speaking. Size changes apply to the next exchange. Turning banter off ends automatic speech and retains what was already said. Provider credentials use your existing provider settings.</p>
        <button id='saveBanterOptions' onclick='saveBanterOptions()'>Save banter options</button><div id='banterStatus' role='status' aria-live='polite'></div>
      </div>";

        private static string BanterOptionsScript() => @"
    ids.push('banterEnabled','banterMaxResponses','banterMaxCharacters','banterProvider','banterModel');boolIds.add('banterEnabled');
    function renderBanterOptions(){for(const id of ['banterMaxResponses','banterMaxCharacters'])document.getElementById(id).disabled=!document.getElementById('banterEnabled').checked;}
    function loadBanterOptions(s){document.getElementById('banterEnabled').checked=s.banterEnabled!==false;document.getElementById('banterMaxResponses').value=s.banterMaxResponses??6;document.getElementById('banterMaxCharacters').value=s.banterMaxCharacters??360;document.getElementById('banterProvider').value=s.banterProvider||'';document.getElementById('banterModel').value=s.banterModel||'';renderBanterOptions();}
    async function saveBanterOptions(){const b=document.getElementById('saveBanterOptions'),status=document.getElementById('banterStatus');b.disabled=true;status.textContent='Saving...';try{const fields={banterEnabled:document.getElementById('banterEnabled').checked,banterMaxResponses:Number(document.getElementById('banterMaxResponses').value),banterMaxCharacters:Number(document.getElementById('banterMaxCharacters').value),banterProvider:document.getElementById('banterProvider').value,banterModel:document.getElementById('banterModel').value.trim()};const response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(fields)}),r=await response.json();if(!response.ok||!r.ok)throw Error(r.error||'Could not save banter options.');status.textContent='Saved.';}catch(e){status.textContent=e.message;}finally{b.disabled=false;}}
    renderBanterOptions();";
    }
}
