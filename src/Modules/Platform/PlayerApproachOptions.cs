using System.Collections.Generic;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static PlayerApproachOptions ReadPlayerApproachOptions(Dictionary<string, object> settings = null)
        {
            settings ??= LoadSettings();
            return new PlayerApproachOptions(key => ReadBool(settings, key, true));
        }

        private static string ValidatePlayerApproachOptions(Dictionary<string, object> incoming)
        {
            if (incoming != null) foreach (string key in PlayerApproachOptions.SettingKeys)
                if (incoming.TryGetValue(key, out object value) && !(value is bool))
                    return "Player attention and approach switches must be on or off.";
            return "";
        }

        private static Dictionary<string, object> PlayerApproachOptionsForClient(Dictionary<string, object> settings)
        {
            var result = new Dictionary<string, object>();
            var options = ReadPlayerApproachOptions(settings);
            foreach (string key in PlayerApproachOptions.SettingKeys) result[key] = options.Preference(key);
            return result;
        }

        private static string PlayerApproachOptionsHtml() => @"
      <div class='xpOptions' style='margin-top:24px'>
        <h3>Player attention and NPC approaches</h3>
        <label class='xpRow'><input id='playerApproachesEnabled' type='checkbox' checked onchange='renderPlayerApproachOptions()'> Enable player attention and NPC approaches</label>
        <p class='xpHelp'>The master switch pauses all features below and retains your individual choices. Changes apply across campaigns on this server; the game checks for changes every five seconds.</p>
        <fieldset id='playerApproachElements' style='border:0;padding:0;margin:0'>
          <legend class='xpHelp'>Individual features</legend>
          <label class='xpRow'><input id='playerAttentionTrackingEnabled' type='checkbox' checked> Track witnessed player attention</label>
          <p class='xpHelp'>Record who you talk to for nearby characters. Turning this off stops new observations; existing history remains available.</p>
          <label class='xpRow'><input id='playerJealousyApproachesEnabled' type='checkbox' checked> Jealousy and neglect approaches</label>
          <p class='xpHelp'>Characters ask to speak when they feel overlooked. Uses retained attention history even when new tracking is off.</p>
          <label class='xpRow'><input id='playerKeepInvitationsEnabled' type='checkbox' checked> Noble invitations in keeps</label>
          <label class='xpRow'><input id='playerPatrolInterceptionsEnabled' type='checkbox' checked> Friendly noble patrol interceptions</label>
          <label class='xpRow'><input id='playerBattleRescueDialogueEnabled' type='checkbox' checked> Listen after helping win an outmatched battle</label>
          <label class='xpRow'><input id='playerPrisonRescueDialogueEnabled' type='checkbox' checked> Listen after a successful prison rescue</label>
        </fieldset>
        <p class='xpHelp'>Disabling approaches cancels unanswered invitations without relationship loss. Conversations already accepted can finish. Rescue rewards and ordinary native conversations continue.</p>
        <button id='savePlayerApproachOptions' onclick='savePlayerApproachOptions()'>Save approach options</button>
        <div id='playerApproachOptionsStatus' role='status' aria-live='polite'></div>
      </div>";

        private static string PlayerApproachOptionsScript() => @"
    const playerApproachOptionIds=['playerApproachesEnabled','playerAttentionTrackingEnabled','playerJealousyApproachesEnabled','playerKeepInvitationsEnabled','playerPatrolInterceptionsEnabled','playerBattleRescueDialogueEnabled','playerPrisonRescueDialogueEnabled'];
    ids.push(...playerApproachOptionIds);for(const id of playerApproachOptionIds)boolIds.add(id);
    function renderPlayerApproachOptions(){document.getElementById('playerApproachElements').disabled=!document.getElementById('playerApproachesEnabled').checked;}
    function loadPlayerApproachOptions(settings){
      for(const id of playerApproachOptionIds)document.getElementById(id).checked=settings[id]!==false;
      const status=document.getElementById('playerApproachOptionsStatus');status.className='';status.textContent='';renderPlayerApproachOptions();
    }
    async function savePlayerApproachOptions(){
      const button=document.getElementById('savePlayerApproachOptions'),status=document.getElementById('playerApproachOptionsStatus');
      const fields=Object.fromEntries(playerApproachOptionIds.map(id=>[id,document.getElementById(id).checked]));
      button.disabled=true;status.className='';status.textContent='Saving…';
      try{const response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(fields)});
        const result=await response.json();if(!response.ok||!result.ok)throw Error(result.error||'Could not save approach options.');
        status.textContent='Saved. Approach options apply to new interactions; the game checks every five seconds.';
      }catch(error){status.className='xpError';status.textContent=error.message;}finally{button.disabled=false;}
    }
    renderPlayerApproachOptions();";
    }
}
