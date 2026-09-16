"""Prepare reviewed source content for validation; never deploy or call a provider."""
import copy
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

ROOT=next(parent for parent in Path(__file__).resolve().parents if (parent/'reign.modules.json').exists())
LIB=ROOT/'ReignBetaServer/ProfileLibrary'
DRAFT=ROOT/'.codex-build/background-rebuild-20260909/task-authored-initial'
def read(path): return json.loads(path.read_text(encoding='utf-8-sig'))
def encode(value): return json.dumps(value,ensure_ascii=False,indent=2)+'\n'

def main():
    baseline=read(LIB/'profile_catalog_v4.json')
    assert read(LIB/'profile_catalog.json')==baseline, 'Shipping source changed; review before replacing it.'
    inventory=read(DRAFT/'draft-inventory.json')
    editorial=read(DRAFT/'roster-editorial-diagnostic.json')
    personality=read(DRAFT/'draft-personality-audit.json')
    assert inventory['draftCount']==len(baseline['profiles'])==1117
    assert not editorial['issues'] and not editorial['duplicateSummaries'] and not editorial['duplicateTrios']
    assert not editorial['similarDefiningMeanings'], 'Resolve diagnostic flags before preparing the candidate.'
    assert all(personality[key]==0 for key in ['incompatibleDreamCount','incompatibleConcernCount','incompatibleFactCount'])
    assert not personality['missingLifeOrVoice']
    audit_time=min((DRAFT/name).stat().st_mtime_ns for name in ['roster-editorial-diagnostic.json','draft-personality-audit.json'])
    assert all((DRAFT/entry['path']).stat().st_mtime_ns<=audit_time for entry in inventory['profiles']), 'Drafts changed after audit.'
    destination=LIB/'narrative/profiles'
    assert not destination.exists() or not any(destination.iterdir()), 'Existing narrative source requires explicit review.'
    by_id={entry['heroStringId']:entry for entry in inventory['profiles']}
    candidate=copy.deepcopy(baseline)
    candidate['packVersion']='reign_profiles_v5'
    candidate['generatedUtc']=datetime.now(timezone.utc).isoformat()
    candidate['manifest']['narrativeAuthorship']={
        'method':'task_authored_local_composition_v1','initialConstructionProviderCalls':0,
        'hobbyDetails':664,'concernMeanings':2096,
        'review':'Full data audits and focused editorial cohort; final package and dialogue validation required.'}
    prepared=[]
    for profile in candidate['profiles']:
        hero=profile['heroStringId']
        document=read(DRAFT/by_id[hero]['path'])
        assert document['heroStringId']==hero and document['status']=='editorial_draft'
        expected=hashlib.sha256(json.dumps(profile['sourceFacts'],sort_keys=True).encode()).hexdigest()
        assert document['provenance']['sourceFactsHash']==expected
        assert document['provenance']['providerCalls']==0 and not document['provenance']['remainingGenericItems']
        document['status']='ready'
        document['provenance']['editorialStatus']='composition_reviewed_pending_release_validation'
        name=hashlib.sha256(hero.encode()).hexdigest()[:24].upper()+'.json'
        profile['packVersion']='reign_profiles_v5'
        profile['narrativeFile']=name
        prepared.append((name,encode(document)))
    # Finish all checks before touching source. No installed paths are involved.
    destination.mkdir(parents=True,exist_ok=True)
    for name,content in prepared: (destination/name).write_text(content,encoding='utf-8')
    # Preserve compact catalog storage; profiles carry the readable prose files.
    (LIB/'profile_catalog.json').write_text(json.dumps(candidate,ensure_ascii=False,separators=(',',':'))+'\n',encoding='utf-8')
    print(json.dumps(dict(preparedProfiles=len(prepared),packVersion=candidate['packVersion'],deployed=False,
        validationRequired=True,initialConstructionProviderCalls=0)))

if __name__=='__main__':main()
