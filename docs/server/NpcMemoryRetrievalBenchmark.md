# NPC-memory embedding comparison and reproducible benchmark

Full-pipeline follow-up (2026-10-18): `pipeline_worker.py` is a nonce-scoped,
network-denied adapter around production worker handlers, Qdrant/local cosine,
source filters, cache and HTTP. Existing artifact-bound `memory_evidence_replay`
accepts bounded `denseWorkerReplay` only in non-listening ReignValidation. It
materializes verbatim source-linked reported-speech memory projections, indexes
each unchanged history once and runs real semantic/SQL fusion and final writer
packing. Such controlled derivatives are not restored saved summaries/native
state. BGE keeps its default512-token window; MiniLM uses256 with whole-source
overlapping chunks. Existing model pins remain authoritative. The paired testing
guide and catalog specify input bounds, identity handshake and exact operations.
Labels remain separate. Retain no-answer candidate/context rejection, complete
source/speaker coverage, first/repeated latency and both process RSS; no model
switch, provider generation, migration or native qualification follows automatically.
The isolated index attaches the current restore generation exactly as the
production indexing queue does. Each new history must pass an observer/timeline/
generation-filtered search for an owned indexed memory before any result is graded.
This bounded eligibility probe warms the local matrix equally for both models;
first-query timings refer to the first question after this index check.

Measured 2026-10-18: both pinned models completed the same53 cases (33 recorded
conversation questions,8 controlled positives,12 controlled no-answer questions),
three rounds each, through the same immutable server artifact. Fifteen isolated
history scopes held533 source-linked memory projections. The installed BGE graph
and tokenizer hashes matched the baseline pins. These are English development
replays with controlled derivatives, not restored saved-summary/native acceptance.

| Pipeline measurement | BGE-small-en-v1.5 | all-MiniLM-L6-v2 |
| --- | ---: | ---: |
| Complete required originals in final writer request | 56/56 across41/41 positive cases | 56/56 across41/41 |
| All required source documents among first five semantic hits | 32/41 cases | 27/41 cases |
| No-answer queries returning zero semantic candidates | 0/12 | 0/12 |
| First-question semantic-search p95 | 36.4ms | 18.8ms |
| First-question retrieval plus writer-request p95 | 2041.6ms | 2212.4ms |
| Repeated retrieval plus writer-request p95 | 2013.0ms | 2104.8ms |
| Isolated worker peak RSS | 469.5MiB | 362.6MiB |
| Largest simultaneous server-plus-worker RSS sample | 1650.2MiB | 1540.5MiB |

Complete recall requires full original text, correct source ID and speaker role
in the captured provider wire request. No-answer packets selected14 unrelated
memory records each for BGE and12-14 for MiniLM; both delivered all14 controlled
background originals. No generated response was tested. No payload campaign,
timeline, restore-generation, owner or requested world-day leakage was observed.
MiniLM reduced worker peak RSS by22.8% and first-question search p95 by48.4%,
but showed no full-pipeline latency improvement and weaker first-five ranking.
Sequential runs on a shared host do not establish a causal latency regression.
Both native-window chunk policies retained full source coverage; MiniLM generated
1225 chunks versus713 for BGE. Its model files and vector store were larger even
though its resident worker was smaller.

Retain BGE. A performance-driven switch needs preserved source recall and scope
protection, calibrated no-answer rejection on a separate held-out set, and a
repeatable foreground or total-memory benefit on representative saved derivatives
and target hardware. The earlier raw-source dense ranking probe uses a different
document representation and cannot substitute for these pipeline measurements.
Qualification and model migration/rollback acceptance remain open.

Evidence: private `.codex-build/npc-memory-pipeline-01a1459f/RESULTS.md`,
`comparison.json`, frozen inputs/labels and both complete writer captures.
Release artifact validation `20261019-020314-25fbe6cf`, source fingerprint
`fa7af99c988023eda939930f5b7880574762cfb02a87cc5a77a6c22ffea7af7d`,
server SHA256 `c68b532f0fea7d8d9ecd7304fc76d7431b12f7db581856d4fa0776435eaa7ada`.
The initial generation-metadata smoke failure is retained and excluded; corrected
index probes and16 replay contracts passed for both arms. Both test workers were
stopped; no installed model/settings/campaign or provider output changed.

Prepared 2026-10-16. Recommendation: retain the current production model while
qualifying Bekko a8m first and Granite 107M second on representative Reign history.
The small synthetic experiment establishes a runnable comparison and exposes
threshold failures; it does not measure campaign accuracy or justify adoption.

## What the external result establishes

[SkyrimNet Beta27 release notes](https://github.com/MinLL/SkyrimNet-GamePlugin/releases/tag/vbeta27)
report the right memory appearing among five results on real Skyrim saves:

| External model | English | Russian | Reported download |
| --- | ---: | ---: | ---: |
| all-MiniLM-L6-v2 | 65% | 46% | Not specified here |
| Granite Embedding 107M Multilingual | 71% | 88% | 442 MB |
| Bekko Embedding v1 | 76% | 93% | 234 MB |

The announcement supplied by the user is dated October15; GitHub displays the
release on October14. These are distinct publication dates. The public notes also
describe stricter relevance defaults, changed similarity scaling, background
re-embedding and restoration of the old selection if download/loading fails.
The notes do not supply the full query set, label process, sample size, uncertainty
or hardware needed to reproduce those percentages. They establish a reason to
investigate, not a transferable Reign improvement estimate. The user's Gmail link
could not be read; the public primary release is the supporting source.

## Reign's actual baseline and candidate approaches

Current source: `services/vector-worker/worker.py`,
`src/Modules/Dialogue/SemanticMemory.cs`, `MemoryContextV2.cs`,
`docs/server/MemoryPrecision.md`, and `release/embedding-model.lock.json`.
Reign uses BGE-small-en-v1.5, a pinned quantized ONNX model through FastEmbed,
384-dimensional cosine vectors, persistent local Qdrant with a compact float32
search matrix, and an optional external Qdrant provider. Both query and document
currently use `embed()` without an instruction prefix. NPC memory is a hybrid
architecture: SQL/text discovery, entity/source routing, semantic candidates,
observer/time/restore checks, linked originals and complete-record prompt packing.
Optional specialist/late-interaction and reranking paths have separate qualification.
PostgreSQL originals and native receipts retain authority over derived vectors.

| Approach | Relevant properties | Reign implication |
| --- | --- | --- |
| Existing BGE-small | English-oriented;384 dimensions; locked ONNX weights66,465,124 bytes | Required baseline; preserve current preprocessing and hybrid behavior. |
| MiniLM-L6-v2 |384 dimensions, mean pooling,256-token default limit | Historical comparator, with no demonstrated advantage over Reign's BGE baseline. |
| Granite107M |384 dimensions, CLS pooling,512-token limit; multilingual | Candidate for multilingual retrieval; larger weights/RSS require measurement. Russian is absent from the card's12 explicitly supported languages, despite SkyrimNet's measured Russian gain. |
| Bekko v1 a8m |384 dimensions, mean pooling, raw text without prefixes; broad multilingual coverage, longer context available | Promising CPU candidate. Test at512 tokens first to isolate model effects from changed chunking; longer windows are a separate experiment. |
| Hybrid BGE/candidate + text and optional reranker | Exact names, numbers and source links alongside semantic recall | Compare complete delivered evidence, not just nearest vectors; embeddings cannot enforce privacy, current debt status or native truth. |

Primary model specifications:
[MiniLM card](https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2),
[Granite card](https://huggingface.co/ibm-granite/granite-embedding-107m-multilingual?library=transformers),
[Bekko a8m card](https://huggingface.co/hotchpotch/bekko-embedding-v1-a8m).
Bekko's active parameter count describes computation, not total weight/RAM size.
The tested a8m ONNX weighs130,099,079 bytes plus its tokenizer/config; it has **not**
been established as SkyrimNet's234MB packaged variant. Candidate artifact choice,
quantization, runtime and tokenization are part of each experimental arm.

Equal384 dimensions do not mean compatible vector spaces. Never search old BGE
document vectors with a new model's query vector. Changing dimensions or using
Bekko's smaller representations would additionally change schema/health contracts.

## Benchmark and example labels

`scripts/npc_memory_benchmark/benchmark.py` consumes `fixtures.json`, with97 authored
synthetic memories and32 English/Russian queries. Each split has12 answerable
queries and4 no-answer queries. Development/test topics differ, but translations
share source facts and are correlated. All are development probes, not an untouched
independent holdout. The fixture deliberately mixes English and Russian source
memories; each query language therefore includes both same-language and cross-language
retrieval. The language numbers cannot be called accuracy on English-only saves.

| Category | Query example | Required evidence / failure |
| --- | --- | --- |
| Personal | Where did you shelter from thunder as a child? | Grandmother's orchard and pear trees; a recent market visit is irrelevant. |
| Personal | Which knee did you hurt, and how long did I tend it? | Left knee and six days; preserve numerical details. |
| Relationships | What repaired your friendship with Liena? | Returned family ring and private apology; another observer cannot inherit the private source. |
| Relationships | Do you still owe me700 denars? | Repaid debt is grade3; the old borrowing promise is supporting grade1, not proof the debt remains unpaid. |
| Historical | How did the Askar famine end; were taxes raised? | Three grain caravans; taxes unchanged. Ordinary grain purchases are distractors. |
| Irrelevant / absent | What medicine cured my mother's illness? | No supported answer; returning the closest unrelated memory is failure. |

Each of12 core memories has six identical-text forbidden copies: wrong observer,
future day, other timeline, other campaign, stale restore generation and inactive
source. These72 traps test the benchmark's eligibility oracle before ranking.
This simplified oracle does not implement Reign's complete knowledge, regional
public-history, source-hash, letter-reading or Save Sync policies. Passing it is
not production privacy certification. No-answer queries remain in reports.

The ranker sees only text and scope metadata, never relevance grades. The evaluator
uses judgments afterward. Grades1–3 distinguish support from essential evidence.
Each complete memory/document is one result; production chunk-to-source grouping
is outside this microbenchmark.

## Measurements and interpretation

- **Hit@5**: fraction of answerable queries with at least one judged relevant
  document in five results. This is the closest explicit analogue to SkyrimNet's
  described top-five accuracy, whose exact evaluator is unknown.
- **Recall@5**: per-query fraction of all judged relevant documents retrieved;
  average over answerable queries. One found fragment is insufficient for a
  multi-source answer. **Precision@5** uses five slots even after abstention;
  **returnedPrecision** uses actual returned count, macro-averaged over answerable
  queries. Negative-query pollution is reported separately.
- **MRR@5**: reciprocal rank of the first relevant result, zero if absent.
  **nDCG@5** uses graded gain `2^grade-1` and logarithmic discount against the ideal
  judged ordering. These distinguish one lucky top-five hit from good ranking.
- **False retrieval**: fraction of no-answer queries returning any memory after
  thresholding; correct abstention is its complement. Raw top-five search normally
  fails all no-answer cases. A very high threshold can abstain on everything, so
  never report this metric without answerable recall.
- **Thresholds**: choose the highest development recall satisfying at most5%
  development false retrieval, then precision and lower-threshold tie breaks.
  Freeze that model-specific raw-cosine threshold before reading test labels.
  Four development negatives cannot statistically qualify a5% target; this is a
  mechanical calibration demonstration, not a production threshold recommendation.
  SkyrimNet's rescaled0.6 relevance setting must not be copied into Reign.
- **Latency**: fresh process per arm, two CPU inference threads, three uncached
  repetitions per query. Record model construction plus file hashing, total
  document embedding time/throughput, first query after indexing, and warm
  p50/p95/p99 for query embedding, eligibility scan, exact search and their total.
  Warm-up and measured repeats perform inference; this is not a cache-hit test.
  Model loading/hash time is not a cold OS-cache guarantee. Query and label
  evaluation, HTTP, SQL, packing and writer generation are not blended together.
- **Memory**: current resident bytes and process-lifetime peak resident bytes
  before model creation, after model, after index and after queries. Includes
  native allocations; differs from Python-only allocation counters. Separately
  report model-file bytes and actual float32 matrix bytes. Report GPU VRAM as
  unmeasured for these CPU runs. Full production payloads, Qdrant storage, caches,
  normalized spare rows and optional specialist models require separate measurement.

For384 float32 values, raw vectors use1,536 bytes per chunk:100,000 chunks require
146.5MiB and1,000,000 require1.43GiB, before all other costs. Count chunks, sources,
NPCs and eligible candidates separately. A bigger model loads once per worker,
not once per NPC; population primarily increases indexed history and filtering.

## Reproduce

Use Python3.13 and the exact package versions retained in the run reports:
NumPy2.5.3, ONNX Runtime1.29.0, tokenizers0.23.2, FastEmbed0.8.0. These are the local
experimental environment, not a change to Reign's pinned production dependencies.
Install them into an isolated environment if needed. Run:

```powershell
python -X utf8 -m unittest discover -s ReignServer/scripts/npc_memory_benchmark -p test_benchmark.py -v
```

`models.json` pins the repository, commit, remote/local filename, bytes and SHA256
for every required artifact. Explicitly stage public weights into an ignored,
task-owned directory, verify every size/hash against that manifest, and flatten
the remote `onnx/model.onnx` to the listed local `model.onnx`. BGE files must match
the release lock. Downloading public files is a separate preparation step;
the benchmark has no downloader, credentials or inference endpoint. For example:

```powershell
$modelRoot = 'D:/Projects/Reign/.codex-build/my-memory-comparison/models'
$modelLock = Get-Content ReignServer/scripts/npc_memory_benchmark/models.json -Raw | ConvertFrom-Json
foreach ($candidate in $modelLock.models) {
    $target = Join-Path $modelRoot $candidate.name
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($asset in $candidate.files) {
        $file = Join-Path $target $asset.file
        if (Test-Path -LiteralPath $file) { throw "Use a fresh preparation directory: $file" }
        $url = 'https://huggingface.co/{0}/resolve/{1}/{2}' -f $candidate.repository,$candidate.revision,$asset.remote
        Invoke-WebRequest -Uri $url -OutFile $file
        if ((Get-Item -LiteralPath $file).Length -ne $asset.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.sha256) {
            throw "Model asset verification failed: $file"
        }
    }
}
```

From the Reign checkout, use a fresh output filename for each process:

```powershell
python -X utf8 ReignServer/scripts/npc_memory_benchmark/benchmark.py --backend fastembed-bge --model-dir "$modelRoot/bge" --model-id BAAI/bge-small-en-v1.5 --revision 52398278842ec682c6f32300af41344b1c0b0bb2 --output .codex-build/my-memory-comparison/bge.json
python -X utf8 ReignServer/scripts/npc_memory_benchmark/benchmark.py --backend onnx --model-dir "$modelRoot/bge" --model-file model_optimized.onnx --pooling cls --model-id BAAI/bge-small-en-v1.5 --revision 52398278842ec682c6f32300af41344b1c0b0bb2 --output .codex-build/my-memory-comparison/bge-onnx.json
python -X utf8 ReignServer/scripts/npc_memory_benchmark/benchmark.py --backend onnx --model-dir "$modelRoot/minilm" --pooling mean --max-tokens 256 --model-id sentence-transformers/all-MiniLM-L6-v2 --revision 1110a243fdf4706b3f48f1d95db1a4f5529b4d41 --output .codex-build/my-memory-comparison/minilm.json
python -X utf8 ReignServer/scripts/npc_memory_benchmark/benchmark.py --backend onnx --model-dir "$modelRoot/granite" --pooling cls --model-id ibm-granite/granite-embedding-107m-multilingual --revision d6cffd338414d6a1c1f5decfad5fec62eebc90d5 --output .codex-build/my-memory-comparison/granite.json
python -X utf8 ReignServer/scripts/npc_memory_benchmark/benchmark.py --backend onnx --model-dir "$modelRoot/bekko-a8m" --pooling mean --model-id hotchpotch/bekko-embedding-v1-a8m --revision c721113d59a1d91b447450324f51c4b3332c924a --output .codex-build/my-memory-comparison/bekko.json
```

Defaults are two threads, three repeats,512 tokens and no prefixes. Both ONNX
pooling recipes normalize float32 output. Unsupported graph inputs/outputs,
invalid vectors, invalid labels or an existing output fail explicitly. Frozen
fixtures are bounded to32MiB,100,000 memories and1,000 queries. Reports retain
runner/fixture/model hashes, exact arguments and runtime/CPU information.

Add `--extra-npcs 1000 --memories-per-npc 20` for20,000 synthetic extra memories;
every tenth extra memory is visible to the benchmark observer, the rest are
other-NPC histories. This exercises loaded population and larger eligible pools,
not realistic distributions or concurrent production service latency. Scale
commands use the same model and a new report path.

## Observed synthetic development results

Final reports: private Reign checkout
`.codex-build/npc-memory-benchmark-01a1459f/*-final.json`. All share runner SHA256
`223748f6068da24c423ba0c4c49aaa5c863b912af0f3539dd05b96887fc5463e`.
There are only12 held-topic positive queries, including translations of six facts,
and four negatives. Model runs were sequential; other workstation activity and
native testing were not paused. These are local microbenchmark observations.

| Arm | Raw Hit@5 | Raw Recall@5 | Warm total p95 | Peak process RSS |
| --- | ---: | ---: | ---: | ---: |
| Locked BGE / current FastEmbed adapter |58.3% |54.2% |167.7ms |212.9MiB |
| Same BGE / common ONNX adapter |58.3% |54.2% |173.1ms |182.8MiB |
| MiniLM / ONNX |58.3% |54.2% |17.0ms |197.7MiB |
| Granite107M / ONNX |100% |100% |10.9ms |1084.7MiB |
| Bekko a8m / ONNX |100% |100% |8.2ms |405.4MiB |

English-query Hit@5 is66.7% and Russian-query Hit@5 is50% for BGE/MiniLM;
Granite/Bekko achieve100% for each on this mixed-language corpus. BGE's common
adapter gives the same document ranking, and it remains slow on this Windows
CPU/runtime/artifact combination. This warrants a separate quantized-graph/runtime
profiling investigation; it does not establish live Reign latency or a universal
model speed ordering. Tests across supported CPU architectures and the production
Linux runtime remain necessary.

Development-calibrated thresholds are approximately0.80457 BGE,0.70893 MiniLM,
0.66770 Granite and0.25795 Bekko. BGE/MiniLM reject every held-topic positive;
Granite retains95.8% recall and Bekko100%, but both return a memory on two of four
no-answer queries. Thus **none qualifies the combined recall/abstention goal**.
Do not adopt these thresholds. Complete failed rankings remain in the reports.

The Bekko scale probe loaded20,097 documents for1,000 extra synthetic NPCs, with
2,025 eligible documents per query. Indexing took92.4seconds (217.5documents/s).
Warm total p95 was23.2ms: query embedding8.4ms, filtering11.8ms and exact search
5.1ms when measured separately (component percentiles are not additive).
The float32 matrix occupied29.4MiB; process peak RSS was480.5MiB and final resident
RSS310.9MiB. Ranking/abstention results remained unchanged. These generated short,
repetitive histories are a scale smoke probe, not a representative population or
an observed production load. There were no truncated inputs in these runs.

## Immediately testable versus representative-data work

### Real-history comparison requested on2026-10-16

The follow-up experiment uses397 distinct original turns from15 frozen conversation
histories,100 exact-source lookup controls,26 newly authored source-checked natural
paraphrases and seven previously reviewed Corein callbacks. The new questions and
labels were frozen before their candidate ranks were inspected. This is reused
development evidence from a few established characters, not an independent
representative holdout. Exact-source controls and natural questions are separate.

Models run in fresh low-priority processes under the installed Linux worker's
Python3.11 environment: FastEmbed0.7.4, ONNX Runtime1.22.0, NumPy2.2.6 and
tokenizers0.23.2. Inference is CPU-only with two threads and three uncached query
repetitions. Token windows are448 with48 overlap for BGE/Granite/Bekko and224 with
48 overlap for MiniLM's shorter context. Source tails remain indexed; source-level
ranking uses the maximum chunk score. These native-window configurations include
a chunking difference, so the result is an approach comparison rather than a
controlled claim about weights alone. No production metadata prefix, hybrid SQL
fusion, reranker or final packing is reproduced by the dense ranker.

| Dense approach | Hit@5,33 natural questions | Hit@5,100 exact lookups | Natural-query p95 | Peak process RSS including indexing |
| --- | ---: | ---: | ---: | ---: |
| Current BGE weights / FastEmbed |78.8% |93% |34.5ms |411.9MiB |
| MiniLM |87.9% |91% |11.6ms |308.3MiB |
| Granite107M |81.8% |100% |13.8ms |1361.8MiB |
| Bekko a8m |84.8% |100% |10.3ms |614.8MiB |

Hit@5 means at least one labelled required original is found, not a complete
answer. On the seven harder callbacks, BGE/MiniLM/Bekko each retrieve only five of
18 required originals within five results; Granite retrieves four. None supplies
all required originals for any of those seven cases at that limit. Thus the
synthetic100% results do not establish real-history callback sufficiency.

MiniLM has the highest natural-question hit count in this English development
sample, while Bekko combines the fastest query stage with100/100 exact lookups.
Granite has the largest memory cost and the weakest callback hit rate. Do not
infer a statistically established winner from differences of one to three cases.
The earlier Windows latency table is not a live Reign performance estimate.

The current validated artifact's `memory_accuracy_replay` additionally compares
production legacy/precision SQL packets for107 cases, followed by33 natural
questions. The raw legacy packet plus recent transcript retains26/44 labelled
originals on the latter set; the precision packet retains43/44. These packet-only
figures must not be described as the current final writer prompt: that path adds
continuity and performs shared allocation. `memory_evidence_replay` captures that
production writer envelope with an explicit fresh-session transcript and the
current shadow mode. Its initial, follow-up and repair requests retain the mapped
native-profile binding, which preserves the ordinary
required political context inside the disposable fixture; no live character
profile or provider dispatch is needed. Missing writer responses remain pending
for this retrieval-only capture and cannot certify generated answers.

Complete private inputs, ranks, packet/envelope coverage, controlled-chunk results,
failures, hashes and validation receipts are retained under the private Reign
checkout's `.codex-build/npc-memory-current-01a1459f/`. Its final `RESULTS.md` owns
the comparison verdict and any remaining coverage gaps. Keep snapshots and
labelled sources out of the public server repository. No model switch is part
of these tests.

Immediately available: synthetic ranking and no-answer calibration, scoped
eligibility traps, pinned artifact loading, CPU inference/RSS, same-backend
comparison, deterministic repeat rankings and bounded synthetic population scale.
The metric/privacy/calibration contracts are provider-free. No game launch,
campaign data, installed archive, production settings or paid calls are required.

Representative evaluation needs a deliberately frozen private export of actual
eligible sources and queries, preserving campaign/timeline/restore generation,
observer/audience, timestamps, source IDs/hashes and original full records. Include
long-established and new NPCs, companions/notables/rulers, common and rare events,
similar names, exact amounts/negation, corrected promises/debts, public and private
knowledge, letters before/after reading, historical/future/other-branch traps,
long narratives and language/code-switching frequencies actually used by players.
Keep all private snapshots and reports out of the public server repository.

Start with at least300 independently judged query situations across30 NPCs and
multiple early/middle/late histories; include at least60 genuine no-answer cases.
This is a starting sample, not guaranteed power. Two reviewers should label exact
required/supporting sources before seeing model outputs and adjudicate disputes.
Split by NPC/campaign/event family, so translations and paraphrases cannot leak
across calibration/test sets. Freeze model recipes and thresholds before a fresh
untouched holdout. Report denominators, per-language/category results and paired
bootstrap confidence intervals clustered by NPC/event; add data if uncertainty
cannot resolve the proposed improvement or guardrails.

Then replay both candidate and baseline through the actual production hybrid
discovery, source revalidation, chunk grouping, optional reranker and final prompt
packing with identical budgets. Score full required-source delivery and wrong
identity/time/confidentiality violations separately. Existing
`memory_accuracy_replay` compares production SQL legacy/precision packets with
semantic retrieval disabled; it cannot by itself qualify a new embedding model.
NPC answer correctness/voice and native acceptance are additional stages, not
inferred from retrieval metrics.

Measure realistic50/500/2,000-NPC distributions at actual chunk counts, p50/p95/p99
foreground latency under1/4/8 simultaneous requests and background indexing,
cache-hit versus uncached queries, worker startup/archive reconstruction, index
disk bytes, total process RSS and optional VRAM. Keep the production thread limit,
hardware, runtime and history snapshot fixed for paired runs. Hardware budgets
must be approved before adopting a model rather than inferred from its download.

## Proposed conditions for a model change

These are recommended acceptance criteria, not measured product requirements:

1. On the untouched representative holdout, gain at least5 percentage points in
   document Hit@5 overall, or10 points in a materially used underserved language,
   with a paired95% confidence interval above zero. No major category/language
   should regress by more than2 points; preserve complete multi-source recall and
   delivered evidence rather than winning only the easiest single-source cases.
2. At the frozen calibrated threshold, no-answer false retrieval at most5% with
   stated confidence bounds, no deterioration in positive recall/precision, and
   zero observed wrong-observer/campaign/timeline/generation/future-source leakage.
   A small test with zero failures is not proof of a zero production rate.
3. On supported target hardware, meet a predeclared foreground budget: proposed
   p95 embedding-plus-vector stage at most100ms and no more than20% regression
   versus baseline under the same load. If the baseline itself misses100ms,
   require a material improvement and an explicit product-budget decision.
   Retain p99, queue delay and end-to-end prompt-assembly evidence separately.
4. Fit an agreed total worker RAM budget with at least25% headroom; provisionally
   permit no more than256MiB incremental model RSS on the minimum supported
   machine. Count index/payload/cache/specialist memory too. If exceeded, require
   enough demonstrated quality benefit and an explicit revised hardware budget.
5. Qualify versioned side-by-side indexes bound to model/tokenizer/pooling/chunk
   hashes and source generations. Build in background with foreground priority;
   verify counts, source hashes and coverage before atomic activation. Retain
   the old index/config until failed download/load, interrupted indexing,
   cancellation, restart and rollback tests succeed. Recheck Save Sync restoration
   and stale-job invalidation. Never roll back the campaign database to roll back
   an embedding model, and never mix vector spaces during migration.

Bekko a8m is the first candidate to qualify because this probe combines strong
multilingual ranking with substantially less RSS than Granite. Granite is a useful
second candidate, particularly if independently judged Reign queries establish
better precision or coverage. MiniLM has no demonstrated reason to replace BGE.
Production architecture, thresholds and installed model remain unchanged by this
benchmark. Canonical validation and exact evidence are retained in the task's
`RESULTS.md`; representative qualification remains an unchecked roadmap item.
