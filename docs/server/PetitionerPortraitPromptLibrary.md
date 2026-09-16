# Petitioner Commoner Portrait Prompt Library

## Purpose

This library defines the text-to-image prompt material for future court petitioners. Petitioner portraits have no captured Bannerlord source image. They are generated as new identities from structured character facts while preserving the current portrait composition, photographic style, and output exclusions.

The culture guidance uses Bannerlord's real-world historical analogues as the primary reference for both clothing and racial appearance. Each generated identity should read immediately as a plausible person from that analogue population while remaining an individual rather than a repeated stock face.

## Real-world analogue map

- Vlandian: high-medieval Norman and Frankish western European.
- Battanian: medieval Celtic Brittonic, Irish, and highland populations.
- Sturgian: medieval Kievan Rus and East Slavic populations with a secondary Norse influence.
- Imperial: medieval Byzantine Greek, Anatolian, and Balkan populations.
- Aserai: medieval Arabian, Levantine, and North African Arab or Amazigh populations.
- Khuzait: medieval Mongolic and Turkic Central Asian steppe populations.
- Nord: medieval Norwegian and closely related Norse Scandinavian populations.

These analogues define the central visual tendency, not merely an optional possibility. Use the most representative coloring and facial traits frequently enough that a set of portraits has a clear cultural identity. Less-common traits may appear at a lower frequency so the population remains believable. Do not introduce distant ancestry merely to maximize diversity; use border or mixed ancestry only when character facts call for it.

## Assembly order

Use the current runtime prompt layers in this order:

1. The text-to-image petitioner identity layer below.
2. `portrait_composition.txt` — unchanged.
3. The commoner petitioner core below.
4. Exactly one culture module below.
5. One livelihood module and one court-disposition module when those facts are known.
6. `portrait_prompt_style.txt` — unchanged.
7. `portrait_output_rules.txt` — unchanged.

The current Reign composition therefore remains a centered, upright, full head-to-toe portrait in front of a softly blurred historical stone doorway. The current photographic treatment remains natural daylight, shallow depth of field, realistic 85mm perspective, authentic skin, subtle film grain, and grounded photography. The current exclusions remain authoritative, including no text, cards, footers, armor, weapons, helmets, CGI, illustration, or modern clothing.

## Production canvas and safe-body contract

Petitioner masters must use the same production shape as Reign's current full-body portrait pipeline:

```text
PRODUCTION CANVAS AND SAFE BODY PLACEMENT
Create an exact 2:3 vertical portrait canvas, intended for a 1024 x 1536 pixel full-body portrait master. Do not use a narrower fashion-poster canvas or a wider landscape canvas.

The single standing figure must occupy approximately 88 to 92 percent of the canvas height. Place the top of the hair approximately 4 to 6 percent below the top edge and the soles of the feet approximately 4 to 6 percent above the bottom edge. Center the face and torso on the vertical axis. Keep the complete body, clothing silhouette, elbows, hands, legs, and footwear inside the central 70 percent of the canvas width, with at least 12 percent clear background on both the left and right sides.

Keep the head large enough for Reign's automatic face-focused chest and thumbnail derivatives: the face should be clearly detectable, unobstructed, looking generally toward the camera, and approximately 7 to 9 percent of the total canvas height from chin to hairline. Keep hair away from the eyes and do not turn the face into profile. Use a natural eye-level camera with no wide-angle distortion, high-angle view, low-angle view, foreshortening, or exaggerated perspective.

Show a narrow strip of ground beneath the footwear, but do not waste canvas height on foreground or overhead architecture. The softly blurred stone doorway should fill the background edge to edge. No frame, matte, letterboxing, pillarboxing, transparent margin, blank strip, footer, or added border.
```

## Text-to-image petitioner identity

```text
IDENTITY AND SUBJECT — TEXT-TO-IMAGE PETITIONER
Create a portrait of [CHARACTER NAME], a [GENDER] commoner of [CULTURE] culture who is [AGE] years old. This is a new fictional human identity generated entirely from the supplied character facts; there is no source face or source image.

Create one believable and visually distinctive individual consistent with the supplied age, gender, culture range, livelihood, economic condition, health, settlement, and life history. Give the person a coherent combination of facial structure, skin tone, eyes, hair, grooming, body build, posture, and age detail rather than averaging every possible cultural trait. Use realistic facial asymmetry, authentic skin texture, visible pores, natural eyes, individually defined hair, plausible teeth and lips, age-appropriate lines, and ordinary human imperfections. Do not make the person conventionally beautiful by default, grotesque, comical, heroic, villainous, or celebrity-like.

The portrait establishes this petitioner's canonical visual identity. Make the face distinctive enough to recognize again in later court appearances. Once an image is accepted, reuse that accepted portrait as the identity reference for any later variants; do not generate a different face for the same petitioner.

Treat the name, age, gender, culture, livelihood, economic condition, settlement, disposition, and petition pressure as private generation guidance. They must never be printed, captioned, labeled, written, or otherwise rendered anywhere in the image.
```

## Commoner petitioner core

```text
COMMONER COURT PETITIONER
Portray this person as an ordinary civilian petitioner appearing before a ruler's court, not as a noble, notable, courtier, soldier, adventurer, bandit, or fantasy hero. Their clothing must reflect [LIVELIHOOD], [ECONOMIC CONDITION], [SETTLEMENT TYPE], and the practical needs of their culture and climate. Use ordinary locally available wool, linen, cotton, felt, leather, or woven cloth as culturally appropriate. Show believable wear through softened fabric, minor fading, careful repairs, patched seams, work-polished surfaces, or dust at the hem, but keep the person recognizably dressed for the dignity of a formal petition. Poverty must not automatically mean filth, rags, deformity, or humiliation.

The person has made a deliberate effort to appear before the court. Their garments may be plain, old, borrowed, mended, or work-worn, but they are arranged as neatly as their circumstances allow. Use little or no jewelry and no noble insignia, heraldry, expensive court fashion, ceremonial dress, military equipment, or official badge of office. No headgear of any kind, so the face, hair, and silhouette remain clearly readable.

Keep both hands naturally visible and empty. Use a restrained standing posture and facial expression shaped by [PETITION DISPOSITION] and [PETITION PRESSURE]. Convey an individual with personal dignity, agency, and a specific reason for coming to court. Do not use ethnic caricature, theatrical misery, exaggerated menace, stock peasant comedy, or a generic fantasy-villager costume.
```

## Individual variation rule

Append this rule to every culture module:

```text
INDIVIDUAL VARIATION
Treat the culture description as a population distribution rather than a checklist. Select a coherent combination for this individual; do not average every listed feature together. Favor the culture's stated common traits and use its less-common traits less frequently. Do not exaggerate features into caricature or make every person identical. Physical condition should follow age, livelihood, health, diet, weather exposure, and life history. Use mixed or border ancestry only when supplied character facts support it.
```

## Vlandian commoner

```text
VLANDIAN COMMONER CULTURE MODULE
Dress this petitioner in practical high-medieval Vlandian clothing inspired by Norman and Frankish civilian working dress. For a man, use a plain knee-length wool tunic over a linen shirt, fitted sleeves, a simple leather or woven belt, narrow trousers or chausses, and worn ankle boots or shoes. For a woman, use a long linen underdress with a practical wool kirtle or simply fitted overdress, full sleeves, a modest neckline, a woven belt, and optionally a plain work apron. A short mantle or shoulder wrap may be used for cold or wet weather. Favor sturdy construction, restrained woven edging, repaired hems, and naturally muted or faded dyes. Avoid royal heraldry, crusader styling, theatrical feudal costume, and luxurious noble tailoring.

Base Vlandian racial appearance on medieval Norman and Frankish western Europeans. Use fair to light skin most often, sometimes ruddy, freckled, sun-weathered, or light olive. Brown, chestnut, dark-blond, and light-brown hair should be common; blond, auburn, red-brown, and dark-brown should also occur, while black hair is less common. Use blue, gray, hazel, green, and brown eyes in a balanced western European range. Favor believable western European facial variation: oval, long, square, or round faces; straight, prominent, narrow, or moderately broad noses; and varied jaws and cheekbones. Do not make every Vlandian blond, blue-eyed, aristocratic, or strongly Anglo-Saxon in appearance.

Apply the INDIVIDUAL VARIATION rule.
```

## Battanian commoner

```text
BATTANIAN COMMONER CULTURE MODULE
Dress this petitioner in rugged Battanian civilian clothing inspired by early medieval Celtic, Brittonic, and highland working dress. For a man, use a long-sleeved linen or wool tunic, close or moderately loose trousers, a broad practical belt, sturdy shoes or boots, and an optional heavy woven mantle fastened simply at the shoulder. For a woman, use a long linen dress beneath a sleeveless or short-sleeved wool overdress, a woven belt, practical apron, shawl, or shoulder mantle. Use coarse woven wool, visible hand-spun texture, subtle checks, stripes, or geometric borders, and careful mending. Avoid modern tartan, kilts, fantasy druid costume, antlers, war paint, and excessive knotwork.

Base Battanian racial appearance on medieval Celtic Brittonic, Irish, and highland populations. Use fair or light skin most often, frequently with ruddy weathering or freckles; lightly tanned skin is plausible for outdoor workers. Brown and dark-brown hair should be most common, with auburn, red, red-brown, dark blond, and black also represented. Red or auburn hair and heavy freckling should be a visible minority rather than the default. Blue, gray, green, hazel, and brown eyes are all plausible, with lighter eyes appearing often. Use varied northwestern European faces, including narrow, oval, broad, and square shapes, with straight, rounded, prominent, or broad noses and naturally varied jaws. Do not turn Battanians into uniformly red-haired, wild-eyed, heavily built caricatures.

Apply the INDIVIDUAL VARIATION rule.
```

## Sturgian commoner

```text
STURGIAN COMMONER CULTURE MODULE
Dress this petitioner in practical medieval Sturgian clothing inspired by Rus and northern Slavic civilian dress. For a man, use a long linen shirt under a belted wool tunic or simple wrap-front coat, loose trousers, wrapped lower legs when visible, and durable boots. For a woman, use a long linen shift beneath a wool overdress, apron-dress, or simple coat-dress, secured with a woven sash and optionally covered by a practical shoulder wrap. Use layered wool and linen, modest geometric embroidery at the collar, cuff, or hem, visible repairs, and only sparse functional fur trim when climate calls for it. Avoid lavish fur mantles, Viking-warrior styling, chainmail, ceremonial caftans, and fantasy northern costume.

Base Sturgian racial appearance primarily on medieval Kievan Rus and East Slavic populations, with a secondary Norse presence. Use fair to light skin most often, commonly cool-toned or ruddy from cold and wind, with light tanning on outdoor workers. Light-brown, ash-brown, dark-blond, brown, and dark-brown hair should be common; flaxen blond, chestnut, red-brown, and black should occur less often. Gray, blue, green, hazel, and brown eyes are all plausible, with gray, blue, and hazel strongly represented. Use East Slavic facial variation with oval, broad, or long faces, moderate to high cheekbones, and straight, rounded, narrow, or moderately broad noses; Norse-influenced individuals may lean toward Scandinavian coloring without becoming the population default. Do not make every Sturgian a blond Viking, huge warrior, or fur-covered northerner.

Apply the INDIVIDUAL VARIATION rule.
```

## Imperial commoner

```text
IMPERIAL COMMONER CULTURE MODULE
Dress this petitioner in practical Calradic Imperial clothing inspired by late Roman, Byzantine, Balkan, and Anatolian civilian dress rather than classical costume. For a man, use a linen undertunic beneath a plain knee-length or calf-length wool tunic, a narrow belt, fitted or loose lower garments, and sturdy shoes or boots. For a woman, use a long tunic-dress or underdress with a simple sleeved overdress, controlled draping, a woven belt, and an optional rectangular shawl or work apron. Use orderly seams, narrow geometric woven borders, practical layered cloth, faded local dyes, and repairs appropriate to an urban laborer or provincial household. Avoid togas, laurel wreaths, legionary imagery, senatorial robes, pristine marble-statue styling, and ornate court dress.

Base Imperial racial appearance on medieval Byzantine Greek, Anatolian, and Balkan populations. Use light olive, olive, and medium Mediterranean complexions most often, with fair or tan individuals also common and darker brown complexions appearing occasionally through the empire's southern and eastern populations. Dark-brown and black hair should predominate, commonly straight, wavy, or curly; chestnut and medium brown should occur regularly, while red-brown or blond is less common. Brown and hazel eyes should predominate, with green, gray, or blue appearing at lower frequency. Favor Mediterranean, Greek, Anatolian, and Balkan facial ranges: oval or long faces, dark brows, varied prominent or straight noses, and moderate cheekbones, without making all subjects look identical. Do not use classical Roman statuary, modern Italian stereotypes, or a uniformly pale northern-European appearance.

Apply the INDIVIDUAL VARIATION rule.
```

## Aserai commoner

```text
ASERAI COMMONER CULTURE MODULE
Dress this petitioner in practical Aserai civilian clothing inspired by medieval Arabian, Levantine, and North African working dress suited to heat, dust, and strong sun. For a man, use a breathable long linen or cotton tunic, loose trousers where appropriate, a simple woven sash or leather belt, an optional light open-front robe or shoulder cloth, and worn soft boots, shoes, or sandals. For a woman, use a long breathable underdress or tunic-dress with a practical loose outer layer, gathered sleeves, a woven sash, and an optional light shawl worn around the shoulders rather than over the head. Use sun-faded cloth, restrained stripes or geometric edging, reinforced hems, and careful repairs. Avoid orientalist luxury costume, transparent harem styling, excessive gold, theatrical veils, and princely robes.

Base Aserai racial appearance on medieval Arabian, Levantine, and North African Arab or Amazigh populations. Use olive, warm tan, medium-brown, and brown complexions most often, with light-olive and deeper-brown individuals also present. Black and dark-brown hair should strongly predominate, commonly straight, wavy, or curly; medium brown, chestnut, or reddish-brown should be uncommon variations. Dark-brown and brown eyes should predominate, with hazel and occasional green or gray appearing less often. Favor plausible Arabian, Levantine, and North African facial ranges with dark brows, oval, long, or broad faces, varied straight, aquiline, rounded, or broad noses, and varied lips and cheekbones. Do not exoticize, uniformly over-darken, or substitute sub-Saharan, South Asian, or orientalist fantasy features unless specific ancestry calls for them.

Apply the INDIVIDUAL VARIATION rule.
```

## Khuzait commoner

```text
KHUZAIT COMMONER CULTURE MODULE
Dress this petitioner in practical Khuzait civilian clothing inspired by medieval Central Asian, Turkic, and Mongolic steppe dress. For a man, use a plain crossed-front deel, caftan, or belted steppe tunic over light underlayers, loose trousers, a broad woven sash, and durable soft riding boots. For a woman, use a long crossed-front robe, coat-dress, or layered tunic over a practical underdress or loose lower garments, secured with a woven sash and sturdy boots. Use wool, felt, linen, leather edging, restrained geometric trim, sun fading, wind wear, and repairs; add minimal functional fur only when seasonally appropriate. Avoid mounted-warrior costume, bows, quivers, lamellar armor, ceremonial eagle imagery, and extravagant khanate dress.

Base Khuzait racial appearance on medieval Mongolic and Turkic Central Asian steppe populations. Use light-tan, golden-tan, warm beige, olive, and medium-brown complexions most often. Black and very dark-brown hair should strongly predominate, usually straight or slightly wavy; medium-brown hair should be uncommon. Dark-brown and brown eyes should overwhelmingly predominate, with hazel or gray uncommon. Favor authentic Central and East Asian steppe facial ranges: epicanthic folds and almond-shaped eyes are common but variable; cheekbones may be moderate or high; faces may be broad, oval, or long; and noses may be low-bridged, straight, rounded, or moderately prominent according to Mongolic or Turkic ancestry. Do not exaggerate eyelids, flatten every face, yellow the skin, or make every Khuzait share the same broad-faced phenotype.

Apply the INDIVIDUAL VARIATION rule.
```

## Nord commoner

```text
NORD COMMONER CULTURE MODULE
Dress this petitioner in practical Nord civilian clothing inspired by early medieval Scandinavian and North Sea working dress. For a man, use a linen shirt under a sturdy wool tunic, a simple belt, practical trousers, wrapped lower legs when appropriate, and worn leather shoes or boots. For a woman, use a long linen shift beneath a plain wool overdress or apron-dress, a woven belt, practical shawl or short mantle, and durable footwear. Use dense wool, linen, simple brooches, restrained tablet-woven edging, weathered leather, and careful mending. Avoid horned helmets, raider costume, excessive fur, war braids as a uniform, runic decoration everywhere, and theatrical Viking styling.

Base Nord racial appearance specifically on medieval Norwegian and closely related Norse Scandinavian populations. Use fair or light skin most often, frequently cool-toned, ruddy, wind-reddened, or lightly freckled; outdoor workers may be lightly tanned. Blond, dark-blond, flaxen, and light-brown hair should be common, with medium brown, auburn, red, and dark-brown also represented; black hair should be uncommon. Blue and gray eyes should be common, with green, hazel, and brown also plausible. Favor Scandinavian facial variation with long, oval, square, or broad faces; straight, narrow, prominent, or rounded noses; and varied jaws and cheekbones. Body build varies normally, though height and robust frames may appear somewhat more often. Do not make every Nord enormous, platinum blond, blue-eyed, braided, bearded, or styled as a raider.

Apply the INDIVIDUAL VARIATION rule.
```

## Livelihood modules

Choose one short module. These modify clothing condition and physique without replacing the culture module.

```text
FARMER OR VINE-GROWER: Sun and repetitive outdoor labor may show in the hands, posture, and complexion. Use serviceable clothing with soil-softened hems, field repairs, and no harvest props.

HERDER OR PASTORALIST: Use weather-ready layers, durable footwear, wind exposure, and clothing polished by repeated travel and animal work. Do not add animals, staffs, ropes, or riding equipment.

FISHER OR RIVER WORKER: Use damp-weather practicality, salt- or water-faded cloth, strong forearms or work-worn hands where age permits, and carefully repaired outer layers. Do not add nets, hooks, fish, boats, or nautical costume.

MINER, QUARRY WORKER, OR LABORER: Use abrasion, dust held in seams, reinforced or repaired garments, sturdy footwear, and a body shaped plausibly by work. Do not exaggerate dirt, muscles, injury, or misery.

WORKSHOP CRAFT WORKER: Use close sleeves, a practical apron or reinforced front layer, small signs of wear specific to repeated handwork, and ordinary pride in neat construction. Do not add tools or guild regalia.

MARKET SELLER, PEDDLER, OR SMALL TRADER: Use layered practical clothing with a belt or sash and subtly better maintenance than the poorest laborer, but no rich merchant costume, purse display, goods, or trade props.

DOMESTIC WORKER, WASHER, COOK, OR CAREGIVER: Use washable layers, rolled or practical sleeves only when culturally appropriate, an apron where appropriate, and clean but frequently mended cloth. Do not reduce the subject to servant caricature.

DISPLACED, BEREAVED, OR DESTITUTE PETITIONER: Use mismatched, borrowed, old, or repeatedly repaired clothing arranged with dignity. Show fatigue or strain through restrained human detail, not rags, grime, emaciation, or theatrical suffering unless explicitly established by game facts.
```

## Court-disposition modules

Choose one, then replace `[PETITION PRESSURE]` with a concrete cause such as threatened eviction, a missing family member, disputed taxes, raiding losses, debt, hunger, unlawful imprisonment, inheritance, or local corruption.

```text
ANXIOUS BUT HOPEFUL: Slight tension in the shoulders and hands, attentive eyes, controlled uncertainty, and an effort to remain respectful.

DEFERENTIAL BUT NOT SERVILE: Careful posture, lowered intensity rather than a bowed body, restrained expression, and visible self-respect.

RESOLUTE: Balanced stance, direct attentive gaze, contained emotion, and the composure of someone who has rehearsed difficult words.

ANGRY BUT CONTROLLED: Tightened jaw or brow, contained physical tension, and deliberate restraint appropriate to a dangerous court setting; no snarling or threatening pose.

GRIEVING: Fatigue around the eyes, subdued posture, and controlled sorrow; no melodramatic tears unless the immediate scene calls for them.

FRIGHTENED: Guarded shoulders, alert eyes, and restrained fear while remaining upright and readable; no cowering, screaming, or horror imagery.

SHREWD OR OPPORTUNISTIC: Watchful eyes, measured composure, and careful self-presentation without a villainous smirk, furtive crouch, or ethnicized deceit stereotype.
```

## Direct-use prompt recipe

For generation, assemble the text-to-image petitioner identity layer, current `portrait_composition.txt`, the production canvas and safe-body contract, commoner petitioner core, selected culture module, selected livelihood and disposition modules, current `portrait_prompt_style.txt`, and current `portrait_output_rules.txt` in that exact order. Fill every bracketed value with game facts before submission. Do not send bracket placeholders to the image model. The provider request must explicitly request `1024x1536` (or its provider-specific exact equivalent) rather than relying on prompt wording alone.

## Implementation note

`BuildPortraitPrompt` currently assumes an image-to-image hero portrait and assembles fixed layers without appending the request's free-form client prompt when those layers are present. Future petitioner integration should use a distinct text-to-image request path and a registered `portrait_petitioner_identity.txt` layer instead of `portrait_identity.txt`. Suggested metadata fields are `characterClass=commoner_petitioner`, `livelihood`, `economicCondition`, `settlementType`, `health`, `lifeHistory`, `petitionDisposition`, and `petitionPressure`. The image provider call must omit source-image input for the initial portrait. Store the accepted portrait as the petitioner's canonical identity image for subsequent variants.
