# Isolated Codex roleplay retrieval evaluation

`scripts/codex_roleplay/compare.py` compares two experimental persistent-thread
writers without deploying changes or accessing a running Reign server. It uses
Codex app-server's programmatic thread/turn interface and experimental dynamic
tools. It is a standalone prototype, not a production dialogue adapter or an
installed SDK package benchmark.

## Methods and evidence

Both arms use the same frozen completed character stack, attributed historical
sources, local BGE-small-en-v1.5 embeddings, reasoning effort, model, and short
prose instructions. `automatic` retrieves six complete source turns before each
message, using the current and previous player messages. `on_demand` lets Codex
choose `search_memories` and `read_memories` during generation. Both use chunked
semantic ranking plus the same small exact-name boost. This does not reproduce
the complete production precision, reranking, context-allocation, or repair
pipeline. Source-backed answer review is required; retrieval alone is not recall.

`prepare.py` freezes the retained Corein memory-evidence capture format into a
private development fixture. It preserves the supplied personality and complete
records; no personality generation runs. It adds explicitly labelled follow-up
and current-name-correction controls. These are not independent holdout cases.
The source corpus and evaluation labels must remain separate from the empty
model working directory and must not enter public Git history.

Each repeat uses one thread per method and reverses method order on alternating
repeats. An optional manual compaction must emit the actual `contextCompaction`
item. The next turn creates a fresh thread with the original character stack,
the actual accepted conversation transcript and fresh automatic retrieval or
restored search access. No fabricated summary or oracle facts enter recovery.
This demonstrates explicit boundary recovery, not automatic mid-response repair.

## Isolation and security

- Never connect this experiment to the installed game, server API, campaign
  database, vector archive, production settings, or existing Codex processes.
- The host binds NPC/player identity and the frozen source cutoff. Search/read
  reject scope overrides, inaccessible IDs, future records and excessive input.
  This frozen fixture filter is not a substitute for production knowledge gates.
- Run the embedding helper as an independent one-thread CPU process against
  existing read-only model files. It opens no network listener or vector store
  and does not download models or call a provider.
- Use a separate task-owned `CODEX_HOME` with an existing local authentication
  copy. Do not print credentials or runtime stderr. Remove only that exact
  credential copy in cleanup. Shell, agents, apps and web search are disabled;
  unexpected runtime tool execution aborts the owned process.
- Provider work requires an exact model, explicit confirmation
  `run bounded isolated Codex roleplay comparison`, and a positive operation cap.
  A durable experiment ledger counts starts and compactions across recovery
  attempts. No ambiguous failed generation is automatically replayed. Each turn
  allows at most four lookup attempts, and repeated excess calls abort.
  The cap counts generation operations, not hidden provider-internal inference
  requests. Built-in provider transport retries are controlled by the installed
  runtime; this harness never replays an ambiguous turn.
- Shared subscription and machine load can affect latency even with isolated
  state. Run only one comparison turn at a time. Respect the canonical build
  lease; no deployment or native acceptance follows from these tests.

## Running and reviewing

Obtain the manifest-selected validation plan for the exact changed paths and run
canonical validation. Run provider-free contracts with:

```text
python -m unittest discover -s ReignServer/scripts/codex_roleplay -p test_compare.py -v
python ReignServer/scripts/codex_roleplay/prepare.py --capture PRIVATE_CAPTURE --output PRIVATE_FIXTURE
```

Run `compare.py --help` for the complete bounded interface. `--embed-command` is
a JSON argv array invoking `embed.py` under an existing FastEmbed environment,
with `--model-dir` pointing at the local BGE artifact. On Windows the helper may
run as an independent process inside the existing WSL distro; it must not call
the managed vector service. `--output` must be a fresh private directory. The
model workspace contains no fixtures, rubrics or project instructions.

Retain source, fixture and embedding hashes; generation ledger; per-turn prose,
retrieval IDs, search arguments/results, actual usage or unknown; observed
compaction and new thread IDs; first-text and completion times; failures and
model-verification events. Separate ordinary warm turns from initial startup and
compaction recovery. Requested/listed model identity is not served-model proof.
Reports are resumable evidence, not acceptance certificates. Review supported
facts, attribution, chronology, unknown details, correction handling and voice
against exact original sources. Do not silently drop failed samples or claim
native behavior, action execution, cost savings or full production parity.

Optional `--arms` and `--memory-policy-file` support separately labelled diagnostic
variants. Their results must not replace or be pooled with the original comparison.
The durable operation ledger still applies across all variants.

## 2026-10-12 development evaluation

The retained private comparison used 261 eligible attributed records (603 local
embedding chunks), requested `gpt-6-luna` with medium reasoning, two repeats and
nine turns per method. All 36 replies completed, and four real compactions were
observed. A separate eight-turn search-policy diagnostic used the remaining
operation budget. No game controls, deployment or live campaign writes occurred.

Manual review against original sources rated automatic retrieval 13/18 fully
supported, 1 partial and 4 failed; optional model-directed lookup rated 5/18 fully
supported, 2 partial and 11 failed. Ordinary warm median completion was 2.02s and
1.93s respectively, but the optional-tool arm successfully retrieved on only two
turns. Its fast refusals are not evidence of equivalent memory performance.
Automatic retrieval itself took a median 122ms. Cold starts and compaction recovery
are excluded from those warm medians; recovery medians were 8.71s and 8.73s.

The stronger search-before-denial diagnostic was fully supported on 6/8 turns,
with a 3.07s warm median. It exposed an invalid search limit and an attribution
error. The host correctly rejected the invalid limit, but its original error
message unnecessarily discouraged correction. The final harness permits bounded
argument recovery and has a provider-free transport contract for it; no provider
rerun followed budget exhaustion. Do not pool this diagnostic with the comparison.

These exploratory results support persistent threads and suggest a hybrid of
automatic evidence plus targeted tool lookup. They do not establish production
parity, a universal quality rate, in-game latency, SDK-wrapper performance or
served-model identity. The historical character stack included stale failed-recall
state, and shared account/machine load was uncontrolled. Full private replies,
rubric, per-case review, tool receipts, source hashes and timings are retained in
the client checkout's ignored `.codex-build/codex-roleplay-01a1315b/` evidence.

After the user authorized stronger models/reasoning, a separate 12-turn
comparison requested `gpt-6.1-sol` at high reasoning on six factual probes, with
the same stronger retrieval policy applied to both arms. Model-directed lookup
answered all six with supported attribution, including the invitation missed by
automatic selection; automatic retrieval answered five. This has one repeat,
changes both model and reasoning from the original comparison, and does not test
compaction. It strengthens the case for targeted lookup but is not a controlled
estimate of the benefit of reasoning effort alone.
