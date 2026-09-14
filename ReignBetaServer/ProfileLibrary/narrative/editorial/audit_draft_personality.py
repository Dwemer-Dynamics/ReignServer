"""Read-only draft coverage audit; no generation calls or per-NPC dialogue tests."""
import json
from collections import Counter
import assemble_draft as author

inventory = author.read(author.OUT/'draft-inventory.json')
incompatible = []
incompatible_concerns = []
incompatible_facts = []
low_compassion = 0
counts = Counter()
missing = []
for entry in inventory['profiles']:
    profile = author.by_id[entry['heroStringId']]
    facts = author.facts_for(profile)
    document = author.read(author.OUT/entry['path'])
    if author.personality_score(profile, facts, 'compassion') <= 40:
        low_compassion += 1
    for item in document['items']:
        if not author.facts_permit(facts,item):
            incompatible_facts.append(dict(heroStringId=entry['heroStringId'],category=item['category'],title=item['title']))
        if item['category'] in ['dream','desire','value'] and author.dream_weight(profile,facts,item)<=0:
            incompatible_concerns.append(dict(heroStringId=entry['heroStringId'],category=item['category'],title=item['title']))
        if item['category'] == 'dream':
            counts[item['id']] += 1
            if author.dream_weight(profile, facts, item) <= 0:
                incompatible.append(dict(heroStringId=entry['heroStringId'], title=item['title']))
    if not document['life'] or not document['voice']:
        missing.append(entry['heroStringId'])
report = dict(schema='reign-draft-personality-audit-v1', profileCount=len(inventory['profiles']),
              lowCompassionCount=low_compassion, incompatibleDreamCount=len(incompatible),
              incompatibleDreams=incompatible, distinctDreams=len(counts),
              incompatibleConcernCount=len(incompatible_concerns),incompatibleConcerns=incompatible_concerns,
              incompatibleFactCount=len(incompatible_facts),incompatibleFacts=incompatible_facts,
              missingLifeOrVoice=missing, providerCalls=0, readyCount=0,
              scope='Draft data only. Does not certify prose quality, defining interests, or production readiness.')
(author.OUT/'draft-personality-audit.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
print(json.dumps(report))
assert not incompatible and not incompatible_concerns and not incompatible_facts and not missing
