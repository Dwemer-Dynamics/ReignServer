# Conversation response recovery

Updated 2026-10-19. `reign-conversation-response-recovery-v1` records recovery of
provider output without granting nested private records the authority of dialogue.

`TryParseJsonObject` parses one complete outer object, optionally in one JSON code
fence. It rejects duplicate outer property names, multiple documents and malformed
JSON. It never searches braces, quotes, private arrays or candidate scores for a
replacement. Reasoning cleanup removes an actual leading provider think preamble
and explicit private reasoning fields; literal tags inside authored JSON strings
remain unchanged. Legacy `text` replies require positive conversation metadata.

Conversation-only prefix recovery is bounded to 1 MiB of characters, 64 JSON levels
and 256 outer properties. Complete values are decoded through the production JSON
serializer. Incomplete arrays contribute no entries. Diagnostics retain the original
exact content hash, UTF-8 reply span in the sanitized assistant source (including
leading whitespace/fences), the original pre-cleanup provider content hash when
available, completed fields, unfinished field, original finish
reason and effect completeness. Retained raw provider bytes continue to follow the
existing opt-in private full-capture policy; recovery does not enable capture.

A complete outer reply and complete effect fields may survive a missing final brace.
A cutoff inside any outer value or property name, including unknown future fields,
or before a required private drinking array, instead uses
one ordinary full-context format regeneration before acceptance. This is not a drink
judge or an inference of missing choices. The second response must be usable or the
turn fails without publishing partial effects. Exact structured decision identity,
normal semantic validation and atomic accepted-unit/replay guards still apply.
Unknown metadata never authorizes native actions, drinks or consent. Ambiguous
provider restarts are rejected rather than mistaken for nested or quoted answers.

Ordinary individual and group/event generation reserve at least 16,000 completion
tokens, including any provider reasoning. Larger configured limits remain intact.
The prompt requests reply, structured decision and drink choices before optional
metadata, with concise summaries and all required source evidence retained. Building
does not change installed model/reasoning choices or settings. Neither the budget
nor format validity certifies generated prose or native execution.

`RunConversationResponseParsingSelfTests` runs within the existing isolated
`interaction_architecture` suite. It uses anonymized structural regression fixtures
and injected provider responses, every-character cutoff checks, ambiguity/Unicode/
source-span controls and a run-owned PostgreSQL drinking/replay fixture. Retain
canonical Release validation, architecture, prompt, structured-action and intoxication
evidence. Existing user campaigns, transcripts and counters are never backfilled.
