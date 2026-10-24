using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Reign.Core.Contracts.World;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> AnnouncementOptionsForClient(Dictionary<string, object> settings) =>
            ReignNewsCategories.Switches.Keys.ToDictionary(k => k, k => (object)ReadBool(settings, k, true), StringComparer.Ordinal);

        private static string ValidateAnnouncementOptions(Dictionary<string, object> incoming)
        {
            foreach (var pair in incoming.Where(p => p.Key.StartsWith("announcement", StringComparison.Ordinal)))
            {
                if (!ReignNewsCategories.Switches.ContainsKey(pair.Key)) return "Unknown announcement option: " + pair.Key;
                if (!(pair.Value is bool)) return ReignNewsCategories.Switches[pair.Key] + " must be on or off.";
            }
            return "";
        }

        private static string AnnouncementOptionsHtml()
        {
            var html = new StringBuilder("<div class='xpOptions' id='announcementOptions'><h3>Announcements</h3><p class='xpHelp'>Hear the news of the realm in a town-crier popup. Campaign time pauses until you acknowledge each report. Reports wait until you return to the campaign map.</p>");
            foreach (var pair in ReignNewsCategories.Switches)
                html.Append("<label class='xpRow'><input type='checkbox' checked id='").Append(pair.Key)
                    .Append("' onchange='renderAnnouncementOptions()'> ").Append(WebUtility.HtmlEncode(pair.Value)).Append("</label>");
            return html.Append("<p class='xpHelp'>All types default to on. Ordinary captures never create a popup. Turn a type off to keep its news in the normal text window; this changes announcements only, not events or their consequences. Unread popups of a disabled type are cleared, so switching it back on starts with new reports. Preferences apply across campaigns and refresh in the game within five seconds.</p><div id='announcementOptionsPreview' class='xpHelp' aria-live='polite'></div><button type='button' onclick='resetAnnouncementOptions()'>Reset to defaults</button> <button type='button' id='saveAnnouncementOptions' disabled onclick='saveAnnouncementOptions()'>Save announcement options</button><div id='announcementOptionsStatus' role='status' aria-live='polite'></div></div>").ToString();
        }

        private static string AnnouncementOptionsScript() => "const announcementSwitchIds=" + Json.Serialize(ReignNewsCategories.Switches.Keys.ToArray()) + @";
ids.push(...announcementSwitchIds);announcementSwitchIds.forEach(k=>boolIds.add(k));
let announcementOptionsLoaded=false;
function renderAnnouncementOptions(){const enabled=document.getElementById('announcementPopupsEnabled').checked;document.getElementById('announcementOptionsPreview').textContent=enabled?'Selected reports pause time and open as popups. All reports also appear in the text window.':'All announcements use the normal text window.';}
function loadAnnouncementOptions(settings){announcementSwitchIds.forEach(k=>document.getElementById(k).checked=settings[k]!==false);announcementOptionsLoaded=true;document.getElementById('saveAnnouncementOptions').disabled=false;renderAnnouncementOptions();}
function resetAnnouncementOptions(){announcementSwitchIds.forEach(k=>document.getElementById(k).checked=true);renderAnnouncementOptions();}
async function saveAnnouncementOptions(){const button=document.getElementById('saveAnnouncementOptions'),status=document.getElementById('announcementOptionsStatus');if(!announcementOptionsLoaded){status.textContent='Refresh to load announcement options before saving.';return;}button.disabled=true;status.textContent='Saving…';try{const patch={};announcementSwitchIds.forEach(k=>patch[k]=document.getElementById(k).checked);const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(patch)});const data=await r.json();if(!r.ok||data.ok===false)throw new Error(data.error||'Could not save announcement options');status.textContent='Saved. The game refreshes within five seconds.';}catch(e){status.textContent=e.message;}finally{button.disabled=false;}}
";
    }
}
