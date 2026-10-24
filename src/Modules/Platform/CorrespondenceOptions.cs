using System;
using System.Collections.Generic;
using System.Globalization;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] CorrespondenceOptionKeys = {
            "correspondenceFrequencyMultiplier", "correspondenceTravelTimeMultiplier"
        };

        private static bool ValidCorrespondenceOption(string key, object raw)
        {
            if (!(raw is byte || raw is short || raw is int || raw is long
                || raw is float || raw is double || raw is decimal)) return false;
            double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return key == "correspondenceTravelTimeMultiplier"
                ? CorrespondenceTransitPolicy.IsValidTravelMultiplier(value)
                : double.IsFinite(value) && value >= 0 && value <= 5
                    && Math.Abs(value * 4 - Math.Round(value * 4)) < 1e-8;
        }

        private static string ValidateCorrespondenceOptions(Dictionary<string, object> incoming)
        {
            if (incoming != null) foreach (string key in CorrespondenceOptionKeys)
                if (incoming.TryGetValue(key, out var raw) && !ValidCorrespondenceOption(key, raw))
                    return key == "correspondenceTravelTimeMultiplier"
                        ? "Letter travel time must be from 0.25 to 4 in steps of 0.25."
                        : "Letter frequency must be from 0 to 5 in steps of 0.25.";
            return "";
        }

        private static double CorrespondenceOption(Dictionary<string, object> settings, string key)
            => settings != null && settings.TryGetValue(key, out var raw) && ValidCorrespondenceOption(key, raw)
                ? Convert.ToDouble(raw, CultureInfo.InvariantCulture) : 1d;

        private static int AutonomousLetterChance(int affinity, double frequency)
            => (int)Math.Round(Math.Clamp(affinity * frequency, 0d, 100d), MidpointRounding.AwayFromZero);

        private static List<Dictionary<string, object>> RunCorrespondenceOptionsAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "correspondence_options_" + id,
                "suite", "interaction_architecture", "passed", passed, "summary", id));
            var legacy = ReignXpOptionsForClient(TestDict("private_fixture", "retain"));
            Check("defaults_and_public_projection", Convert.ToDouble(legacy[CorrespondenceOptionKeys[0]]) == 1
                && Convert.ToDouble(legacy[CorrespondenceOptionKeys[1]]) == 1 && !legacy.ContainsKey("private_fixture"));
            foreach (string key in CorrespondenceOptionKeys)
            {
                Check(key + "_bounds", ValidateCorrespondenceOptions(TestDict(key, .25d)) == ""
                    && ValidateCorrespondenceOptions(TestDict(key, key == CorrespondenceOptionKeys[0] ? 5d : 4d)) == ""
                    && (ValidateCorrespondenceOptions(TestDict(key, 0d)) == "") == (key == CorrespondenceOptionKeys[0]));
                object[] invalid = { null, false, "1", -.25d, .3d, 5.25d, double.NaN, double.PositiveInfinity, TestDict() };
                bool rejected = true;
                foreach (object value in invalid) rejected &= ValidateCorrespondenceOptions(TestDict(key, value)).Length > 0
                    && CorrespondenceOption(TestDict(key, value), key) == 1;
                Check(key + "_rejects_invalid", rejected);
            }
            var settings = DefaultSettings();
            settings["private_fixture"] = "retain";
            MergeProviderSettings(settings, TestDict(CorrespondenceOptionKeys[0], 0d));
            MergeProviderSettings(settings, TestDict(CorrespondenceOptionKeys[1], .5d));
            var restored = TryParseJsonObject(Json.Serialize(settings));
            Check("independent_partial_merge_roundtrip", CorrespondenceOption(restored, CorrespondenceOptionKeys[0]) == 0
                && CorrespondenceOption(restored, CorrespondenceOptionKeys[1]) == .5
                && ReadString(restored, "private_fixture", "") == "retain");
            Check("frequency_chances_and_cap", AutonomousLetterChance(50, .5) == 25
                && AutonomousLetterChance(20, 2) == 40 && AutonomousLetterChance(50, 5) == 100
                && AutonomousLetterChance(100, 0) == 0 && AutonomousLetterChance(19, .25) == 5);
            return results;
        }

        private static string CorrespondenceOptionsHtml() => @"
      <div class='xpOptions' style='margin-top:24px'>
        <h3>Correspondence</h3>
        <div class='xpRow'><label for='correspondenceFrequencyMultiplier'>Automatic letter frequency</label>
          <input id='correspondenceFrequencyMultiplier' type='range' min='0' max='5' step='0.25' value='1' oninput='renderCorrespondenceOptions()' aria-describedby='correspondenceFrequencyExamples'>
          <output id='correspondenceFrequencyMultiplierValue' for='correspondenceFrequencyMultiplier'>1×</output></div>
        <p id='correspondenceFrequencyExamples' aria-live='polite'></p>
        <p class='xpHelp'>Eligible characters check once every 3 campaign days. After sending, each waits a random 3–15 days before becoming eligible again. Known characters need at least +10 affinity; introductions need +20, with at most one introduction per 3 days. These are chances per eligible check, not guaranteed deliveries. Replies and event letters keep their own triggers.</p>
        <div class='xpRow'><label for='correspondenceTravelTimeMultiplier'>Letter travel time</label>
          <input id='correspondenceTravelTimeMultiplier' type='range' min='0.25' max='4' step='0.25' value='1' oninput='renderCorrespondenceOptions()' aria-describedby='correspondenceTravelExamples'>
          <output id='correspondenceTravelTimeMultiplierValue' for='correspondenceTravelTimeMultiplier'>1×</output></div>
        <p id='correspondenceTravelExamples' aria-live='polite'></p>
        <p class='xpHelp'>1× keeps the current courier speed. Lower values shorten journeys; higher values lengthen them. Changes apply across campaigns to newly dispatched letters, including replies. Letters already travelling keep their arrival dates. Command reports remain immediate. Deliveries are checked hourly while campaign time advances.</p>
        <button id='saveCorrespondenceOptions' onclick='saveCorrespondenceOptions()'>Save correspondence options</button>
        <div id='correspondenceOptionsStatus' role='status' aria-live='polite'></div>
      </div>";

        private static string CorrespondenceOptionsScript() => @"
    const correspondenceOptionIds=['correspondenceFrequencyMultiplier','correspondenceTravelTimeMultiplier'];
    ids.push(...correspondenceOptionIds);
    function renderCorrespondenceOptions(){
      const frequency=Number(document.getElementById(correspondenceOptionIds[0]).value);
      const travel=Number(document.getElementById(correspondenceOptionIds[1]).value);
      for(const id of correspondenceOptionIds)document.getElementById(id+'Value').textContent=document.getElementById(id).value+'×';
      document.getElementById('correspondenceFrequencyExamples').textContent=frequency===0
        ? 'Automatic personal letters and introductions are off. Replies and event letters continue.'
        : [20,50,100].map(affinity=>'+'+affinity+' affinity: '+Math.round(Math.min(100,affinity*frequency))+'% chance per eligible check').join('; ')+'.';
      const hours=days=>Number((days*24*travel).toFixed(2));
      document.getElementById('correspondenceTravelExamples').textContent='A journey currently taking 6 hours → '+hours(.25)+' hours; 18 hours → '+hours(.75)+' hours; 2 days → '+hours(2)+' hours.';
    }
    function loadCorrespondenceOptions(settings){
      for(const id of correspondenceOptionIds){const v=settings[id],min=id==='correspondenceFrequencyMultiplier'?0:.25,max=id==='correspondenceFrequencyMultiplier'?5:4;
        document.getElementById(id).value=typeof v==='number'&&Number.isFinite(v)&&v>=min&&v<=max&&Math.abs(v*4-Math.round(v*4))<.000001?v:1;}
      const status=document.getElementById('correspondenceOptionsStatus');status.className='';status.textContent='';renderCorrespondenceOptions();
    }
    async function saveCorrespondenceOptions(){
      const button=document.getElementById('saveCorrespondenceOptions'),status=document.getElementById('correspondenceOptionsStatus');
      const fields=Object.fromEntries(correspondenceOptionIds.map(id=>[id,Number(document.getElementById(id).value)]));
      button.disabled=true;status.className='';status.textContent='Saving…';
      try{const response=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(fields)});
        const result=await response.json();if(!response.ok||!result.ok)throw Error(result.error||'Could not save correspondence options.');
        status.textContent='Saved. Correspondence options apply to new checks and dispatches.';
      }catch(error){status.className='xpError';status.textContent=error.message;}finally{button.disabled=false;}
    }
    renderCorrespondenceOptions();";
    }
}
