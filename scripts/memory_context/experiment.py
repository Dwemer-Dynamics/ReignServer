"""Offline, frozen-prompt experiment. No network, runtime, provider or DB access.

This tests a proposed allocation policy, not the production retrieval pipeline.
All source bytes remain in the input and the retained baseline output. Derived
summaries and current character/relationship/state instructions are immutable.
"""
from __future__ import annotations

import argparse
import collections
import copy
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import re
import time

SCHEMA = "reign-memory-context-experiment-v1"
POLICY = "frozen-complete-exchange-selective-v1"
BLOCK = re.compile(r"\[\[continuity ([^\]\n]+)\]\]\n(.*?)\n\[\[/continuity\]\]", re.S)
TURN = re.compile(r"(?m)^- \[Day [^\]\n]+\] \[source=([^\s\]]+) role=([^\s\]]+)\] ([^\n]+)")
EXPANDABLE = {"targeted_historical_exchange", "commitment_source_context"}
STOP = set("a an the and or but if then else is are was were be been being i me my mine you your yours we us our ours he she it they them his her their this that those these to of in on at by for from with as about do did does can could would should will shall may might have has had just now so very much only please tell said say what when where why who which how remember recall remind again before after already still exactly exact last time first really know think want look forward here there not yes no into out up down over under also all any some more most im its dont youre thats lets lets".split())
RECALL = re.compile(r"\b(?:remember|remind|recall|promis\w*|pledge\w*|agreed|agreement|conditions?|terms|last time|earlier|previously|used to|what (?:did|was|were)|who (?:did|was)|when did|which|how many|how much|tell me (?:the|that) story|what happened|what you (?:said|meant)|what i (?:said|meant))\b", re.I)
FOLLOWUP = re.compile(r"\b(?:then|that|it|he|she|they|afterwards?|next|the other|and what|and who)\b", re.I)


def digest(value):
    if not isinstance(value, bytes):
        value = value.encode("utf-8")
    return hashlib.sha256(value).hexdigest()


def read_json(path):
    path = Path(path)
    if path.stat().st_size > 32 * 1024 * 1024:
        raise ValueError("Frozen JSON exceeds the 32 MiB experiment limit")
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write_json(path, value):
    with Path(path).open("x", encoding="utf-8") as stream:
        stream.write(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def terms(value):
    words = re.findall(r"[\w]+", value.casefold())
    # Only simple inflection handling; deliberately no claim of semantic parity.
    return {w[:-1] if len(w) > 4 and w.endswith("s") and not w.endswith("ss") else w
            for w in words if len(w) > 2 and w not in STOP and not w.isdigit()}


def fields(header):
    return dict(re.findall(r"([A-Za-z]+)=([^\s]*)", header))


@dataclass(frozen=True)
class Block:
    message: int
    start: int
    end: int
    text: str
    kind: str
    source: str
    body: str


@dataclass(frozen=True)
class Source:
    id: str
    role: str
    text: str
    body: str

    @property
    def key(self):
        return self.id, self.role, digest(self.text)


def sources_in(text):
    """Parse only a bounded, already-rendered source record, never live input."""
    matches = list(TURN.finditer(text))
    result = []
    for i, match in enumerate(matches):
        end = matches[i + 1].start() if i + 1 < len(matches) else len(text)
        raw = text[match.start():end].rstrip()
        # Commitment records put an extra turn_id prefix before the next turn.
        raw = re.sub(r"\nturn_id=[^\s;]+;\s*$", "", raw).rstrip()
        body = raw[match.end() - match.start() - len(match.group(3)):]
        result.append(Source(match.group(1), match.group(2), raw, body))
    return result


def message_chars(messages):
    return sum(len(m["content"]) for m in messages)


def locate_all(text, value):
    start = 0
    while value:
        at = text.find(value, start)
        if at < 0:
            return
        yield at
        start = at + len(value)


def complete_occurrences(text, value):
    for at in locate_all(text, value):
        tail = text[at + len(value):].lstrip()
        if not tail or tail.startswith(("[[/continuity]]", "- [Day ", "[[continuity ", "turn_id=",
                                       "HISTORICAL session=", "CURRENT ", "FOUNDATIONAL ", "Source index:")):
            yield at


class FrozenRequest:
    def __init__(self, assembly, name="capture"):
        self.name = name
        self.assembly = copy.deepcopy(assembly)
        self.messages = copy.deepcopy(assembly["messages"])
        if not self.messages or any(m.get("role") not in {"system", "user", "assistant"}
                                    or not isinstance(m.get("content"), str) for m in self.messages):
            raise ValueError("Expected complete text-only captured provider messages")
        if not self.messages[-1]["content"].startswith("LATEST PLAYER TURN "):
            raise ValueError("Capture must have the current complete latest-player message")
        self.blocks = []
        self.known = {}
        self.warnings = []
        envelope = assembly.get("promptEnvelope", {})
        proof = envelope.get("continuityPreflight", {})
        self.scope = {k: proof.get("memoryPrecisionEvidence", {}).get(k)
                      for k in ("campaignId", "observer", "timeline")}
        required = proof.get("continuityEvidence", {}).get("requiredContent", [])
        # Discovery must be confirmed by the captured assembly receipt. Text that
        # merely resembles an internal marker cannot make an editable block.
        receipts = {r.get("text", "").strip(): r for r in required if isinstance(r, dict)}
        self.unbound_expandable = 0
        for mi, message in enumerate(self.messages):
            if not message["content"].startswith("REFERENCE CONTEXT"):
                continue
            for match in BLOCK.finditer(message["content"]):
                meta = fields(match.group(1))
                kind = meta.get("kind", "")
                raw = match.group()
                receipt = receipts.get(raw.strip())
                if kind in EXPANDABLE and (not receipt or receipt.get("kind") != kind):
                    self.unbound_expandable += 1
                    self.warnings.append("Unbound archive block retained verbatim: " + meta.get("source", ""))
                    continue
                block = Block(mi, match.start(), match.end(), raw, kind,
                              meta.get("source", ""), match.group(2))
                self.blocks.append(block)
                # A source record is trusted for dedup only if the block itself
                # is receipt-bound or its complete source is independently bound.
                if receipt:
                    for source in sources_in(block.body):
                        self.known.setdefault(source.key, source)
        for record in proof.get("memoryPrecisionEvidence", {}).get("records", []):
            # Shadow diagnostics supply identity hints only. Physical occurrence
            # in the final captured messages is always required below.
            for row in record.get("sourceTurns", []):
                text = row.get("text", "")
                src = sources_in(text)
                if len(src) == 1 and src[0].id == row.get("id") and any(text in m["content"] for m in self.messages):
                    self.known.setdefault(src[0].key, src[0])
        self.candidates = [b for b in self.blocks if b.kind in EXPANDABLE]
        self.latest = self.messages[-1]["content"].partition("\n")[2]
        self.previous = []
        for m in self.messages[:-1]:
            if m["content"].startswith("HISTORICAL PLAYER TURN "):
                self.previous.append(m["content"].partition("\n")[2])

    def fingerprint(self):
        return digest(json.dumps(self.messages, ensure_ascii=False, sort_keys=True, separators=(",", ":")))

    def _query(self, query, previous):
        query = self.latest if query is None else query
        qterms = terms(query)
        follow = bool(FOLLOWUP.search(query) and len(qterms) <= 4 and ("?" in query or RECALL.search(query)))
        history = previous if previous is not None else self.previous[-2:]
        if follow and history:
            qterms |= terms(" ".join(history[-2:]))
        question = "?" in query or bool(re.match(r"\s*(?:and\s+)?(?:what|who|where|when|which|how)\b", query, re.I))
        recall = bool(RECALL.search(query))
        return query, qterms, recall, question, follow

    def select(self, query=None, previous=None, budget_chars=24000, max_exchanges=3):
        if not 1000 <= budget_chars <= 100000 or not 1 <= max_exchanges <= 12:
            raise ValueError("Invalid bounded experiment allowance")
        query, qterms, recall, question, follow = self._query(query, previous)
        documents = [terms(b.body) for b in self.candidates]
        freq = collections.Counter(term for ts in documents for term in ts)
        n = len(documents)
        ranked = []
        for index, (block, ts) in enumerate(zip(self.candidates, documents)):
            overlap = qterms & ts
            score = sum(math.log(1 + (n + .5) / (freq[t] + .5)) for t in overlap)
            ranked.append((score, len(overlap), index, overlap))
        ranked.sort(key=lambda row: (-row[0], -row[1], row[2]))
        # A named callback may be phrased as a statement. Require a rare content
        # term plus an explicit backward-reference cue, not arbitrary keywords.
        backward = bool(re.search(r"\b(?:that|again|still|another|latest|same|our)\b", query, re.I))
        rare_callback = backward and any(freq[t] and freq[t] <= max(1, n // 5) for t in qterms)
        expand = recall or question or rare_callback
        selected = set()
        reasons = []
        used = 0
        emitted = set()
        for score, overlap_count, index, overlap in ranked:
            block = self.candidates[index]
            if not expand or score <= 0:
                reasons.append({"index": index, "reason": "no_detail_need" if not expand else "no_match", "score": score})
                continue
            # Shared cost is computed against source views already selected; the
            # final prompt-wide pass resolves copies in other retained sections.
            views = sources_in(block.body)
            cost = len(block.text)
            for src in views:
                if src.key in emitted:
                    cost -= max(0, len(src.text) - len(reference(src)))
            if len(selected) >= max_exchanges or used + cost > budget_chars:
                reasons.append({"index": index, "reason": "shared_expansion_allowance", "score": score, "characters": cost})
                continue
            selected.add(index)
            emitted.update(s.key for s in views)
            used += cost
            reasons.append({"index": index, "reason": "detail_evidence", "score": score, "matchedTerms": sorted(overlap), "characters": cost})
        unresolved = bool(expand and not selected)
        return selected, {"policy": POLICY, "query": query, "needsOriginals": expand,
                          "explicitRecall": recall, "question": question, "contextualFollowup": follow,
                          "rareCallback": rare_callback, "budgetCharacters": budget_chars,
                          "maximumExchanges": max_exchanges, "selectedExpansionCharacters": used,
                          "unresolvedDetailNeed": unresolved, "decisions": reasons}

    def run(self, arm="dedup", query=None, previous=None, budget_chars=24000, max_exchanges=3):
        if arm not in {"baseline", "dedup", "selective", "guarded"}:
            raise ValueError("Unknown experiment arm")
        if (type(budget_chars) is not int or type(max_exchanges) is not int
                or not 1000 <= budget_chars <= 100000 or not 1 <= max_exchanges <= 12):
            raise ValueError("Invalid bounded experiment allowance")
        start = time.perf_counter()
        messages = copy.deepcopy(self.messages)
        selection = None
        excluded = []
        if arm in {"selective", "guarded"}:
            if arm == "guarded":
                current = self.latest if query is None else query
                simple = clearly_routine(current)
                selected = set() if simple else set(range(len(self.candidates)))
                selection = {"policy": "clear-routine-only-v1", "query": current, "needsOriginals": not simple,
                             "unresolvedDetailNeed": False, "clearlyRoutine": simple,
                             "fallback": None if simple else "baseline_recall_or_unclassified",
                             "budgetCharacters": budget_chars,
                             "selectedExpansionCharacters": sum(len(b.text) for b in self.candidates if not simple),
                             "budgetException": not simple,
                             "decisions": [{"index": i, "reason": "baseline_retention" if not simple else "clear_routine", "score": None}
                                           for i in range(len(self.candidates))]}
            else:
                selected, selection = self.select(query, previous, budget_chars, max_exchanges)
            # Uncertain discovery keeps the full baseline. This is observable and
            # counted as an exception, never hidden as a successful small packet.
            if selection["unresolvedDetailNeed"]:
                selection["fallback"] = "baseline_detail_need_unresolved"
            else:
                changes = collections.defaultdict(list)
                for index, block in enumerate(self.candidates):
                    if index not in selected:
                        changes[block.message].append((block.start, block.end, ""))
                        excluded.append({"kind": block.kind, "source": block.source, "characters": len(block.text), "sha256": digest(block.text)})
                apply_edits(messages, changes)
        before_dedup = message_chars(messages)
        before_dedup_messages = copy.deepcopy(messages)
        dedup_audit = {"removedCopies": 0, "savedCharacters": 0, "changes": [], "unresolvedOverlaps": []}
        if arm != "baseline":
            messages, dedup_audit = deduplicate(messages, list(self.known.values()))
        reconstructed = expand_references(messages, list(self.known.values()))
        if reconstructed != before_dedup_messages:
            raise AssertionError("Source references cannot reconstruct the selected prompt exactly")
        # Immutable message boundaries include schema, character, current input
        # and every role-separated chronological historical contribution.
        untouched = [i for i, m in enumerate(self.messages) if not m["content"].startswith("REFERENCE CONTEXT")]
        if any(messages[i] != self.messages[i] for i in untouched):
            raise AssertionError("Non-memory message changed")
        checks = {
            "nonMemoryMessagesIdentical": all(messages[i] == self.messages[i] for i in untouched),
            "latestContributionIdentical": messages[-1] == self.messages[-1],
            "rolesAndOrderIdentical": [m["role"] for m in messages] == [m["role"] for m in self.messages],
            "archiveInputUnchanged": self.messages == self.assembly["messages"],
            "dedupReconstructionExact": reconstructed == before_dedup_messages,
            "protectedRecordsReconstructExactly": all(b.text in reconstructed[b.message]["content"] for b in self.blocks
                                                        if b.kind not in EXPANDABLE),
            "protectedRecordsIdentical": all(b.text in messages[b.message]["content"] for b in self.blocks
                                               if b.kind not in EXPANDABLE and b.kind != "reported_exchange"),
        }
        # Exact-source content can be shared by protected records. Coverage is the
        # relevant invariant; byte equality of a duplicate wrapper is not.
        retained_views = []
        missing_views = []
        for source in self.known.values():
            was_present = any(source.text in m["content"] for m in self.messages)
            present = any(source.text in m["content"] for m in messages)
            if present:
                retained_views.append({"id": source.id, "role": source.role, "bodyHash": digest(source.body), "viewHash": digest(source.text)})
            elif was_present:
                missing_views.append({"id": source.id, "role": source.role, "bodyHash": digest(source.body), "viewHash": digest(source.text)})
        if arm == "dedup" and missing_views:
            raise AssertionError("Dedup lost unique complete source content")
        result = {"schema": SCHEMA, "policy": POLICY, "capture": self.name, "arm": arm,
                  "baselineHash": self.fingerprint(), "baselineCharacters": message_chars(self.messages),
                  "characters": message_chars(messages), "savedCharacters": message_chars(self.messages) - message_chars(messages),
                  "preDedupCharacters": before_dedup, "selection": selection, "excludedBlocks": excluded,
                  "deduplication": dedup_audit, "checks": checks, "retainedSourceViews": retained_views,
                  "omittedSourceViews": missing_views, "discoveryWarnings": self.warnings,
                  "elapsedMs": (time.perf_counter() - start) * 1000,
                  "messages": messages,
                  "limitations": ["Frozen captured-prompt projection, not production retrieval or privacy validation.",
                                  "Unwrapped foundational history and non-source-bound summary overlap remain unchanged.",
                                  "Only mapped exact source views have a deduplication guarantee.",
                                  "Character counts are measured; provider tokens, answer quality and voice are unmeasured.",
                                  "No current character foundation, recent window, original storage or production setting is changed."]}
        return result


def reference(source):
    return ("[Original source " + source.id + "; role=" + source.role + "; view="
            + digest(source.text)[:16] + " is included once elsewhere in this prompt.]")


def clearly_routine(query):
    """Narrow experimental fast path. Unknown turns retain the baseline pool.

    This is deliberately not claimed as a semantic classifier. Narration is
    retained in the prompt and inspected for recall cues before greeting checks.
    """
    if RECALL.search(query) or "?" in query:
        return False
    speech = re.sub(r"\*[^*]*\*", " ", query).strip()
    if len(speech.split()) > 35:
        return False
    greeting = re.fullmatch(r"(?:good morning|morning|hello|good night|goodbye|bye|be safe|thank you|thanks|sleep well)\b[^?]*", speech, re.I)
    handover = re.fullmatch(r"I wish to give you \d+\s+[\w '\-]+[.!]?\s*(?:Then there we have it[.!]*)?", speech, re.I)
    # Even a greeting may introduce a callback or unresolved historical issue.
    historical = re.search(r"\b(?:yesterday|previous|earlier|before|remember|recall|laughed|told|mentioned|promis\w*|agreed|that day|our night|last night|last time)\b", speech, re.I)
    return bool((greeting or handover) and not historical)


def expand_references(messages, sources):
    originals = {reference(s): s.text for s in sources}
    result = copy.deepcopy(messages)
    if originals:
        pattern = re.compile("|".join(re.escape(s) for s in sorted(originals, key=len, reverse=True)))
        for message in result:
            message["content"] = pattern.sub(lambda m: originals[m.group()], message["content"])
    return result


def apply_edits(messages, edits):
    for index, replacements in edits.items():
        ordered = sorted(replacements)
        if any(a[1] > b[0] for a, b in zip(ordered, ordered[1:])):
            raise ValueError("Overlapping replacement spans")
        for start, end, text in reversed(ordered):
            messages[index]["content"] = messages[index]["content"][:start] + text + messages[index]["content"][end:]


def deduplicate(messages, sources):
    messages = copy.deepcopy(messages)
    changes = []
    blocked = []
    before = message_chars(messages)
    # Longest first avoids replacing a source prefix that happens to be a subset
    # of a different view. Identity, role and the complete view must all match.
    for source in sorted(sources, key=lambda s: (-len(s.text), s.id, s.role)):
        hits = [(i, p) for i, m in enumerate(messages) for p in complete_occurrences(m["content"], source.text)]
        if len(hits) < 2:
            continue
        editable = [(i, p) for i, p in hits if messages[i]["content"].startswith("REFERENCE CONTEXT")]
        fixed = [(i, p) for i, p in hits if (i, p) not in editable]
        # Keep one full copy, preferentially an immutable chronological message.
        keep = fixed[0] if fixed else hits[0]
        edits = collections.defaultdict(list)
        for index, position in editable:
            if (index, position) != keep:
                edits[index].append((position, position + len(source.text), reference(source)))
                changes.append({"sourceId": source.id, "role": source.role, "bodyHash": digest(source.body),
                                "viewHash": digest(source.text), "message": index, "originalCharacters": len(source.text)})
        if len(fixed) > 1:
            blocked.append({"sourceId": source.id, "reason": "multiple_immutable_history_messages"})
        apply_edits(messages, edits)
        if not any(source.text in m["content"] for m in messages):
            raise AssertionError("Last complete source view removed")
    duplicates = []
    for source in sources:
        count = sum(len(list(complete_occurrences(m["content"], source.text))) for m in messages)
        if count > 1:
            duplicates.append({"sourceId": source.id, "bodyHash": digest(source.body), "viewHash": digest(source.text), "copies": count})
    return messages, {"removedCopies": len(changes), "savedCharacters": before - message_chars(messages),
                      "changes": changes, "remainingDuplicateViews": duplicates, "unresolvedOverlaps": blocked}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", action="append", required=True, help="Frozen assembly JSON; repeat for multiple captures")
    parser.add_argument("--output", required=True, help="Fresh private output directory")
    parser.add_argument("--budget-characters", type=int, default=24000)
    parser.add_argument("--max-exchanges", type=int, default=3)
    args = parser.parse_args()
    if len(args.capture) > 16:
        raise ValueError("At most 16 frozen captures per comparison")
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("Output already exists; retain previous evidence and use a fresh directory")
    captures = [(Path(p).resolve(), read_json(p)) for p in args.capture]
    if len({p.stem for p, _ in captures}) != len(captures):
        raise ValueError("Capture names must be distinct")
    output.mkdir(parents=True)
    summary = {"schema": SCHEMA, "classification": "development_projection_not_acceptance", "captures": []}
    for path, capture in captures:
        frozen = FrozenRequest(capture, path.stem)
        record = {"name": path.stem, "captureSha256": digest(path.read_bytes()), "scope": frozen.scope, "arms": []}
        for arm in ("baseline", "dedup", "selective", "guarded"):
            result = frozen.run(arm, budget_chars=args.budget_characters, max_exchanges=args.max_exchanges)
            write_json(output / (path.stem + "-" + arm + ".json"), result)
            compact = {k: v for k, v in result.items() if k not in {"messages", "retainedSourceViews", "omittedSourceViews", "excludedBlocks", "deduplication"}}
            compact["retainedSourceViews"] = len(result["retainedSourceViews"])
            compact["omittedSourceViews"] = len(result["omittedSourceViews"])
            compact["removedCopies"] = result["deduplication"]["removedCopies"]
            compact["remainingDuplicateViews"] = len(result["deduplication"].get("remainingDuplicateViews", []))
            record["arms"].append(compact)
        summary["captures"].append(record)
    write_json(output / "summary.json", summary)
    print(json.dumps({"output": str(output), "captures": len(captures), "providerCalls": 0}))


if __name__ == "__main__":
    main()
