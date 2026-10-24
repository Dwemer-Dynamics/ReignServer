# Frozen memory-context experiment

This standalone development tool evaluates alternatives to the currently working
memory behavior. It has no production hook, setting, feature flag, network client,
provider integration, database connection, service control or game access.
Nothing in these results authorizes production adoption or deployment.

## Comparison arms

`scripts/memory_context/experiment.py` accepts complete retained assembly JSON and
produces four sets of messages in a fresh private output directory:

| Arm | Behavior |
| --- | --- |
| `baseline` | Exact captured messages, including their original role order. |
| `dedup` | Replace repeated complete, source-identified views with references to one retained complete copy. |
| `selective` | Experimental lexical detail detector/ranker selects at most three complete older exchanges within a shared 24,000-character allowance, then deduplicates. An unresolved detail need retains the baseline. |
| `guarded` | Omit mapped older exchange expansions only for narrowly recognized routine greetings, farewells and handovers. Recall and unclassified turns keep the full baseline pool, with an explicit budget exception, then deduplicate. |

The selective policy is a candidate under evaluation, not production semantic
retrieval parity. Its bounded ranking is deliberately visible in the report.
It must not be enabled based on small prompt size or passing structural contracts.
The guarded variant is conservative because uncertainty retains all captured
evidence. Passing recall probes by retaining the baseline is not evidence that a
smaller recall packet is sufficient.

## Preservation and overlap

Every source file remains unchanged. System messages, complete character data,
the latest player contribution and role-separated chronological messages are
immutable. Other reference text is changed only by the explicit experiment.
Derived summaries, relationship state, promise status and current facts remain;
an exact source quoted inside a summary can reference its retained full copy,
while the summary's other facts remain verbatim.

Archive blocks must match the captured required-content receipt before they can
be omitted. Precision shadow diagnostics are only source-identity hints: actual
physical occurrence in the captured messages is mandatory. An unbound block is
retained and reported. Unwrapped foundational relationship originals and
unbound promise blocks remain unchanged. Therefore the proposed compact
relationship foundation and a complete budget across every production lane are
not implemented by this capture adapter.

Deduplication requires a complete source view, its source identity and role.
Similar wording from separate events remains separate. Prefixes, divergent
views, missing identities and ambiguous boundaries are never merged. A retained
reference includes the source ID, role and view hash. Expanding the references
must reproduce the selected pre-dedup messages exactly, including qualifications
and source attribution. The report distinguishes original byte equality from
exact reconstructability after duplicate replacement.

Zero remaining duplicates applies only to the mapped exact views. Differently
formatted structured history, unmapped legacy prose and semantic overlap are
coverage gaps. This is not a claim of zero repeated meaning in the whole prompt.
Production integration would need one source inventory across all prompt owners
and an observer-safe canonical renderer, rather than this frozen-text adapter.

## Source-delivery evaluation

`evaluate_recall.py` consumes a frozen source fixture and a separate labels file.
The fixture contains exact observer/peer IDs, a sequence cutoff, complete source
rows and queries. Both participants must be in each source's audience. Duplicate
IDs or missing source boundaries fail. These frozen checks do not replace the
production timeline, restore-generation, letter-delivery and observer policies.

Candidates are generated before loading answer/source labels. Labels cannot
influence selection, and labels absent from the baseline are rejected. The
result measures complete required-source delivery only. A missing label is
`not_scored`, never a pass. It does not generate or grade NPC prose, assess voice,
or establish native acceptance. Retain original failures when testing another
policy; an adapted development corpus is not an independent holdout.

## Running

Obtain the manifest-selected plan and canonical validation for exact changed
paths. Coordinate the shared validation lease and native testing window first.
The following commands only read frozen inputs and write task-owned outputs:

```text
python -X utf8 -m unittest discover -s ReignServer/scripts/memory_context -p test_experiment.py -v
python -X utf8 ReignServer/scripts/memory_context/experiment.py --capture PRIVATE_ASSEMBLY_JSON --output FRESH_PRIVATE_DIRECTORY
python -X utf8 ReignServer/scripts/memory_context/evaluate_recall.py --fixture PRIVATE_SOURCE_FIXTURE --labels PRIVATE_LABELS --output FRESH_PRIVATE_RESULT_JSON
```

Repeat `--capture` for multiple distinct capture names. Optional
`--budget-characters` (1,000–100,000) and `--max-exchanges` (1–12) are labelled
experimental settings, not changes to production. The guarded fallback explicitly
retains the baseline even when it exceeds the experimental archive allowance.
Each input JSON is limited to 32 MiB, with at most 16 captures per run. The
source-delivery evaluator accepts at most 512 sources and 1,000 unique queries.

Private captures, complete prompts, source text, labels and reports belong in
ignored evidence, never in this public repository. The tool will not overwrite
an existing result directory/file. Run one local process at a time; no GPU or
model inference is used. Canonical build verification remains a separate shared
resource and must yield to the user-prioritized native test owner.

## Review criteria and limits

Keep baseline and candidate source hashes, every final message, exclusions and
selection reasons, source-coverage verdicts, exact reconstruction checks and
measured character counts. Provider token counts, billed cost, latency and
response quality remain unmeasured without separately authorized bounded model
comparisons. There are no new credentials, provider gates or deployment commands.

Reject an optimization that loses required evidence even if its prompt is much
smaller. Do not shorten recent windows, discard unique milestones or infer
summary sufficiency to make a comparison pass. Controlled provider comparisons
and human review are required before concluding that the user's current recall
and characterization are preserved. Live campaign testing and adoption remain
separate decisions.

## 2026-10-14 development findings

Four frozen Ingunde/Corein assemblies showed 0.38–5.13% character reduction from
mapped exact deduplication, with exact reconstruction and no lost mapped unique
source views. Selective allocation showed 26.95–30.41% reduction, but delivered
all labelled evidence in only 3/7 scored queries from a reused 261-source Corein
development corpus. The guarded variant retained 7/7 through baseline fallback;
it reduced the three clear routine captures by 28.23–31.19%, while the callback
capture received only deduplication. Two further corpus queries were unscored.

These findings reject the tested aggressive selector as an adoption candidate.
The conservative variants remain experiments pending provider and human review.
The capture adapter did not implement or certify compact replacement of the
foundational relationship originals. Existing behavior remains the baseline.
