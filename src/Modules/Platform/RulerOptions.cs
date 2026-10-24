using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using ReignBeta.Shared.Rulers;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> RulerOptionsForClient(Dictionary<string, object> settings) =>
            RulerOptions.Switches.Keys.ToDictionary(k => k, k => (object)ReadBool(settings, k, true), StringComparer.Ordinal);
        private static string ValidateRulerOptions(Dictionary<string, object> incoming)
        {
            foreach (var pair in incoming.Where(p => p.Key.StartsWith("ruler", StringComparison.Ordinal)))
            {
                if (!RulerOptions.Switches.ContainsKey(pair.Key)) return "Unknown ruler option: " + pair.Key;
                if (!(pair.Value is bool)) return RulerOptions.Switches[pair.Key] + " must be on or off.";
            }
            return "";
        }
        private static string RulerOptionsHtml()
        {
            var html = new StringBuilder("<div class='xpOptions' id='rulerOptions'><h3>Rulers, capitals and captivity</h3>");
            foreach (var pair in RulerOptions.Switches)
                html.Append("<label class='xpRow'><input type='checkbox' checked id='").Append(pair.Key).Append("' onchange='renderRulerOptions()'> ").Append(WebUtility.HtmlEncode(pair.Value)).Append("</label>");
            html.Append("<p class='xpHelp'>All switches default to on. Changes apply across campaigns and refresh in the game within five seconds. The master switch controls every feature above. Turning off judgments cancels pending sentences and their uncommitted consequences; completed executions, payments, allegiance changes and memories remain. Settlement effects stop while disabled and keep their original expiry. Turning residence off releases AI travel restrictions. Turning off the governor option restores the native foreign-governor penalty; foreign-owner culture is unchanged. Turning off the life-and-death requirement allows the normal game option again.</p>");
            return html.Append("<div id='rulerOptionsPreview' class='xpHelp' aria-live='polite'></div><button type='button' onclick='resetRulerOptions()'>Reset to defaults</button> <button type='button' id='saveRulerOptions' disabled onclick='saveRulerOptions()'>Save ruler options</button><div id='rulerOptionsStatus' role='status' aria-live='polite'></div></div>").ToString();
        }
        private static string RulerOptionsScript() => "const rulerSwitchIds=" + Json.Serialize(RulerOptions.Switches.Keys.ToArray()) + @";
ids.push(...rulerSwitchIds);rulerSwitchIds.forEach(k=>boolIds.add(k));
let rulerOptionsLoaded=false;
function renderRulerOptions(){const enabled=document.getElementById('rulerSystemEnabled').checked;document.getElementById('rulerOptionsPreview').textContent=enabled?'Ruler features are active according to the switches above.':'Ruler features are disabled.';}
function resetRulerOptions(){rulerSwitchIds.forEach(k=>document.getElementById(k).checked=true);renderRulerOptions();}
async function saveRulerOptions(){const button=document.getElementById('saveRulerOptions'),status=document.getElementById('rulerOptionsStatus');if(!rulerOptionsLoaded){status.textContent='Refresh to load ruler options before saving.';return;}button.disabled=true;status.textContent='Saving...';try{const patch={};rulerSwitchIds.forEach(k=>patch[k]=document.getElementById(k).checked);const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(patch)});const data=await r.json();if(!r.ok||data.ok===false)throw new Error(data.error||'Could not save ruler options');status.textContent='Saved. The game refreshes within five seconds.';}catch(e){status.textContent=e.message;}finally{button.disabled=false;}}
fetch('/api/settings').then(r=>{if(!r.ok)throw new Error('Load failed');return r.json();}).then(s=>{rulerSwitchIds.forEach(k=>document.getElementById(k).checked=s[k]!==false);rulerOptionsLoaded=true;document.getElementById('saveRulerOptions').disabled=false;renderRulerOptions();}).catch(()=>{document.getElementById('rulerOptionsStatus').textContent='Could not load ruler options. Refresh before saving.';});";
    }
}
