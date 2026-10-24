using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly object NpcStoryEditorLock = new();
        private static Dictionary<string, object> NpcStoryEditorApi(string operation, Dictionary<string, object> p)
        {
            lock (NpcStoryEditorLock)
            {
                var all = ReadNpcStoryDefinitions();
                if (operation == "editor-list") return TestDict("ok", true, "schema", "reign-npc-stories-v1", "definitions", all);
                if (operation != "editor-save") return TestDict("ok", false, "error", "Unknown story editor operation.");
                try
                {
                    string json = ReadString(p, "definitionJson", "");
                    if (json.Length == 0 || json.Length > 131072) throw new InvalidOperationException("A definition must contain 1–131072 characters.");
                    var definition = Json.Deserialize<NpcStoryDefinition>(json);
                    var errors = NpcStoryCatalog.Validate(new[] { definition });
                    if (errors.Count > 0) return TestDict("ok", false, "errors", errors, "error", string.Join(" ", errors));
                    errors = NpcStoryCatalog.ValidateLinks(all.Where(d => d.Id != definition.Id).Concat(new[] { definition }));
                    if (errors.Count > 0) return TestDict("ok", false, "errors", errors, "error", string.Join(" ", errors));
                    var prior = all.FirstOrDefault(d => d.Id == definition.Id);
                    int expected = ReadInt(p, "expectedVersion", 0);
                    if ((prior?.Version ?? 0) != expected) throw new InvalidOperationException("This definition changed. Reload it before saving.");
                    if (definition.Version != expected + 1) throw new InvalidOperationException("Use version " + (expected + 1) + " for this revision.");
                    string path = Path.Combine(DataDir, "npc-lives-stories.json");
                    var edits = File.Exists(path) ? Json.Deserialize<List<NpcStoryDefinition>>(File.ReadAllText(path, Encoding.UTF8)) : new List<NpcStoryDefinition>();
                    edits.RemoveAll(d => d.Id == definition.Id); edits.Add(definition);
                    string staged = path + ".new";
                    File.WriteAllText(staged, Json.Serialize(edits.OrderBy(d => d.Id).ToList()), new UTF8Encoding(false));
                    File.Move(staged, path, true);
                    return TestDict("ok", true, "id", definition.Id, "version", definition.Version,
                        "note", "Saved for future stories. Active stories retain their accepted definition and version.");
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is System.Text.Json.JsonException)
                { return TestDict("ok", false, "error", ex.Message); }
            }
        }
        private static string NpcStoryEditorHtml() => @"<details class='xpOptions' style='margin-top:24px'>
<summary>Personal story editor</summary>
<p class='xpHelp'>Edit prompts, objectives, locations, rewards and lasting facts. Saved revisions apply to future instances. Current stories retain their accepted content.</p>
<button type='button' onclick='loadNpcStoryEditor()'>Load catalogue</button>
<label for='npcStoryFilter'>Find a story</label><input id='npcStoryFilter' placeholder='Title or family' oninput='filterNpcStoryEditor()'>
<select id='npcStorySelect' aria-label='Personal story definition' onchange='selectNpcStoryEditor()'></select>
<label for='npcStoryJson'>Versioned definition</label><textarea id='npcStoryJson' rows='22' style='width:100%;box-sizing:border-box' spellcheck='false'></textarea>
<button type='button' onclick='previewNpcStoryEditor()'>Preview prompts</button>
<button type='button' onclick='saveNpcStoryEditor()'>Validate and save next version</button>
<button type='button' onclick='exportNpcStoryEditor()'>Export definition</button>
<label>Import definition<input type='file' accept='application/json,.json' onchange='importNpcStoryEditor(this.files[0])'></label>
<pre id='npcStoryPreview' style='white-space:pre-wrap'></pre><p id='npcStoryEditorStatus' role='status' aria-live='polite'></p></details>";
        private static string NpcStoryEditorScript() => @"
let npcStoryDefinitions=[],npcStoryExpectedVersion=0;
async function npcStoryEditorCall(operation,payload){const r=await fetch('/npc-lives/'+operation,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload||{})});const j=await r.json();if(!r.ok||!j.ok)throw new Error(j.error||'Story editor unavailable.');return j;}
function npcStoryEditorStatus(text){document.getElementById('npcStoryEditorStatus').textContent=text;}
async function loadNpcStoryEditor(){try{const data=await npcStoryEditorCall('editor-list',{});npcStoryDefinitions=data.definitions;filterNpcStoryEditor();npcStoryEditorStatus(npcStoryDefinitions.length+' definitions loaded.');}catch(e){npcStoryEditorStatus(e.message);}}
function filterNpcStoryEditor(){const q=document.getElementById('npcStoryFilter').value.toLowerCase(),select=document.getElementById('npcStorySelect');select.replaceChildren();npcStoryDefinitions.filter(d=>(d.Title+' '+d.Family+' '+d.Id).toLowerCase().includes(q)).forEach(d=>{const o=document.createElement('option');o.value=d.Id;o.textContent=d.Title+' — '+d.Category;select.append(o);});selectNpcStoryEditor();}
function selectNpcStoryEditor(){const d=npcStoryDefinitions.find(d=>d.Id===document.getElementById('npcStorySelect').value);if(!d)return;npcStoryExpectedVersion=d.Version;const copy=JSON.parse(JSON.stringify(d));copy.Version=d.Version+1;document.getElementById('npcStoryJson').value=JSON.stringify(copy,null,2);previewNpcStoryEditor();}
function previewNpcStoryEditor(){try{const d=JSON.parse(document.getElementById('npcStoryJson').value);document.getElementById('npcStoryPreview').textContent=(d.Phases||[]).map(p=>p.Title+'\nAffinity '+p.MinimumAffinity+'; '+p.LocationKind+'; '+p.Xp+' '+p.Skill+' XP; '+p.Affinity+' affinity\n'+p.Prompt+'\nObjectives: '+(p.Objectives||[]).map(o=>o.Kind+': '+o.Instruction).join('\n')).join('\n\n');}catch(e){npcStoryEditorStatus('Invalid JSON: '+e.message);}}
async function saveNpcStoryEditor(){try{const j=await npcStoryEditorCall('editor-save',{definitionJson:document.getElementById('npcStoryJson').value,expectedVersion:npcStoryExpectedVersion});npcStoryEditorStatus(j.note);npcStoryExpectedVersion=j.version;const d=JSON.parse(document.getElementById('npcStoryJson').value);d.Version=j.version+1;document.getElementById('npcStoryJson').value=JSON.stringify(d,null,2);}catch(e){npcStoryEditorStatus(e.message);}}
function exportNpcStoryEditor(){try{const text=document.getElementById('npcStoryJson').value,d=JSON.parse(text),url=URL.createObjectURL(new Blob([text],{type:'application/json'})),a=document.createElement('a');a.href=url;a.download=(d.Id||'personal-story').replace(/[^a-zA-Z0-9_-]/g,'_')+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}catch(e){npcStoryEditorStatus(e.message);}}
async function importNpcStoryEditor(file){try{if(!file||file.size>131072)throw new Error('Choose a definition of at most 128 KiB.');const d=JSON.parse(await file.text()),prior=npcStoryDefinitions.find(x=>x.Id===d.Id);npcStoryExpectedVersion=prior?prior.Version:0;d.Version=npcStoryExpectedVersion+1;document.getElementById('npcStoryJson').value=JSON.stringify(d,null,2);previewNpcStoryEditor();npcStoryEditorStatus('Imported for review. Validate and save when ready.');}catch(e){npcStoryEditorStatus(e.message);}}
";
    }
}
