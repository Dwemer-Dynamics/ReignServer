using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using EconomyOptions = Reign.Core.Contracts.Economy.WarEconomyOptions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static EconomyOptions ReadWarEconomyOptions(Dictionary<string, object> settings)
        {
            var known = (settings ?? new Dictionary<string, object>()).Where(p => EconomyOptions.Switches.ContainsKey(p.Key) || EconomyOptions.Numbers.ContainsKey(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            return EconomyOptions.TryCreate(known, false, out var options, out _) ? options : EconomyOptions.Defaults;
        }
        private static string ValidateWarEconomyPatch(Dictionary<string, object> settings, Dictionary<string, object> incoming, bool requireRevision = false)
        {
            if (incoming.TryGetValue("expectedSettingsRevision", out var revision)
                && (!(revision is string) || (string)revision != ReadString(settings, "settingsRevision", "")))
                return "Settings changed since this page loaded. Refresh before saving.";
            var merged = ReadWarEconomyOptions(settings).Values();
            foreach (var pair in incoming.Where(p => p.Key.StartsWith(EconomyOptions.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                if (!merged.ContainsKey(pair.Key)) return "Unknown War Economy option: " + pair.Key;
                merged[pair.Key] = pair.Value;
            }
            if(!EconomyOptions.TryCreate(merged,true,out var candidate,out var error))return error;
            var current=ReadWarEconomyOptions(settings).Values();
            if(requireRevision && !incoming.ContainsKey("expectedSettingsRevision")
                && candidate.Values().Any(p=>!Equals(p.Value,current[p.Key])))
                return "Refresh Options before changing economy settings; this page has no settings revision.";
            return "";
        }
        private static Dictionary<string, object> WarEconomyOptionsForClient(Dictionary<string, object> settings)
            => TestDict("version", EconomyOptions.Version, "revision", ReadString(settings, "settingsRevision", ""), "values", ReadWarEconomyOptions(settings).Values());
        private static string WarEconomyOptionsHtml()
        {
            var html = new StringBuilder(@"<div class='xpOptions' id='warEconomyOptions'><h3>War economy</h3>
<p class='xpHelp'>Tune future costs and output across campaigns. Saved values are retained when a section is switched off. Changes refresh in the game within five seconds; the last valid settings remain available offline. Existing soldiers, damage, payments and agreements are not reset.</p>
<style>#warEconomyOptions{box-sizing:border-box;width:100%}#warEconomyOptions details{border-top:1px solid #352C20;padding:14px 0}#warEconomyOptions summary{color:#A88A54;cursor:pointer;padding:6px 0}#warEconomyOptions .economyFields{border:0;padding:0;margin:0;display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:14px}#warEconomyOptions .economyNumber{display:flex;flex-direction:column;gap:6px}#warEconomyOptions input[type=number]{box-sizing:border-box;width:100%;background:#0B0B0B;color:#C5BDAF;border:1px solid #7E6A4D;padding:8px}#warEconomyOptions fieldset:disabled{color:#4A4945}#warEconomyOptions .economyPreview{white-space:normal;line-height:1.6}#warEconomyOptionsStatus{min-height:1.5em;margin-top:12px}@media(max-width:600px){#warEconomyOptions .economyFields{grid-template-columns:1fr}}</style>");
            void Toggle(string key)
            {
                html.Append("<label class='xpRow'><input type='checkbox' id='").Append(key).Append("'");
                if (EconomyOptions.Defaults.Enabled(key)) html.Append(" checked");
                html.Append(" onchange='renderWarEconomyOptions()'> ").Append(WebUtility.HtmlEncode(EconomyOptions.Switches[key])).Append("</label>");
            }
            void Help(string help) => html.Append("<p class='xpHelp'>").Append(WebUtility.HtmlEncode(help)).Append("</p>");
            void Fields(string toggles, Func<string, bool> numberFilter)
            {
                html.Append("<fieldset class='economyFields' data-economy-toggle='").Append(toggles).Append("'>");
                foreach (var pair in EconomyOptions.Numbers.Where(p => numberFilter(p.Key)))
                {
                    var spec = pair.Value;
                    string N(double n) => n.ToString(CultureInfo.InvariantCulture);
                    html.Append("<label class='economyNumber' for='").Append(pair.Key).Append("'>").Append(WebUtility.HtmlEncode(spec.Label));
                    string type = pair.Key == "economyVillageOutputScale" ? "range" : "number";
                    html.Append("<input type='").Append(type).Append("' id='").Append(pair.Key).Append("' min='").Append(N(spec.Minimum)).Append("' max='").Append(N(spec.Maximum))
                        .Append("' step='").Append(N(spec.Step)).Append("' value='").Append(N(spec.Default)).Append("' oninput='renderWarEconomyOptions()'>");
                    if (type == "range") html.Append("<output id='economyVillageOutputScaleValue' for='economyVillageOutputScale'>1×</output>");
                    html.Append("</label>");
                }
                html.Append("</fieldset>");
            }
            void Group(string title, string toggle, string help, Func<string, bool> numberFilter)
            {
                html.Append("<details><summary>").Append(WebUtility.HtmlEncode(title)).Append("</summary>");
                Toggle(toggle);
                Help(help);
                Fields(toggle, numberFilter);
                html.Append("</details>");
            }
            Group("Troop wages", "economyTroopWagesEnabled", "Wage schedule for regular troops in player, lord, patrol and garrison parties. Garrisons keep their existing quartering and other reductions. Mercenaries retain their native 50% premium; negotiated hero salaries and civilian parties keep their existing treatment. In-game quotes include the paying party’s modifiers. Off: native base wages for military troops.", k => k.StartsWith("economyTroopWageTier", StringComparison.Ordinal));
            Group("Recruitment and upgrades", "economyRecruitPricesEnabled", "Multipliers on the native discounted recruitment quote. Native upgrade prices derive from these recruitment prices once. Ransom prices are excluded. Off: native recruitment and upgrade quotes. Higher tiers must not have lower multipliers.", k => k.StartsWith("economyRecruitTier", StringComparison.Ordinal));
            Group("Village output", "economyVillageCurveEnabled", "The scale and hearth anchors affect bound food, village goods and supplementary food together. Output continues above 1,200 hearths at the final slope. Raid recovery still applies once; non-functioning villages produce nothing. Off: native hearth bands, with existing recovery and other modifiers retained.", k => k.StartsWith("economyVillage", StringComparison.Ordinal));
            Group("Raid food disruption", "economyRaidFoodEnabled", "Actual raid damage disrupts food only, including fishing boats, nets and shore capacity. No extra hearth loss. Recovery accrues during functioning days; negative bound food change or security below 50 slows it once. Full looting recovers more slowly. At zero fishing capacity, existing voyages may finish and unload before boats wait ashore. Switching off retains damage and restores native fishing movement and catches.", k => k.StartsWith("economyRaid", StringComparison.Ordinal));
            Group("Garrison wages", "economyGarrisonWagesEnabled", "Multiply the regular garrison stack wage from the troop schedule, retaining native perks and Clan Accord reductions. Full garrison strength is preserved. Off: remove this quartering discount; the chosen troop wage schedule still applies.", k => k == "economyGarrisonWageFactor");
            Group("Garrison food", "economyGarrisonFoodEnabled", "More soldiers per food means lower garrison consumption. Native siege, building and governor effects still apply. Off: the native baseline of 20 soldiers per food.", k => k == "economyGarrisonMenPerFood");
            Group("Automatic spending reserves", "economyReservesEnabled", "Days of clan military wages retained before automatic recruitment or upgrades. Off: no precautionary reserve. Actual payer cash, outstanding recruitment liabilities, native budgets, food and capacity remain required. Manual player purchases never require this buffer.", k => k.EndsWith("ReserveDays", StringComparison.Ordinal));
            const string caravans = "economyNpcTradeCaravansEnabled", convoys = "economyNpcTradeConvoysEnabled";
            html.Append("<details id='npcClanTradeOptions'><summary>NPC Clan Trade</summary>");
            Help("Clans pay one formation fee to the town, with native cultural discounts. The game supplies the same trading cash as it does for the player's standard caravan or convoy. Merchant crews run these parties without a noble captain. Convoys require War Sails and a suitable port.");
            Toggle(caravans);
            Fields(caravans, k => k == "economyNpcTradeCaravanPrice" || k == "economyNpcTradeCaravanLimit");
            Toggle(convoys);
            Fields(convoys, k => k == "economyNpcTradeConvoyPrice" || k == "economyNpcTradeConvoyLimit");
            Help("The fee must be below the chosen share of clan cash and leave the military reserve. Each eligible clan gets one roll per day. Reign allows up to eight active sponsored trade parties worldwide. Disabling a mode or lowering its limit only affects future launches.");
            Fields(caravans + " " + convoys, k => k == "economyNpcTradeCashPercent" || k == "economyNpcTradeDailyChance");
            html.Append("</details>");
            return html.Append("<p id='warEconomyPreview' class='economyPreview xpHelp' aria-live='polite'></p><button type='button' onclick='resetWarEconomyOptions()'>Reset economy defaults</button> <button type='button' id='saveWarEconomyOptions' disabled onclick='saveWarEconomyOptions()'>Save economy options</button><div id='warEconomyOptionsStatus' role='status' aria-live='polite'></div></div>").ToString();
        }
        private static string WarEconomyOptionsScript() => "const economyDefaults=" + Json.Serialize(EconomyOptions.DefaultValues())
            + ";const economySwitches=" + Json.Serialize(EconomyOptions.Switches.Keys.ToArray())
            + ";const economyNumbers=" + Json.Serialize(EconomyOptions.Numbers.Select(p => TestDict("key", p.Key, "label", p.Value.Label, "min", p.Value.Minimum, "max", p.Value.Maximum, "step", p.Value.Step)).ToArray()) + @";
let warEconomyLoaded=false,warEconomyRevision='',warEconomyBaseline={},warEconomySaving=false;
function economySaveButtons(){const economy=document.getElementById('saveWarEconomyOptions'),global=document.getElementById('saveChangesButton');if(economy)economy.disabled=warEconomySaving||!warEconomyLoaded;if(global)global.disabled=warEconomySaving;}
function beginWarEconomySave(){if(warEconomySaving)return false;warEconomySaving=true;economySaveButtons();return true;}
function endWarEconomySave(){warEconomySaving=false;economySaveButtons();}
function renderWarEconomyOptions(){
  document.querySelectorAll('#warEconomyOptions fieldset').forEach(f=>f.disabled=!f.dataset.economyToggle.split(' ').some(key=>document.getElementById(key).checked));
  document.getElementById('economyVillageOutputScaleValue').textContent=document.getElementById('economyVillageOutputScale').value+'×';
  const wages=Array.from({length:8},(_,i)=>document.getElementById('economyTroopWageTier'+i).value);
  const stress=Number(document.getElementById('economyRaidStressSpeed').value),partial=Number(document.getElementById('economyRaidPartialDays').value),full=Number(document.getElementById('economyRaidFullDays').value);
  document.getElementById('warEconomyPreview').textContent='Troop wages (tiers 0–7+): '+(document.getElementById('economyTroopWagesEnabled').checked?wages.join(' / '):'native 1 / 2 / 3 / 5 / 8 / 12 / 17 / 23')+'. Village scale: '+(document.getElementById('economyVillageCurveEnabled').checked?document.getElementById('economyVillageOutputScale').value+'×':'native hearth bands')+'. Stressed raid recovery from zero: '+(stress>0?(partial/stress).toFixed(1)+' / '+(full/stress).toFixed(1)+' functioning days (partial/full)':'invalid speed')+'.';
}
function loadWarEconomyOptions(settings){
  economySwitches.forEach(k=>document.getElementById(k).checked=typeof settings[k]==='boolean'?settings[k]:economyDefaults[k]);
  economyNumbers.forEach(s=>{const e=document.getElementById(s.key);e.value=typeof settings[s.key]==='number'?settings[s.key]:economyDefaults[s.key];e.setCustomValidity('');});
  warEconomyRevision=typeof settings.settingsRevision==='string'?settings.settingsRevision:'';
  warEconomyBaseline={};economySwitches.forEach(k=>warEconomyBaseline[k]=document.getElementById(k).checked);economyNumbers.forEach(s=>warEconomyBaseline[s.key]=Number(document.getElementById(s.key).value));
  warEconomyLoaded=true;economySaveButtons();renderWarEconomyOptions();
}
function resetWarEconomyOptions(){const loaded=warEconomyLoaded,revision=warEconomyRevision,baseline=warEconomyBaseline;loadWarEconomyOptions(economyDefaults);warEconomyRevision=revision;warEconomyBaseline=baseline;warEconomyLoaded=loaded;document.getElementById('saveWarEconomyOptions').disabled=!loaded;}
function warEconomySettingsForSave(){
  if(!warEconomyLoaded)throw Error('Load War Economy options before saving.');
  const patch={};economySwitches.forEach(k=>patch[k]=document.getElementById(k).checked);
  economyNumbers.forEach(s=>{const e=document.getElementById(s.key),v=Number(e.value),steps=(v-s.min)/s.step;if(e.value.trim()===''||!Number.isFinite(v)||v<s.min||v>s.max||Math.abs(steps-Math.round(steps))>.000001)throw Error('Enter a value in the shown range and step for '+s.label+'.');patch[s.key]=v;});
  for(let i=1;i<8;i++)if(patch['economyTroopWageTier'+i]<patch['economyTroopWageTier'+(i-1)]||patch['economyRecruitTier'+i]<patch['economyRecruitTier'+(i-1)])throw Error('Higher tiers cannot have a lower wage or recruitment multiplier.');
  const a=patch.economyVillageFood10,b=patch.economyVillageFood300,c=patch.economyVillageFood600,d=patch.economyVillageFood1200;
  if(a>=b||b>c||c>d||(d-c)/600>(c-b)/300+.000001)throw Error('Village output must rise to 300 hearths, never decrease, and grow no faster above 600.');
  if(patch.economyRaidFullDays<patch.economyRaidPartialDays)throw Error('Full looting recovery must not be shorter than partial raid recovery.');
  return patch;
}
function warEconomyPatchForSave(){const all=warEconomySettingsForSave(),patch={expectedSettingsRevision:warEconomyRevision};Object.keys(all).forEach(k=>{if(all[k]!==warEconomyBaseline[k])patch[k]=all[k];});return patch;}
async function saveWarEconomyOptions(){
  if(!beginWarEconomySave())return;
  const status=document.getElementById('warEconomyOptionsStatus');status.className='';status.textContent='Saving…';
  try{const patch=warEconomyPatchForSave();if(Object.keys(patch).length===1){status.textContent='No changes to save.';return;}const response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(patch)}),result=await response.json();if(!response.ok||result.ok===false)throw Error(result.error||'Could not save economy options.');loadWarEconomyOptions(result);status.textContent='Saved. The game refreshes within five seconds. Existing commitments retain their terms.';}
  catch(error){status.className='xpError';status.textContent=error.message;}finally{endWarEconomySave();}
}
fetch('/api/settings').then(r=>{if(!r.ok)throw Error('load');return r.json();}).then(loadWarEconomyOptions).catch(()=>{document.getElementById('warEconomyOptionsStatus').textContent='Could not load economy options. Refresh before saving.';});";
    }
}
