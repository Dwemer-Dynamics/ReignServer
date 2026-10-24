using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static NpcLivesOptions ReadNpcLivesOptions(Dictionary<string, object> settings = null)
        {
            settings ??= LoadSettings();
            return new NpcLivesOptions(k => ReadBool(settings, k, true), k =>
            {
                if (!settings.TryGetValue(k, out var raw) || !NpcLivesNumber(raw, out double value)) return null;
                return value;
            });
        }
        private static bool NpcLivesNumber(object raw, out double value)
        {
            value = 0;
            if (!(raw is byte || raw is short || raw is int || raw is long || raw is float || raw is double || raw is decimal)) return false;
            value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return NpcLivesPolicy.Finite(value);
        }
        private static string ValidateNpcLivesOptions(Dictionary<string, object> incoming)
        {
            if (incoming == null) return "";
            foreach (var pair in NpcLivesOptions.Switches)
                if (incoming.TryGetValue(pair.Key, out var raw) && !(raw is bool)) return pair.Value + " must be on or off.";
            foreach (var setting in NpcLivesOptions.Numbers)
                if (incoming.TryGetValue(setting.Key, out var raw) && (!NpcLivesNumber(raw, out double value) || !setting.Accepts(value)))
                    return setting.Label + " must be a number from " + setting.Minimum.ToString(CultureInfo.InvariantCulture) + " to " + setting.Maximum.ToString(CultureInfo.InvariantCulture) + " in steps of " + setting.Step.ToString(CultureInfo.InvariantCulture) + ".";
            foreach (string key in incoming.Keys.Where(k => k.StartsWith("npcLives", StringComparison.Ordinal)))
                if (!NpcLivesOptions.Switches.ContainsKey(key) && !NpcLivesOptions.Numbers.Any(s => s.Key == key)
                    && key != "npcLivesApproachProvider" && key != "npcLivesApproachModel") return "Unknown NPC Lives option: " + key;
            foreach (string key in new[] { "npcLivesApproachProvider", "npcLivesApproachModel" })
                if (incoming.TryGetValue(key, out var value) && (!(value is string text) || text.Length > 200))
                    return "Approach provider and model must be text of at most 200 characters.";
            if (incoming.ContainsKey("npcLivesApproachProvider") && ReadString(incoming, "npcLivesApproachProvider", "").Length > 0
                && !ChatProviders.Contains(ReadString(incoming, "npcLivesApproachProvider", ""))) return "Select a supported Approach provider.";
            return "";
        }
        private static Dictionary<string, object> NpcLivesOptionsForClient(Dictionary<string, object> settings)
        {
            var options = ReadNpcLivesOptions(settings);
            var result = NpcLivesOptions.Switches.Keys.ToDictionary(k => k, k => (object)options.Preference(k), StringComparer.Ordinal);
            foreach (var setting in NpcLivesOptions.Numbers) result[setting.Key] = options.Number(setting.Key);
            return result;
        }
        private static string NpcLivesOptionsHtml()
        {
            var html = new StringBuilder("<div class='xpOptions' style='margin-top:24px'><h3>NPC Lives and Storylines</h3>");
            foreach (var item in NpcLivesOptions.Switches)
            {
                if (item.Key == NpcLivesOptions.InitialApproachKey) html.Append("<fieldset id='npcLivesFeatures' style='border:0;padding:0;margin:0'><legend class='xpHelp'>Individual features</legend>");
                html.Append("<label class='xpRow'><input type='checkbox' checked id='").Append(item.Key).Append("' onchange='renderNpcLivesOptions()'> ").Append(WebUtility.HtmlEncode(item.Value)).Append("</label>");
                if (item.Key == NpcLivesOptions.InitialApproachKey)
                    html.Append("<p class='xpHelp'>Turn off to speak first instead of receiving an automatic NPC Lives Approach opening. Wilderness events, social events and other existing scene openings keep their required prompts. Personal stories and party announcements have their own switches.</p>");
            }
            html.Append("</fieldset><p class='xpHelp'>Changes apply across campaigns; the game refreshes every five seconds. Disabling a feature stops new requests. Accepted stories can finish, travellers can return, and existing meeting commitments continue. Memories remain. Disabling Approach calls preserves ordinary conversation and silent resumption.</p>");
            html.Append("<details><summary>Frequency, rewards and percentage adjustments</summary>");
            AppendNpcLivesNumberControls(html, false);
            return html.Append("</details><p class='xpHelp'>100% is the normal rate. Frequency modifiers multiply; 0% prevents new starts. Shorter cooldown percentages reduce waiting. Summons default to 80% of estimated normal travel time. Affinity, prerequisites and repetition limits still apply. Existing rolls and agreed dates do not change.</p><div id='npcLivesPreview' aria-live='polite'></div><button type='button' onclick='resetNpcLivesOptions()'>Reset to defaults</button> <button id='saveNpcLivesOptions' onclick='saveNpcLivesOptions()'>Save NPC Lives options</button><div id='npcLivesOptionsStatus' role='status' aria-live='polite'></div></div>").ToString();
        }
        private static bool NpcLivesAdvancedNumber(NpcLivesSetting setting) => !setting.Unit.StartsWith("%", StringComparison.Ordinal);
        private static void AppendNpcLivesNumberControls(StringBuilder html, bool advanced)
        {
            foreach (var s in NpcLivesOptions.Numbers.Where(s => NpcLivesAdvancedNumber(s) == advanced))
            {
                string Attr(double n) => n.ToString(CultureInfo.InvariantCulture);
                html.Append("<div class='xpRow'><label for='").Append(s.Key).Append("'>").Append(WebUtility.HtmlEncode(s.Label)).Append("</label><input type='range' id='").Append(s.Key)
                    .Append("' min='").Append(Attr(s.Minimum)).Append("' max='").Append(Attr(s.Maximum)).Append("' step='").Append(Attr(s.Step)).Append("' value='").Append(Attr(s.Default)).Append("' oninput='syncNpcLivesNumber(this,false)' aria-describedby='npcLivesPreview'>")
                    .Append("<input type='number' style='width:6em' aria-label='").Append(WebUtility.HtmlEncode(s.Label)).Append(" value' id='").Append(s.Key).Append("Number' min='").Append(Attr(s.Minimum)).Append("' max='").Append(Attr(s.Maximum)).Append("' step='").Append(Attr(s.Step)).Append("' value='").Append(Attr(s.Default)).Append("' oninput='syncNpcLivesNumber(this,true)'>")
                    .Append("<span>").Append(WebUtility.HtmlEncode(s.Unit)).Append("</span><button type='button' onclick=\"resetNpcLivesOption('").Append(s.Key).Append("')\">Reset</button></div>");
            }
        }
        private static string NpcLivesDiagnosticsHtml()
        {
            var html = new StringBuilder("<section id='npclivesdiagnostics' class='page'><h2>NPC Lives and Story Editor</h2><div class='xpOptions'><h3>Advanced Approach and timing</h3><p class='xpHelp'>Configure the fast Approach model and base timing. Feature switches and percentage adjustments are in Options. Existing commitments retain their accepted terms.</p>");
            html.Append("<div class='xpRow'><label for='npcLivesApproachProvider'>Fast Approach provider</label><select id='npcLivesApproachProvider'><option value=''>Use banter provider</option><option value='nanogpt'>NanoGPT</option><option value='openrouter'>OpenRouter</option><option value='openai_compatible'>OpenAI compatible</option><option value='codex_subscription'>Codex subscription</option></select></div><div class='xpRow'><label for='npcLivesApproachModel'>Fast Approach model</label><input id='npcLivesApproachModel' maxlength='200' placeholder='Use banter model'></div>");
            AppendNpcLivesNumberControls(html, true);
            html.Append("<button type='button' onclick='resetNpcLivesAdvancedOptions()'>Reset advanced defaults</button> <button id='saveNpcLivesAdvancedOptions' type='button' onclick='saveNpcLivesOptions(true)'>Save advanced settings</button><div id='npcLivesAdvancedOptionsStatus' role='status' aria-live='polite'></div></div>");
            return html.Append(NpcStoryEditorHtml()).Append("</section>").ToString();
        }
        private static string NpcLivesOptionsScript() => "const npcLivesSwitchIds=" + Json.Serialize(NpcLivesOptions.Switches.Keys.ToArray())
            + ";const npcLivesNumberSpecs=" + Json.Serialize(NpcLivesOptions.Numbers.Select(s => TestDict("key", s.Key, "value", s.Default, "min", s.Minimum, "max", s.Maximum, "step", s.Step, "advanced", NpcLivesAdvancedNumber(s))).ToArray())
            + ";const npcLivesPreviewWeights=" + Json.Serialize(NpcLivesPolicy.CategoryWeights) + @";
    const npcLivesTextIds=['npcLivesApproachProvider','npcLivesApproachModel'];ids.push(...npcLivesSwitchIds,...npcLivesNumberSpecs.map(s=>s.key),...npcLivesTextIds);npcLivesSwitchIds.forEach(k=>boolIds.add(k));
    function npcLivesNumericValid(s,v){return Number.isFinite(v)&&v>=s.min&&v<=s.max&&Math.abs((v-s.min)/s.step-Math.round((v-s.min)/s.step))<.000001;}
    function syncNpcLivesNumber(input,number){const key=number?input.id.slice(0,-6):input.id,s=npcLivesNumberSpecs.find(x=>x.key===key),v=Number(input.value);input.setCustomValidity(input.value!==''&&npcLivesNumericValid(s,v)?'':'Enter a value in the shown range and step.');if(input.validity.valid)document.getElementById(key+(number?'':'Number')).value=input.value;renderNpcLivesOptions();}
    function resetNpcLivesOption(key){const s=npcLivesNumberSpecs.find(x=>x.key===key);for(const suffix of ['','Number']){const e=document.getElementById(key+suffix);e.value=s.value;e.setCustomValidity('');}renderNpcLivesOptions();}
    function resetNpcLivesOptions(){npcLivesSwitchIds.forEach(k=>document.getElementById(k).checked=true);npcLivesNumberSpecs.filter(s=>!s.advanced).forEach(s=>resetNpcLivesOption(s.key));renderNpcLivesOptions();}
    function resetNpcLivesAdvancedOptions(){npcLivesTextIds.forEach(k=>document.getElementById(k).value='');npcLivesNumberSpecs.filter(s=>s.advanced).forEach(s=>resetNpcLivesOption(s.key));renderNpcLivesOptions();}
    function loadNpcLivesOptions(settings){npcLivesTextIds.forEach(k=>document.getElementById(k).value=settings[k]||'');npcLivesSwitchIds.forEach(k=>document.getElementById(k).checked=settings[k]!==false);for(const s of npcLivesNumberSpecs){const v=typeof settings[s.key]==='number'&&npcLivesNumericValid(s,settings[s.key])?settings[s.key]:s.value;for(const suffix of ['','Number']){const e=document.getElementById(s.key+suffix);e.value=v;e.setCustomValidity('');}}document.getElementById('npcLivesOptionsStatus').textContent='';renderNpcLivesOptions();}
    function renderNpcLivesOptions(){
      const get=k=>Number(document.getElementById('npcLives'+k).value),on=k=>document.getElementById('npcLives'+k+'Enabled').checked,master=on('');
      document.getElementById('npcLivesFeatures').disabled=!master;for(const k of ['Brief','Outings','Major'])document.getElementById('npcLives'+k+'Enabled').disabled=!on('Stories');
      document.getElementById('npcLivesApproachEnabled').disabled=!on('InitialApproach');
      const kinds={brief:['Brief','Brief'],outing:['Outings','Outing'],major:['Major','Major'],leave:['Leave','Leave'],independent:['Independent','Independent']};
      const lines=[20,40,60,80,100].map(a=>{const cats=Object.keys(kinds).filter(k=>master&&on(kinds[k][0])&&(!['brief','outing','major'].includes(k)||on('Stories'))&&(k!=='outing'||a>=60)&&(k!=='major'||a>=80));const total=cats.reduce((s,k)=>s+npcLivesPreviewWeights[k],0);let values=cats.map(k=>[k,get('Chance'+a)/100*npcLivesPreviewWeights[k]/total*get('FrequencyPercent')/100*get(kinds[k][1]+'FrequencyPercent')/100]);const sum=values.reduce((s,v)=>s+v[1],0);if(sum>1)values=values.map(([k,p])=>[k,p/sum]);return '+'+a+' affinity: '+(100*Math.min(1,sum)).toFixed(2)+'% total daily chance'+(values.length?' ('+values.map(([k,p])=>k+' '+(p*100).toFixed(2)+'%').join(', ')+')':'');});
      lines.push('New invitation cooldown: '+(get('InvitationDays')*get('InvitationCooldownPercent')/100).toFixed(2)+' days. Major starts: '+(get('MajorMinDays')*get('MajorCooldownPercent')/100).toFixed(2)+'–'+(get('MajorMaxDays')*get('MajorCooldownPercent')/100).toFixed(2)+' days. Ask again: '+(get('ReaskDays')*get('ReaskDelayPercent')/100).toFixed(2)+' days.');
      document.getElementById('npcLivesPreview').replaceChildren(...lines.map(t=>{const p=document.createElement('p');p.textContent=t;return p;}));
    }
    function npcLivesSettingsForSave(includeAdvanced=false,diagnosticsOnly=false){const fields=diagnosticsOnly?{}:Object.fromEntries(npcLivesSwitchIds.map(k=>[k,document.getElementById(k).checked]));for(const s of npcLivesNumberSpecs.filter(s=>diagnosticsOnly?s.advanced:includeAdvanced||!s.advanced)){const e=document.getElementById(s.key+'Number'),v=Number(e.value);if(e.value===''||!npcLivesNumericValid(s,v)||!e.reportValidity())throw Error('Check the range and step for '+e.getAttribute('aria-label')+'.');fields[s.key]=v;}if(includeAdvanced||diagnosticsOnly)for(const key of npcLivesTextIds)fields[key]=document.getElementById(key).value.trim();return fields;}
    async function saveNpcLivesOptions(diagnostics=false){const status=document.getElementById(diagnostics?'npcLivesAdvancedOptionsStatus':'npcLivesOptionsStatus'),button=document.getElementById(diagnostics?'saveNpcLivesAdvancedOptions':'saveNpcLivesOptions');button.disabled=true;status.className='';status.textContent='Saving…';try{const fields=npcLivesSettingsForSave(false,diagnostics),response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(fields)}),r=await response.json();if(!response.ok||!r.ok)throw Error(r.error||'Could not save NPC Lives options.');status.textContent='Saved. New scheduling uses these values; existing commitments retain their terms.';}catch(e){status.className='xpError';status.textContent=e.message;}finally{button.disabled=false;}}
    renderNpcLivesOptions();";
    }
}
