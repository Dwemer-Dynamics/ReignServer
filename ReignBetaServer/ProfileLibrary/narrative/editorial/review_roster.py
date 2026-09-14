"""Bounded data/editorial diagnostic, without provider calls or campaign access."""
import collections
import heapq
import itertools
import json
import re
from pathlib import Path

ROOT=next(parent for parent in Path(__file__).resolve().parents if (parent/'reign.modules.json').exists())
HERE=ROOT/'.codex-build/background-rebuild-20260909/task-authored-initial'
inventory=json.loads((HERE/'draft-inventory.json').read_text())
documents=[]
issues=[]
trios=collections.defaultdict(list)
summaries=collections.defaultdict(list)
near=[]
similar_keys=[]
stop={'a','an','the','of','to','and','in','for','with','their','own','be','being','from','without','by'}
for entry in inventory['profiles']:
    document=json.loads((HERE/entry['path']).read_text())
    hero=entry['heroStringId']
    for key,value in document['life'].items():
        if len(value)<(900 if key=='summary' else 30): issues.append([hero,'short_life',key])
    if document['provenance']['remainingGenericItems']: issues.append([hero,'generic_items'])
    chosen=[item for item in document['items'] if item['id'] in document['definingIds']]
    if len(chosen)!=3 or len({x['meaningKey'] for x in chosen})!=3: issues.append([hero,'invalid_trio'])
    trios[tuple(sorted(x['meaningKey'] for x in chosen))].append(hero)
    if len(document.get('influenceLevels',[]))!=10 or 'A ruler may dearly love cats' not in document.get('realismPolicy',''): issues.append([hero,'missing_guard_metadata'])
    for left,right in itertools.combinations(chosen,2):
        a=set(left['meaningKey'].split())-stop;b=set(right['meaningKey'].split())-stop
        if a and b and len(a&b)/len(a|b)>=.5:
            similar_keys.append(dict(heroStringId=hero,name=entry['name'],left=left['title'],right=right['title'],keys=[left['meaningKey'],right['meaningKey']]))
    public=document['life']['publicSummary']
    for item in document['items']:
        if item['visibility']=='private' and (item['description'] in public or item['personalMeaning'] in public): issues.append([hero,'public_private_copy',item['id']])
    normalized=document['life']['summary'].replace(entry['name'],'NAME')
    summaries[normalized].append(hero)
    words=re.findall(r'[a-z]+',normalized.lower())
    shingles={' '.join(words[i:i+4]) for i in range(len(words)-3)}
    documents.append((hero,entry['name'],shingles))
for left,right in itertools.combinations(documents,2):
    overlap=len(left[2]&right[2]);score=overlap/(len(left[2])+len(right[2])-overlap)
    row=(score,left[0],right[0],left[1],right[1])
    if len(near)<20: heapq.heappush(near,row)
    elif row>near[0]:heapq.heapreplace(near,row)
report=dict(schema='reign-roster-editorial-diagnostic-v1',profileCount=len(documents),issues=issues,
    duplicateSummaries=[v for v in summaries.values() if len(v)>1],duplicateTrios=[v for v in trios.values() if len(v)>1],
    similarDefiningMeanings=similar_keys,closestSummaries=sorted(near,reverse=True),providerCalls=0,
    scope='Structure, verbatim privacy, exact duplication and diagnostic similarity only. Similarity flags require editorial judgment; this does not promote drafts.')
(HERE/'roster-editorial-diagnostic.json').write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps(report))
assert not issues
