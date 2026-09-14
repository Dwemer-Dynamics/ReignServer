# Character Construction Policy

## Required Runtime Behavior

- The server ships the fixed character catalog in `ProfileLibrary/profile_catalog.json`.
- Campaign launch must not upload or rewrite every fixed noble profile.
- Opening chat history must not create or update a character profile.
- Passive wilderness-event planning must not create or construct a dynamic character profile. It may read an existing profile, read the shipped catalog, or use compact live baseline facts only.
- A dialogue, party-chat, or event request may carry the one live speaker profile needed for that interaction.
- Shipped characters materialize their canonical campaign files without an LLM call.
- Uncatalogued dynamic characters receive a deterministic baseline and the status `first_contact_pending`.
- Before that dynamic character produces their first interactive response, Reign synchronously:
  1. updates the one live character profile from the game request;
  2. constructs the complete campaign character with at most two LLM attempts;
  3. writes the public background, private history, traits, voice, motives, secrets, and state;
  4. generates the requested response only after construction succeeds.
- If both construction attempts fail, the response pipeline stops and returns a diagnostic. It must not answer using an incomplete character.
- Once construction succeeds, later interactions reuse the saved campaign profile.

## Prohibited Behavior

- No character-construction worker may run at server startup.
- No pending character queue may be consumed while the game is idle or closed.
- No server process may scan all campaigns and issue character LLM calls.
- The retired `background_enrichment` source is blocked before provider access.
- The general `constructCharactersWithLlm` setting authorizes explicit and first-contact construction only. It does not authorize background work.

## Legacy Queue Migration

At server startup, old `pending`, `running`, and `failed` enrichment records are changed to `deferred_until_interaction`. Completed profiles and shipped canonical profiles are preserved. No model call is made during migration.

## Diagnostics And Regression Gates

`GET /api/diagnostics` exposes `characterConstructionPolicy` with:

- `policy: first_interaction_only`
- `backgroundWorkerStarted: false`
- `automaticProfileUploadsEnabled: false`
- `pendingBackgroundCalls: 0`
- campaign queue-state counts

The character-profile test suite verifies canonical no-LLM materialization, passive-event non-materialization, dynamic first-contact deferral, and the hard background-source block. Verification contracts also fail if the removed launch uploader or background worker symbols return.

## Separate Live-State Synchronization

Identity-network synchronization remains a separate compact roster operation because kingdom, clan, family, ruler, and living-status membership can change during a campaign. It does not construct character profiles and does not call an LLM.
