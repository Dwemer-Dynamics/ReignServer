# Conversation response recovery

Ordinary individual and group/event dialogue use one authored response per
generation attempt. Response recovery is deterministic.

`TryParseJsonObject` parses one complete outer object, optionally in one JSON code
fence. It rejects duplicate outer property names, multiple documents and malformed
JSON. It never searches braces, quotes, private arrays or candidate scores for a
replacement. Reasoning cleanup removes an actual leading provider think preamble
and explicit private reasoning fields; literal tags inside authored JSON strings
remain unchanged.

Conversation-only prefix recovery is bounded to 1 MiB of characters, 64 JSON levels
and 256 outer properties. Complete values are decoded through the production JSON
serializer. An unambiguous truncated object with a complete outer `reply` can
supply speech. Reign keeps only the complete reply from the provider output.
An unreadable response fails the turn and the player can explicitly retry.

When Ollama Cloud is configured, eligible dialogue and event writers can also
return one exactly delimited speech-only answer. Reign can publish that NPC line
to the transcript while withholding gameplay effects from the line. Structured
responses still go through separate action-authority, source and atomic
acceptance/replay checks.

The earlier `reign-conversation-response-recovery-v1` contract recorded exact
content hashes, the UTF-8 reply span, completed and unfinished fields, the
provider finish reason, and effect completeness. It also supported one format
regeneration. That regeneration is outside ordinary single-pass delivery.
Retained raw provider bytes still follow the opt-in private full-capture policy.

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
