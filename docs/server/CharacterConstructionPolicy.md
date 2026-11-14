# Character Construction Policy

## Runtime behavior

Shipped characters materialize their canonical campaign files without an LLM call. Campaign launch does not upload or regenerate every fixed noble. Notable startup preparation assigns the deterministic MBTI/native-trait foundation; it does not author the full personal narrative.

On **player town entry**, Reign starts full profile creation for every living notable belonging to that town. Loading a save already in town also submits once campaign preparation and Save Sync alignment finish. NPC-only settlement entry, villages and castles do not trigger this feature. Native profiles are captured on the application thread; transport and construction run outside it. There is no progress popup or native-time advance.

The client obtains a server generation scope, then submits batches of at most 128 profiles to `POST /notables/profiles/prewarm`. The endpoint only admits work and returns counts; it never waits for construction. A single bounded pool admits up to 256 pending profiles across visits, coalescing `(replacement generation, campaign, hero)` identities. It fills the configured `providerMaxConcurrency` allowance (default 4, supported range 1–16), while all actual requests retain their provider-specific limits, interactive priority, circuit and retry behavior. The implementation does not raise configured limits or switch models/providers/billing. `constructCharactersWithLlm=false` disables speculative admission.

Completed and editor-owned profiles are reused. The full first-contact construction path remains authoritative: a foreground request can create a queued profile immediately, or wait on the same character gate for an already running creation. Its remaining model requests receive foreground priority. A successful prewarm makes the next conversation reuse the complete stored profile. No partial profile may speak. If speculative generation fails, its failure must not poison the original first-contact status; input-fingerprinted authoring checkpoints remain available for the normal route to resume. An unsuccessful normal foreground construction retains its existing diagnostic/retry policy.

An unsuccessful speculative attempt suppresses that hero for ten minutes for the same settings fingerprint and campaign generation. It does not suppress foreground creation. Capacity overflow returns `full` counts rather than creating more workers or retrying provider calls. Deferred profiles retain the normal first-contact fallback and can be admitted on a later visit. Transport failures retry after 30 seconds while the loaded campaign remains current. Admitted work can finish after leaving town; completed profiles remain useful. Pending jobs are in memory only and are never restored/scanned at server startup.

Workers use campaign read leases and the common character construction gate. Provider and character-lock waits release their read lease through the existing campaign provider-wait contract. Restore/import/deletion invalidates queued and in-flight jobs before publication, including staged checkpoints. The scope handshake and client campaign-instance guard prevent a previous save's submission from being admitted as new work after same-campaign restoration. Snapshot creation does not invalidate ordinary current work; a restored incomplete profile retains normal first-contact recovery.

Pre-generation records `completed_on_town_entry`, never `completed_on_first_contact`, and grants no encounter, identity knowledge, witness access, relationship, portrait or gameplay action. It retains the existing notable MBTI/native-trait foundation policy. It cannot replace a newer native observation or Character Editor authority. First-contact generation for uncatalogued characters outside these town candidates remains unchanged.

## Prohibited background behavior

- No all-campaign construction scan or periodic population generator.
- No startup consumption of historical enrichment queues.
- No construction when opening chat history or during passive wilderness-event planning.
- No revival of the retired `background_enrichment` source; its provider guard remains.
- No automatic upload or rewrite of the fixed noble catalog.

The November 12, 2026 town-entry request authorizes this bounded exception to the former first-interaction-only rule. Existing `pending`, `running` and `failed` legacy enrichment records still migrate to `deferred_until_interaction` without a model call.

## Diagnostics and proof

Operational events `notable_prewarm.queued`, `.completed` and `.failed` report bounded admission/failure evidence. `characterConstructionPolicy` reports the town-entry/fallback policy and pending work. Ready profiles and Character Editor corrections must not be rerolled.

`RunTownNotableProfileSelfTests`, explicitly invoked by the focused `character_narrative` quick suite and the offline character-profile pipeline, uses isolated run-owned files/database and fake construction work. It exercises actual worker fanout at 1/4/16, capacity, duplicate/revisit admission, per-job callback handoff, priority promotion, mixed-case foreground locking, failure restoration/cooldown, completed/editor/deceased profile reuse, and campaign-generation/lease cleanup. Record these focused results separately from generic quick totals. Existing campaign-provider-wait contracts establish the restore barrier; the narrative suite also checks complete narrative publication. See the paired workspace's `docs/agent/testing/DIALOGUE_AND_PROVIDERS.md#town-notable-profile-pre-generation`.

Canonical validation and Linux artifact publication do not establish native acceptance. After separately authorized deployment, observe town entry and save load in-town, all local notables, zero native-time/UI blocking, concurrent actual requests within the configured allowance, chat before/during/after completion, failed-provider fallback, repeated visits without regeneration, editor preservation, and same-campaign Save Sync restoration. Real provider/native tests retain their normal explicit authorization and disposable-save gates.
