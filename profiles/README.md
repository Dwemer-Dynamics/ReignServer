# Reign character profile library

The shipped v5 `profile_catalog.json` and preserved v4 foundation catalog contain
1,195 profiles: 397 Sandbox nobles and 798 Reign court nobles. The court roster
has 133 independent six-person households, including 13 Nord households with 78
adults. Every new Nord clan starts at tier 1; its members keep the ordinary
renown, equipment, relationships and future rank rules.

Do not hand-edit the generated catalogs or replace the existing v5 roster with
the legacy foundation generator. Existing profile objects and narrative files
are authored canon. The initial v5 roster used local task authorship and the
approved concept/meaning library; it was not built through construction provider
calls.

## Nord extension authoring

The new family identities, home IDs and individual life seeds are authored in
the paired client checkout's `ReignBeta/ModuleData/reign_nord_households.json` and
`ReignBeta/tools/CourtNobles/nord-households-authored.json`. Native DLC XML owns
their culture, body-property ranges, skill and equipment template identities.
The Nord extension targets a completely new campaign; it does not backfill
older saves.

Build through the paired workspace's canonical validation engine. Its successful
artifact can generate foundations to a separate authoring path using
`--generate-profile-library`, `--sandbox-data`, `--reign-data`, `--court-manifest`
and `--output`. Resolve Nord skill sets alongside Sandbox skill sets. Keep the
authoring runtime and database isolated; never start a listener or use live
campaign storage for this operation. Foundation generation does not establish
finished narratives, native appearance, dialogue quality or save acceptance.

The reproducible local composer is
`ReignBeta/tools/CourtNobles/author_nord_profiles.py` in the sibling private
checkout. Run it with `--foundations <foundation-json>`, `--baseline <exact-prior-catalog-folder>`
and `--evidence <task-owned-evidence-folder>`. By default it writes review output
and a preservation/compatibility audit only. `--install` appends the reviewed 78
identities and their 78 new narrative files into this source checkout. It refuses
changes to any prior profile object or narrative file and makes no provider call.

Concept IDs, age/family constraints, personality compatibility, influence and
privacy follow `CharacterNarrative.cs`, `CharacterNarrativePersonality.cs` and the
versioned libraries. Each Nord life and voice uses its actual family and authored
individual history. Shared concept meanings remain reusable library material;
they are not 78 independently handwritten sets of every concern.

After authoring, obtain the manifest-selected validation plan for all exact
changed paths and run canonical validation. Run the successful artifact's
`character_narrative` Verification Lab suite through
`reign_run_offline_verification` when the plan has not covered it. The suite owns
the complete-roster identity, family, trait/MBTI preservation and narrative
compatibility checks. Provider dialogue behavior, native fresh-campaign presence
and save/reload acceptance are separate evidence. Deployment requires the user's
explicit command and must include the accepted shared portrait source/inventory.

The two complete 1,195-person catalogs use compact JSON. Their decoded profile
objects remain unchanged by formatting. Each exceeds the ordinary 25 MiB file
limit, so `reign.repository.json` registers exactly these two shipped catalogs
as large authored assets; the global limit is unchanged. Preserve full redacted
secret-classifier scans when updating these files, because the generic hygiene
scanner bounds ordinary text inspection by that same size limit.
