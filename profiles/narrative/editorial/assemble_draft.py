"""Local editorial draft assembly. No network/provider imports or calls.

This prepares review material, not a publishable catalog. Production readiness is
owned by the Reign narrative validator. Never label these drafts individually
handwritten or copy them into the shipping catalog without editorial review.
"""
from pathlib import Path
import collections
import hashlib
import json
import math
import re

HERE = Path(__file__).resolve().parent
ROOT = next(parent for parent in HERE.parents if (parent/'reign.modules.json').exists())
OUT = ROOT/'.codex-build/background-rebuild-20260909/task-authored-initial'
LIB = ROOT / 'ReignBetaServer/ProfileLibrary'
SCHEMA = 'reign-narrative-v1'

# Copy the exact runtime guard text and scale; fail if their declaration changes.
runtime_source = (ROOT/'src/Modules/Characters/CharacterNarrative.cs').read_text(encoding='utf-8-sig')
def runtime_strings(pattern):
    match = re.search(pattern, runtime_source, re.S)
    assert match, 'Canonical narrative metadata declaration changed.'
    return [json.loads(value) for value in re.findall(r'"(?:[^"\\]|\\.)*"', match.group(1))]
influence_levels = runtime_strings(r'NarrativeInfluenceLevels = \{(.*?)\};')
realism_policy = ''.join(runtime_strings(r'const string NarrativeRealismPolicy =\s*(.*?);\s*\n\s*private static'))
assert len(influence_levels)==10 and 'A ruler may dearly love cats' in realism_policy

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def digest(text):
    return hashlib.sha256(text.encode('utf-8')).hexdigest()

def draw(seed, domain):
    return int(digest(SCHEMA + '\n' + seed + '\n' + domain)[:16], 16)

def choose(values, seed, domain):
    return values[draw(seed, domain) % len(values)]

def lines(path):
    return [line.split('|') for line in path.read_text(encoding='utf-8-sig').splitlines()
            if line.strip() and not line.startswith('#')]

profiles = read(LIB / 'profile_catalog_v4.json')['profiles']
by_id = {p['heroStringId']: p for p in profiles}
context = read(LIB / 'narrative/native-context.json')
temperaments = read(HERE / 'temperaments.json')
hobby_details = {title:(detail,reward) for title,detail,reward in lines(HERE/'hobby-details.txt')}
dream_policy = read(LIB/'narrative/dream-compatibility.json')
dream_rules = {rule['id']:rule for rule in dream_policy['concepts']}
concern_rules = {rule['id']:rule for rule in read(LIB/'narrative/concern-compatibility.json')['concepts']}
fact_rules = {rule['id']:rule for rule in read(LIB/'narrative/fact-compatibility.json')['concepts']}
portraits = {}
for filename in ['child-portraits.json', 'deceased-portraits.json', 'ruler-portraits.json']:
    portraits.update(read(HERE/filename))
voice_material = read(HERE/'voice-material.json')
concepts = []
source_order = collections.defaultdict(list)
for filename in ['hobbies.txt', 'drives.txt', 'additional.txt']:
    for family, *titles in lines(LIB / 'narrative' / filename):
        source_order[filename+':'+family].extend(titles)
        category = 'hobby' if filename == 'hobbies.txt' else family.split('/')[0]
        for title in titles:
            concepts.append(dict(id=category+'_'+digest(category+'|'+title)[:16],
                                 category=category, family=family, title=title,
                                 minAge=0 if '/child_' in family else 12,
                                 maxAge=11 if '/child_' in family else 150))
expected_order=read(HERE/'source-order.json')['families']
actual_order={key:dict(count=len(titles),sha256=digest('\n'.join(titles))) for key,titles in source_order.items()}
assert actual_order==expected_order, 'Source concept order changed; review indexed prose mappings before assembly.'

RANGES = {'hobby':(3,7), 'formative':(4,8), 'dream':(2,5), 'desire':(2,5),
          'fear':(2,6), 'value':(2,5), 'attachment':(1,4), 'wound':(0,3),
          'jealousy':(0,3), 'temptation':(0,3), 'secret':(0,3)}
WEIGHTS = [5,10,15,20,20,12,8,5,3,2]
SKILLS = {'horses_livestock':'riding','physical_pursuits':'athletics',
          'woodwork':'engineering','metal_leather':'engineering',
          'social_observation':'charm','story_performance':'charm',
          'hospitality_service':'steward','travel_places':'scouting','gardens':'medicine'}
specific_meanings = {}
def add_meaning(concept, aim, meaning):
    assert concept['id'] not in specific_meanings, 'Duplicate authored meaning: '+concept['id']
    assert aim.strip() and len(meaning)>=20, 'Incomplete authored meaning: '+concept['id']
    specific_meanings[concept['id']] = (aim, meaning)
for category in ['dream', 'desire', 'value', 'fear', 'secret', 'attachment', 'wound', 'jealousy', 'temptation', 'formative']:
    ordered = [c for c in concepts if c['category'] == category and '/child_' not in c['family']][:160]
    assert len(ordered) == 160
    for index, aim, meaning in lines(HERE/(category+'-meanings.txt')):
        add_meaning(ordered[int(index)],aim,meaning)
for family_index, aim, meaning in lines(HERE/'personal-dream-meanings.txt'):
    family, index = family_index.rsplit('/',1)
    ordered = [c for c in concepts if c['family'] == 'dream/'+family]
    add_meaning(ordered[int(index)],aim,meaning)
assert all(c['id'] in specific_meanings for c in concepts if c['category']=='dream')
for filename in ['supplemental-concern-meanings.txt', 'child-concern-meanings.txt']:
    for family_index, aim, meaning in lines(HERE/filename):
        family,index=family_index.rsplit('/',1)
        ordered=[c for c in concepts if c['family']==family]
        add_meaning(ordered[int(index)],aim,meaning)
assert all(c['id'] in specific_meanings for c in concepts if c['category']!='hobby'), 'Every concern needs specific authored prose.'
assert all(c['title'] in hobby_details for c in concepts if c['category']=='hobby'), 'Every hobby needs specific authored detail.'

def facts_for(profile):
    facts = dict(profile['sourceFacts'])
    if profile['heroStringId'] in context['unspecifiedAgeHeroIds']:
        facts.pop('age', None)
    clan = context['clans'].get(facts.get('clanId'), {})
    kingdom = context['kingdoms'].get(facts.get('kingdomId'), {})
    facts['clanName'] = clan.get('name', '')
    facts['kingdomName'] = kingdom.get('name', '')
    facts['isRuler'] = kingdom.get('rulerId') == profile['heroStringId']
    for rel in ['father','mother','spouse']:
        other = by_id.get(facts.get(rel+'Id'), {})
        facts[rel+'Name'] = other.get('sourceFacts', {}).get('name', '')
        facts[rel+'Alive'] = other.get('sourceFacts', {}).get('isAlive', True)
    facts['childrenNames'] = [p['sourceFacts']['name'] for p in profiles
        if profile['heroStringId'] in [p['sourceFacts'].get('fatherId'),p['sourceFacts'].get('motherId')]]
    siblings=[p for p in profiles if p['heroStringId']!=profile['heroStringId'] and any(
        facts.get(parent) and facts[parent]==p['sourceFacts'].get(parent) for parent in ['fatherId','motherId'])]
    facts['siblingIds']=[p['heroStringId'] for p in siblings]
    facts['olderSiblingIds']=[p['heroStringId'] for p in siblings if 'age' in facts and 'age' in p['sourceFacts'] and p['sourceFacts']['age']>facts['age']]
    facts['livingParentIds']=[p['heroStringId'] for p in profiles if p['heroStringId'] in [facts.get('fatherId'),facts.get('motherId')] and p['sourceFacts'].get('isAlive',True)]
    return facts

def facts_permit(facts, concept):
    rule=fact_rules.get(concept['id'])
    if not rule: return True
    assert rule['title']==concept['title']
    if facts.get('age',30)<rule['minAge']: return False
    if facts.get('age',30)>rule.get('maxAge',150): return False
    fields={'sibling':'siblingIds','olderSibling':'olderSiblingIds','livingParent':'livingParentIds'}
    return all(facts.get(fields[requirement]) for requirement in rule['requires'])

def personality_score(profile, facts, trait):
    personality = profile['traits']
    if trait == 'honor' and 'honor' in personality.get('courtVirtues', {}):
        return max(0, min(100, personality['courtVirtues']['honor']))
    if trait in personality.get('traitPercentages', {}):
        return max(0, min(100, personality['traitPercentages'][trait]))
    key = 'honesty' if trait == 'honor' else trait
    if key in personality.get('foundationTraits', {}):
        return 50 + 20 * max(-2, min(2, personality['foundationTraits'][key]))
    key = 'mercy' if trait == 'compassion' else trait
    if key in facts.get('traits', {}):
        return 50 + 40 * max(-1, min(1, facts['traits'][key]))
    return 50

def dream_weight(profile, facts, concept):
    if concept['category'] not in ['dream','desire','value'] or '/child_' in concept['family']:
        return 1
    rule = (dream_rules if concept['category']=='dream' else concern_rules)[concept['id']]
    assert rule['title'] == concept['title']
    score = lambda trait: personality_score(profile, facts, trait)
    if any(score(trait) <= 40 for trait in rule['requireAll']):
        return 0
    if any(score(trait) > 60 for trait in rule.get('requireNotHigh', [])):
        return 0
    if rule['requireAny'] and not any(score(trait) > 40 for trait in rule['requireAny']):
        return 0
    affinities = [100-score(trait[1:]) if trait.startswith('-') else score(trait) for trait in rule['prefer']]
    return .35 + 2.65 * sum(affinities)/len(affinities)/100 if affinities else 1

def blueprint(facts, seed, profile):
    age = facts.get('age', 30)
    result = []
    for category, (minimum, maximum) in RANGES.items():
        candidates = []
        for concept in concepts:
            if concept['category'] != category or not concept['minAge'] <= age <= concept['maxAge']:
                continue
            if not facts_permit(facts, concept):
                continue
            if category == 'hobby' and 0 < age < 18 and re.search(r'\b(wine|ale|beer|mead|brandy|spirits|drinking|tavern)\b', concept['title'], re.I):
                continue
            skill = SKILLS.get(concept['family'], '')
            skills = {k.lower():v for k,v in facts.get('skills', {}).items()}
            weight = 1 + min(.6, max(0, skills.get(skill,0))/300) if category == 'hobby' and skill else 1
            weight *= dream_weight(profile, facts, concept)
            if weight <= 0:
                continue
            score = -math.log(((draw(seed,'candidate/'+concept['id']) >> 11)+1)/9007199254740993)/weight
            candidates.append((score, concept))
        candidates.sort(key=lambda x:x[0])
        if category == 'hobby':
            seen = set()
            candidates = [(score,c) for score,c in candidates if not (c['family'] in seen or seen.add(c['family']))]
        count = minimum + draw(seed,'count/'+category) % (maximum-minimum+1)
        assert len(candidates) >= count, (profile['heroStringId'], category, len(candidates), count)
        for _, concept in candidates[:count]:
            item = dict(concept)
            roll = draw(seed,'influence/'+item['id']) % 100
            for influence, weight in enumerate(WEIGHTS,1):
                if roll < weight:
                    break
                roll -= weight
            item.update(influence=influence,
                        visibility='private' if category in ['fear','wound','jealousy','temptation','secret'] else 'personal',
                        source='task_authored_library_composition',meaningKey=item['title'])
            result.append(item)
    return result

def temperament_for(profile, seed):
    traits = profile['traits']['foundationTraits']
    candidates = sorted(temperaments, key=lambda key:(-abs(traits.get(key,0)),draw(seed,'editorial-trait/'+key)))
    chosen = []
    for key in candidates[:3]:
        pole = 'high' if traits.get(key,0) >= 0 else 'low'
        public, private = temperaments[key][pole]
        chosen.append((key, public.format(name=profile['sourceFacts']['name']),
                       private.format(name=profile['sourceFacts']['name'])))
    return chosen

def describe_items(items, facts, seed):
    name = facts['name']
    for item in items:
        title = item['title']
        if item['id'] in specific_meanings:
            aim, meaning = specific_meanings[item['id']]
            item['description'] = (f'{name} hopes to '+title+'.' if item['category']=='dream'
                                   else f'{name} wants to '+title+'.' if item['category']=='desire'
                                   else f'{name} believes that '+title+'.' if item['category']=='value'
                                   else f'{name} feels attached to '+title+'.' if item['category']=='attachment'
                                   else f'{name} is uneasy about '+title+'.' if item['category']=='fear'
                                   else f'A lingering hurt for {name} concerns '+title+'.' if item['category']=='wound'
                                   else f'An experience that stayed with {name} was '+title+'.' if item['category']=='formative'
                                   else f'{name} is privately tempted to '+title+'.' if item['category']=='temptation'
                                   else f'A private point of comparison for {name} is '+title+'.' if item['category']=='jealousy'
                                   else title[0].upper()+title[1:]+'.')
            item['personalMeaning'] = meaning
            item['meaningKey'] = aim
        elif item['category'] == 'hobby' and title in hobby_details:
            detail, reward = hobby_details[title]
            item['description'] = f'{name} enjoys {title}. A particular point of interest is {detail}.'
            item['personalMeaning'] = reward[0].upper()+reward[1:]+'.'
        else:
            raise ValueError('Missing specific authored prose: '+item['id'])
        if not facts.get('isAlive',True):
            for present,past in [(f'{name} hopes to ',f'{name} hoped to '),(f'{name} wants to ',f'{name} wanted to '),
                                 (f'{name} believes that ',f'{name} believed that '),(f'{name} feels attached to ',f'{name} felt attached to '),
                                 (f'{name} is uneasy about ',f'{name} was uneasy about '),(f'{name} enjoys ',f'{name} enjoyed '),
                                 (f'{name} is privately tempted to ',f'{name} was privately tempted to ')]:
                item['description']=item['description'].replace(present,past)
            item['description']=item['description'].replace('A particular point of interest is ','A particular point of interest was ')

def list_names(names):
    if len(names) <= 1:
        return ''.join(names)
    return ', '.join(names[:-1])+' and '+names[-1]

def draft_life(facts, traits, items, seed):
    name = facts['name']
    age = facts.get('age')
    clan = facts.get('clanName')
    culture = {'empire':'imperial','sturgia':'Sturgian','aserai':'Aserai','khuzait':'Khuzait','vlandia':'Vlandian','battania':'Battanian'}.get(facts.get('cultureId'),'Calradian')
    article = 'an' if culture[0].lower() in 'aeiou' else 'a'
    opening = f'{name} is '+(f'{int(age)} years old, ' if age is not None else '')+f'{article} {culture} noble'+(f' of {clan}.' if clan else '.')
    canon = facts.get('nativeEncyclopediaText','').strip()
    origin = opening + (' '+canon if canon else '')
    family = []
    parents = [facts.get(rel+'Name') for rel in ['father','mother'] if facts.get(rel+'Name')]
    if parents:
        family.append(f'{name} is the child of {list_names(parents)}.')
    if facts.get('spouseName'):
        family.append(f'{name} '+('is married to ' if facts['spouseAlive'] else 'was married to the late ')+facts['spouseName']+'.')
    if facts['childrenNames']:
        family.append(f'{name} is '+('the parent of ')+list_names(facts['childrenNames'])+'.')
    if facts.get('homeSettlementName'):
        family.append(f'{facts["homeSettlementName"]} is the household\'s home.')
    formative=sorted([item for item in items if item['category']=='formative'],key=lambda item:(-item['influence'],draw(seed,'life-experience/'+item['id'])))
    upbringing=formative[0]['description']+' '+formative[0]['personalMeaning']
    development=formative[1]['description']+' '+formative[1]['personalMeaning']
    inner=max([item for item in items if item['visibility']=='private'],key=lambda item:(item['influence'],draw(seed,'life-private/'+item['id'])))
    private=inner['description']+' '+inner['personalMeaning']
    dream=max([item for item in items if item['category']=='dream'],key=lambda item:(item['influence'],draw(seed,'life-dream/'+item['id'])))
    value=max([item for item in items if item['category']=='value'],key=lambda item:(item['influence'],draw(seed,'life-value/'+item['id'])))
    aspiration=dream['description']+' '+dream['personalMeaning']
    conviction=value['description']+' '+value['personalMeaning']
    hobbies = [x for x in items if x['category']=='hobby']
    leisure = hobbies[0]['description']+' '+hobbies[0]['personalMeaning']
    reputation = traits[1][1]
    summary = '\n\n'.join(x for x in [origin,' '.join(family),upbringing,development,traits[0][1],aspiration,conviction,leisure,private] if x)
    return dict(summary=summary,publicSummary=opening+' '+reputation,origin=origin,
                upbringing=upbringing,reputation=reputation,privateBackstory=private)

def authored_life(facts, traits, items, seed, portrait):
    life = draft_life(facts, traits, items, seed)
    if not facts.get('isAlive', True):
        life['origin'] = facts.get('nativeEncyclopediaText', '').strip()
    life['upbringing'] = portrait.get('upbringing', life['upbringing'])
    life['privateBackstory'] = portrait['private']
    life['reputation'] = portrait.get('public', traits[1][1])
    life['publicSummary'] = life['origin']+' '+life['reputation']
    life['summary'] = '\n\n'.join([life['origin'], life['upbringing'], portrait['portrait'], portrait['private']])
    return life

def draft_voice(profile, facts, seed, portrait):
    native = profile['voice']['nativeVoice']
    if portrait:
        return dict(nativeVoice=native, speechStyle=portrait['speech'], socialMask=portrait['mask'], tells=portrait['tells'])
    traits = profile['traits']['foundationTraits']
    mask = 'guarded' if traits.get('trust',0)<0 else 'firm' if traits.get('assertiveness',0)>0 else 'open' if traits.get('sociability',0)>0 else 'reserved'
    mbti = profile['traits']['mbtiProfile']['type']
    tells = sorted(voice_material['tells'],key=lambda text:draw(seed,'tell/'+text))[:2]
    return dict(nativeVoice=native,
                speechStyle=choose(voice_material['native'][native],seed,'voice-style')+' '+voice_material['mbti'][mbti],
                socialMask=choose(voice_material['masks'][mask],seed,'social-mask'), tells=tells)

def defining_ids(items, seed):
    # Match the production two-stage semantic grouping and deterministic ties.
    by_meaning=collections.defaultdict(list)
    for item in items:
        meaning=re.sub(r'[^a-z0-9]+', ' ', item['meaningKey'].lower()).strip()
        by_meaning[meaning].append(item)
    representatives=[min(group,key=lambda item:(-item['influence'],draw(seed,'meaning-tie/'+item['id']))) for group in by_meaning.values()]
    selected=sorted(representatives,key=lambda item:(-item['influence'],draw(seed,'defining-tie/'+item['id'])))[:3]
    assert len(selected)==3
    return [item['id'] for item in selected]

def main():
    draft_root = OUT/'drafts'
    draft_root.mkdir(parents=True,exist_ok=True)
    inventory = []
    for index, profile in enumerate(profiles):
        hero = profile['heroStringId']
        facts = facts_for(profile)
        seed = 'premade/'+hero
        items = blueprint(facts,seed,profile)
        describe_items(items,facts,seed)
        temper = temperament_for(profile,seed)
        needs_individual = facts.get('age',30)<12 or not facts.get('isAlive',True)
        portrait = portraits.get(hero)
        assert not needs_individual or portrait, hero
        life = authored_life(facts,temper,items,seed,portrait) if portrait else draft_life(facts,temper,items,seed)
        document = dict(schema=SCHEMA,heroStringId=hero,seed=seed,revision=1,status='editorial_draft',
                        items=items,life=life,voice=draft_voice(profile,facts,seed,portrait),definingIds=defining_ids(items,seed),
                        influenceLevels=influence_levels,realismPolicy=realism_policy,
                        provenance=dict(authoringMethod='task_authored_local_composition_v1',providerCalls=0,
                                        sourceFactsHash=digest(json.dumps(profile['sourceFacts'],sort_keys=True)),
                                        editorialStatus='unreviewed',individualLifeRequired=needs_individual,
                                        remainingGenericItems=[item['id'] for item in items if item['category']!='hobby' and item['id'] not in specific_meanings]))
        path = draft_root/(digest(hero)[:24].upper()+'.json')
        path.write_text(json.dumps(document,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        inventory.append(dict(index=index,heroStringId=hero,name=facts['name'],age=facts.get('age'),
                              isRuler=facts['isRuler'],individualLifeRequired=needs_individual,
                              path=str(path.relative_to(OUT)),items=len(items)))
    report = dict(schema='reign-local-editorial-inventory-v1',draftCount=len(inventory),readyCount=0,
                  providerCalls=0,sourceCatalogHash=digest((LIB/'profile_catalog_v4.json').read_text(encoding='utf-8-sig')),
                  notes=['Unreviewed compositional drafts, not publishable profiles.',
                         'Child, deceased and ruler portraits plus voices are incorporated; all remain review drafts.',
                         'Semantic trio and substantive prose review are still required.'],profiles=inventory)
    (OUT/'draft-inventory.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({k:report[k] for k in ['draftCount','readyCount','providerCalls','notes']}))

if __name__ == '__main__':
    main()
