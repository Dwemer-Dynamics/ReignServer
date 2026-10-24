using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly Dictionary<string, string> PersonalGossipChanceLabels = new Dictionary<string, string> {
            ["share"] = "Sharing a conversation", ["negative"] = "Choosing malicious gossip",
            ["disclosure"] = "Disclosing details or an impression", ["distortion"] = "Malicious exaggeration",
            ["retell"] = "Retelling something heard", ["raise"] = "Bringing gossip up with you"
        };
        private static string GossipAdjustmentKey(string stage) => "personalGossip" + char.ToUpperInvariant(stage[0]) + stage.Substring(1) + "Adjustment";
        private static bool ValidGossipAdjustment(object value) =>
            (value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal)
            && double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture))
            && Convert.ToDouble(value, CultureInfo.InvariantCulture) >= -100 && Convert.ToDouble(value, CultureInfo.InvariantCulture) <= 100
            && Convert.ToDouble(value, CultureInfo.InvariantCulture) % 1 == 0;
        private static string ValidatePersonalGossipOptions(Dictionary<string, object> incoming)
        {
            foreach (var pair in incoming ?? new Dictionary<string, object>())
            {
                if (pair.Key == "personalGossip") continue; // Retain the existing versioned policy setting.
                if (!pair.Key.StartsWith("personalGossip", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Key == "personalGossipEnabled") { if (!(pair.Value is bool)) return "Personal gossip must be on or off."; continue; }
                if (!PersonalGossipChanceLabels.Keys.Any(stage => GossipAdjustmentKey(stage) == pair.Key)) return "Unknown personal gossip option: " + pair.Key;
                if (!ValidGossipAdjustment(pair.Value)) return "Gossip adjustments must be whole numbers from −100 to +100 percentage points.";
            }
            return "";
        }
        private static Dictionary<string, object> PersonalGossipChanceOptions(Dictionary<string, object> settings)
        {
            var result = new Dictionary<string, object>();
            foreach (string stage in PersonalGossipChanceLabels.Keys)
                result[stage] = settings != null && settings.TryGetValue(GossipAdjustmentKey(stage), out var value) && ValidGossipAdjustment(value)
                    ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
            return result;
        }
        private static int GossipAdjustedChance(int basis, string stage, Dictionary<string, object> policy) =>
            GossipPercent(basis + ReadInt(ReadDictionary(policy, "chanceAdjustments"), stage, 0));
        private static string PersonalGossipOptionsHtml()
        {
            var html = new StringBuilder(@"<div class='xpOptions' id='personalGossipOptions'><h3>Personal gossip</h3>
<label class='xpRow'><input type='checkbox' id='personalGossipEnabled' checked onchange='renderPersonalGossipOptions()'> Enable personal gossip</label>
<p class='xpHelp' id='personalGossipChanceHelp'>Adjust personality-based chances in percentage points. 0 keeps the standard chance. For example, +20 changes a 40% chance to 60%. −100 prevents that roll; +100 guarantees it when eligible. Results stay between 0% and 100%.</p>");
            foreach (var pair in PersonalGossipChanceLabels)
            {
                string key = GossipAdjustmentKey(pair.Key);
                html.Append("<div class='xpRow'><label for='").Append(key).Append("'>").Append(pair.Value)
                    .Append("</label><input type='range' min='-100' max='100' step='1' value='0' id='").Append(key)
                    .Append("' oninput='renderPersonalGossipOptions()' aria-describedby='personalGossipChanceHelp'><output id='").Append(key)
                    .Append("Value' for='").Append(key).Append("'>Standard</output></div>");
            }
            return html.Append(@"<p class='xpHelp'>These options apply across campaigns on this server after saving, without restarting. Each new decision keeps its settings and saved roll. Existing decisions and queued messages keep their results and travel times. Family, friendship, motive, privacy classification and location requirements still apply. A completely honest NPC never fabricates an event. Turning gossip off pauses pending gossip; turning it on resumes it subject to normal expiry.</p>
<button type='button' id='resetPersonalGossipOptions' onclick='resetPersonalGossipOptions()'>Reset to standard chances</button> <button type='button' id='savePersonalGossipOptions' disabled onclick='savePersonalGossipOptions()'>Save gossip options</button>
<div id='personalGossipOptionsStatus' role='status' aria-live='polite'></div></div>").ToString();
        }
        private static string PersonalGossipOptionsScript() => "const personalGossipChanceIds=" + Json.Serialize(PersonalGossipChanceLabels.Keys.Select(GossipAdjustmentKey).ToArray()) + @";
ids.push('personalGossipEnabled',...personalGossipChanceIds);boolIds.add('personalGossipEnabled');
let personalGossipOptionsLoaded=false,personalGossipOptionsSaving=false;
function renderPersonalGossipOptions(){const enabled=document.getElementById('personalGossipEnabled').checked;personalGossipChanceIds.forEach(id=>{const el=document.getElementById(id),n=Number(el.value);el.disabled=!enabled||personalGossipOptionsSaving;document.getElementById(id+'Value').textContent=n===0?'Standard':(n>0?'+':'')+n+' pp';el.setAttribute('aria-valuetext',n===0?'Standard chance':(n>0?'plus ':'minus ')+Math.abs(n)+' percentage points');});document.getElementById('personalGossipEnabled').disabled=personalGossipOptionsSaving;document.getElementById('resetPersonalGossipOptions').disabled=personalGossipOptionsSaving;}
function loadPersonalGossipOptions(settings){document.getElementById('personalGossipEnabled').checked=settings.personalGossipEnabled!==false;personalGossipChanceIds.forEach(id=>{intIds.add(id);const n=settings[id];document.getElementById(id).value=typeof n==='number'&&Number.isInteger(n)&&n>=-100&&n<=100?n:0;});personalGossipOptionsLoaded=true;document.getElementById('savePersonalGossipOptions').disabled=false;renderPersonalGossipOptions();}
function resetPersonalGossipOptions(){document.getElementById('personalGossipEnabled').checked=true;personalGossipChanceIds.forEach(id=>document.getElementById(id).value=0);document.getElementById('personalGossipOptionsStatus').textContent='Standard chances selected. Save to apply.';renderPersonalGossipOptions();}
async function savePersonalGossipOptions(){const button=document.getElementById('savePersonalGossipOptions'),status=document.getElementById('personalGossipOptionsStatus');if(!personalGossipOptionsLoaded||personalGossipOptionsSaving){if(!personalGossipOptionsLoaded)status.textContent='Refresh to load gossip options before saving.';return;}const patch={personalGossipEnabled:document.getElementById('personalGossipEnabled').checked};personalGossipChanceIds.forEach(id=>patch[id]=Number(document.getElementById(id).value));personalGossipOptionsSaving=true;button.disabled=true;status.className='';status.textContent='Saving…';renderPersonalGossipOptions();try{const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(patch)}),data=await r.json();if(!r.ok||data.ok===false)throw new Error(data.error||'Could not save gossip options');status.textContent='Saved. New gossip decisions use these chances immediately.';}catch(e){status.className='xpError';status.textContent=e.message;}finally{personalGossipOptionsSaving=false;button.disabled=false;renderPersonalGossipOptions();}}
";
    }
}
