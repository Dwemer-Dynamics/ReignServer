# Structured action decisions

`leave_spouse` (2026-10-23) binds the consenting NPC's `spouseId` and `spouseClanId` from supplied native `currentMarriage` facts. Both signatures authorize only writing to that spouse that the marriage is over and ending it. It implies no romance, new marriage, party invitation, travel or clan transfer. The attached request adds private major-consequence guidance; NPCs may refuse or negotiate. A successful native receipt clears reciprocal spouse/romance state and imposes -50 native relation with the spouse and each other living adult member of the spouse's clan, excluding the leaver and duplicates. A marriage change or clan change blocks execution. Deferred/conditional/repeated divorce and a bundled new marriage are rejected.

The native saved agreement receipt and existing World History outbox carry `source=explicit_spouse_letter`, exact operation identity/hash, the notice and relationship before/after rows. Ingestion reads its immutable stored event, then atomically stores one immediately effective private notice and applies -50 in both authoritative affinity directions per affected pair. Deterministic notice identity scopes retries to timeline and operation. No paid provider, new endpoint or campaign authority is introduced. Provider-free policy, negotiation and transaction/replay/reopen checks cover this in `contracts.structured_actions`; actual native conversation, effect and save/reload acceptance remain required.

Speech alone does not authorize an action. The hidden decision must identify the exact player-signed proposal, revision, terms hash, participants and conversation. Acceptance does not replace government approval or native fulfillment.

The model contract requires empty `clauses` for `accept`, `reject` and `none`. Counteroffers and new proposals supply complete terms. The shared contract validator continues to reject replacement terms on an unchanged decision.

On 2026-10-11, native action tests exposed provider responses that accepted a valid proposal while redundantly echoing its exact executable terms. The server now canonicalizes that formatting to the ordinary empty-clause decision, without another provider call. This applies only to an otherwise valid, authorized `accept` or `reject`, with the complete ordered clause list, identical actions, participants, argument keys/values, timing, dates, duration, installments and conditions. Unknown term or argument fields, duplicate arguments, omitted/extra/reordered clauses, changed identities or signatures, and `none` with clauses remain blocked. The raw provider response is preserved; the validated envelope records `unchangedTermEchoCanonicalized: true`.

Provider-free coverage is included in `RunStructuredActionSelfTests` under Verification Lab's `contracts.structured_actions` check. Native acceptance still requires exact conversation consent, the real government process where applicable, observed effects, once-only replay and checkpoint persistence. A normalized response does not itself prove fulfillment.

## Dialogue transport deadline

The managed Apache proxy allows 360 seconds, matching the native client's local-server request window. A provider's first 300-second attempt can time out and a retry can still return within that window. The proxy must not replace that valid response with the offline HTML page at 300 seconds. This does not extend the client's deadline or allow unlimited retries.

After a transport failure, inspect the native agreement journal, queued actions and actual world state before retrying the exact proposal. A server-side generated acceptance alone does not prove the client received it or that an agreed effect ran. Preserve refusals and never rewrite a missing native receipt as successful execution.
