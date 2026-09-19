using System;
using System.Collections.Generic;
using System.Globalization;
using Reign.Core.Contracts.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string ValidateReignXpOptions(Dictionary<string, object> incoming)
        {
            if (incoming == null) return "";
            if (incoming.TryGetValue("reignXpEnabled", out var enabled) && !(enabled is bool))
                return "Reign XP Gain must be on or off.";
            if (incoming.TryGetValue("reignXpMultiplier", out var multiplier))
            {
                try
                {
                    if (multiplier == null || multiplier is bool
                        || !ReignXpRules.IsValidMultiplier(Convert.ToDouble(multiplier, CultureInfo.InvariantCulture)))
                        return "XP multiplier must be from 0.25 to 5 in steps of 0.25.";
                }
                catch { return "XP multiplier must be a number from 0.25 to 5."; }
            }
            return "";
        }

        private static Dictionary<string, object> ReignXpOptionsForClient()
        {
            var settings = LoadSettings();
            double multiplier;
            try { multiplier = Convert.ToDouble(settings["reignXpMultiplier"], CultureInfo.InvariantCulture); }
            catch { multiplier = 1; }
            if (!ReignXpRules.IsValidMultiplier(multiplier)) multiplier = 1;
            return new Dictionary<string, object> {
                ["ok"] = true,
                ["reignXpEnabled"] = ReadBool(settings, "reignXpEnabled", true),
                ["reignXpMultiplier"] = multiplier
            };
        }

        private static string ReignXpOptionsHtml() => @"
    <section id='options' class='page'>
      <style>
        #options .xpOptions{max-width:760px;background:#121211;color:#C5BDAF;border:1px solid #7E6A4D;padding:24px}
        #options h2,#options h3{color:#A88A54} #options .xpHelp{color:#8A8883}
        #options .xpRow{display:flex;align-items:center;gap:12px;margin:20px 0;flex-wrap:wrap}
        #options input{accent-color:#A88A54} #options input[type=range]{flex:1;min-width:140px;width:100%;max-width:440px}
        #options output{color:#C5AC83;min-width:4em} #options input:disabled{opacity:.55}
        #options button{background:#1A1917;color:#C5BDAF;border:1px solid #7E6A4D}
        #options button:disabled{color:#4A4945} #options :focus-visible{outline:2px solid #A88A54;outline-offset:3px}
        #options .xpError{color:#C35B32} #options #reignXpStatus{min-height:1.5em;margin-top:12px}
      </style>
      <h2>Options</h2>
      <div class='xpOptions'>
        <h3>Experience gains</h3>
        <label class='xpRow'><input id='reignXpEnabled' type='checkbox' checked onchange='renderReignXpOptions()'> Reign XP Gain</label>
        <p class='xpHelp'>Earn experience by talking with people, participating in social events and deciding petitions.</p>
        <div class='xpRow'><label for='reignXpMultiplier'>XP Multiplier</label>
          <input id='reignXpMultiplier' type='range' min='0.25' max='5' step='0.25' value='1' oninput='renderReignXpOptions()' aria-describedby='reignXpRewards'>
          <output id='reignXpMultiplierValue' for='reignXpMultiplier'>1×</output></div>
        <p id='reignXpRewards'></p>
        <p class='xpHelp'>Each social phase also grants one random bonus based on a person you spoke with: their highest Steward, Trade, Leadership or Tactics skill. Steward is the quartermaster skill.</p>
        <p class='xpHelp'>Amounts are before your character’s normal learning rate. Settings apply to subsequent interactions across campaigns on this server.</p>
        <button id='saveReignXpOptions' onclick='saveReignXpOptions()'>Save XP options</button>
        <div id='reignXpStatus' role='status' aria-live='polite'></div>
      </div>
    </section>";

        private static string ReignXpOptionsScript() => @"
    ids.push('reignXpEnabled','reignXpMultiplier');
    boolIds.add('reignXpEnabled');
    function renderReignXpOptions(){
      const enabled=document.getElementById('reignXpEnabled').checked;
      const slider=document.getElementById('reignXpMultiplier'), m=Number(slider.value);
      slider.disabled=!enabled;
      document.getElementById('reignXpMultiplierValue').textContent=m+'×';
      document.getElementById('reignXpRewards').textContent=enabled
        ? (5*m)+' Charm per spoken exchange or social phase; '+(2*m)+' random skill XP per social phase; '+(10*m)+' Leadership per decided petition.'
        : 'Reign participation rewards are off. Your multiplier is retained.';
    }
    function loadReignXpOptions(settings){
      const status=document.getElementById('reignXpStatus');status.className='';status.textContent='';
      document.getElementById('reignXpEnabled').checked=settings.reignXpEnabled!==false;
      const m=Number(settings.reignXpMultiplier??1);
      document.getElementById('reignXpMultiplier').value=Number.isFinite(m)&&m>=.25&&m<=5?m:1;
      renderReignXpOptions();
    }
    async function saveReignXpOptions(){
      const button=document.getElementById('saveReignXpOptions'), status=document.getElementById('reignXpStatus');
      const fields={reignXpEnabled:document.getElementById('reignXpEnabled').checked,reignXpMultiplier:Number(document.getElementById('reignXpMultiplier').value)};
      button.disabled=true;status.className='';status.textContent='Saving…';
      try{
        const response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(fields)});
        const result=await response.json();if(!response.ok||!result.ok)throw new Error(result.error||'Could not save XP options.');
        status.textContent='Saved. XP options apply to subsequent interactions.';
      }catch(error){status.className='xpError';status.textContent=error.message;}
      finally{button.disabled=false;}
    }
    renderReignXpOptions();";
    }
}
