using System;
using System.Collections.Generic;
using System.Globalization;
using Reign.Core.Contracts.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] ReignXpCategoryOptionKeys = {
            "reignXpConversationMultiplier", "reignXpSocialCharmMultiplier",
            "reignXpSocialBonusMultiplier", "reignXpPetitionMultiplier"
        };

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
            foreach (string key in ReignXpCategoryOptionKeys)
            {
                if (!incoming.TryGetValue(key, out var category)) continue;
                if (!(category is byte || category is short || category is int || category is long
                    || category is float || category is double || category is decimal)
                    || !ReignXpRules.IsValidCategoryMultiplier(Convert.ToDouble(category, CultureInfo.InvariantCulture)))
                    return "XP category multipliers must be numbers from 0 to 5 in steps of 0.25.";
            }
            return "";
        }

        private static Dictionary<string, object> ReignXpOptionsForClient()
            => ReignXpOptionsForClient(LoadSettings());

        private static Dictionary<string, object> ReignXpOptionsForClient(Dictionary<string, object> settings)
        {
            double multiplier;
            try { multiplier = Convert.ToDouble(settings["reignXpMultiplier"], CultureInfo.InvariantCulture); }
            catch { multiplier = 1; }
            if (!ReignXpRules.IsValidMultiplier(multiplier)) multiplier = 1;
            var result = new Dictionary<string, object> {
                ["ok"] = true,
                ["reignXpEnabled"] = ReadBool(settings, "reignXpEnabled", true),
                ["reignXpMultiplier"] = multiplier,
                ["playerApproaches"] = PlayerApproachOptionsForClient(settings)
            };
            foreach (string key in ReignXpCategoryOptionKeys)
            {
                double category = 1;
                if (settings.TryGetValue(key, out var value))
                {
                    try { category = Convert.ToDouble(value, CultureInfo.InvariantCulture); }
                    catch { category = 1; }
                    if (value == null || value is bool || !ReignXpRules.IsValidCategoryMultiplier(category)) category = 1;
                }
                result[key] = category;
            }
            return result;
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
        <div class='xpRow'><label for='reignXpMultiplier'>Universal multiplier</label>
          <input id='reignXpMultiplier' type='range' min='0.25' max='5' step='0.25' value='1' oninput='renderReignXpOptions()' aria-describedby='reignXpRewards'>
          <output id='reignXpMultiplierValue' for='reignXpMultiplier'>1×</output></div>
        <p class='xpHelp'>Each reward uses its base amount × universal multiplier × category multiplier. Set a category to 0× to turn off that reward.</p>
        <div class='xpRow'><label for='reignXpConversationMultiplier'>Conversation Charm</label>
          <input id='reignXpConversationMultiplier' type='range' min='0' max='5' step='0.25' value='1' oninput='renderReignXpOptions()' aria-describedby='reignXpRewards'>
          <output id='reignXpConversationMultiplierValue' for='reignXpConversationMultiplier'>1×</output></div>
        <div class='xpRow'><label for='reignXpSocialCharmMultiplier'>Social-phase Charm</label>
          <input id='reignXpSocialCharmMultiplier' type='range' min='0' max='5' step='0.25' value='1' oninput='renderReignXpOptions()' aria-describedby='reignXpRewards'>
          <output id='reignXpSocialCharmMultiplierValue' for='reignXpSocialCharmMultiplier'>1×</output></div>
        <div class='xpRow'><label for='reignXpSocialBonusMultiplier'>Social phase-end skill bonus</label>
          <input id='reignXpSocialBonusMultiplier' type='range' min='0' max='5' step='0.25' value='1' oninput='renderReignXpOptions()' aria-describedby='reignXpRewards'>
          <output id='reignXpSocialBonusMultiplierValue' for='reignXpSocialBonusMultiplier'>1×</output></div>
        <div class='xpRow'><label for='reignXpPetitionMultiplier'>Petition-decision Leadership</label>
          <input id='reignXpPetitionMultiplier' type='range' min='0' max='5' step='0.25' value='1' oninput='renderReignXpOptions()' aria-describedby='reignXpRewards'>
          <output id='reignXpPetitionMultiplierValue' for='reignXpPetitionMultiplier'>1×</output></div>
        <p id='reignXpRewards'></p>
        <p class='xpHelp'>Each social phase also grants one random bonus based on a person you spoke with: their highest Steward, Trade, Leadership or Tactics skill. Steward is the quartermaster skill.</p>
        <p class='xpHelp'>Amounts are before your character’s normal learning rate. Settings apply to subsequent interactions across campaigns on this server.</p>
        <button id='saveReignXpOptions' onclick='saveReignXpOptions()'>Save XP options</button>
        <div id='reignXpStatus' role='status' aria-live='polite'></div>
      </div>
" + BanterOptionsHtml() + PlayerApproachOptionsHtml() + @"    </section>";

        private static string ReignXpOptionsScript() => @"
    ids.push('reignXpEnabled','reignXpMultiplier');
    boolIds.add('reignXpEnabled');
    const reignXpCategoryIds=['reignXpConversationMultiplier','reignXpSocialCharmMultiplier','reignXpSocialBonusMultiplier','reignXpPetitionMultiplier'];
    ids.push(...reignXpCategoryIds);
    function renderReignXpOptions(){
      const enabled=document.getElementById('reignXpEnabled').checked;
      const slider=document.getElementById('reignXpMultiplier'), m=Number(slider.value);
      slider.disabled=!enabled;
      document.getElementById('reignXpMultiplierValue').textContent=m+'×';
      const categories=reignXpCategoryIds.map(id=>{
        const input=document.getElementById(id);input.disabled=!enabled;
        document.getElementById(id+'Value').textContent=input.value+'×';
        return Number(input.value);
      });
      document.getElementById('reignXpRewards').textContent=enabled
        ? (5*m*categories[0])+' Charm per spoken exchange; '+(5*m*categories[1])+' Charm per social phase; '+(2*m*categories[2])+' random skill XP per social phase; '+(10*m*categories[3])+' Leadership per decided petition.'
        : 'Reign participation rewards are off. Your multipliers are retained.';
    }
    function loadReignXpOptions(settings){
      loadBanterOptions(settings);
      loadPlayerApproachOptions(settings);
      const status=document.getElementById('reignXpStatus');status.className='';status.textContent='';
      document.getElementById('reignXpEnabled').checked=settings.reignXpEnabled!==false;
      const m=Number(settings.reignXpMultiplier??1);
      document.getElementById('reignXpMultiplier').value=Number.isFinite(m)&&m>=.25&&m<=5?m:1;
      reignXpCategoryIds.forEach(id=>{
        const value=settings[id], c=typeof value==='number'?value:1;
        document.getElementById(id).value=Number.isFinite(c)&&c>=0&&c<=5&&Math.abs(c*4-Math.round(c*4))<.000001?c:1;
      });
      renderReignXpOptions();
    }
    async function saveReignXpOptions(){
      const button=document.getElementById('saveReignXpOptions'), status=document.getElementById('reignXpStatus');
      const fields={reignXpEnabled:document.getElementById('reignXpEnabled').checked,reignXpMultiplier:Number(document.getElementById('reignXpMultiplier').value)};
      reignXpCategoryIds.forEach(id=>fields[id]=Number(document.getElementById(id).value));
      button.disabled=true;status.className='';status.textContent='Saving…';
      try{
        const response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(fields)});
        const result=await response.json();if(!response.ok||!result.ok)throw new Error(result.error||'Could not save XP options.');
        status.textContent='Saved. XP options apply to subsequent interactions.';
      }catch(error){status.className='xpError';status.textContent=error.message;}
      finally{button.disabled=false;}
    }
    renderReignXpOptions();" + BanterOptionsScript() + PlayerApproachOptionsScript();
    }
}
