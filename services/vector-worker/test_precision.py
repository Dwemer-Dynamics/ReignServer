"""Provider-free memory index contracts; never starts the worker or a listener."""
import importlib.util
import json
import hashlib
import os
import re
import sys
import types
import unittest
import tempfile
import time
from collections import OrderedDict
from contextlib import closing
from pathlib import Path

# These contracts exercise source windows and eligibility, not embedding math.
# A minimal import placeholder permits execution without installing ML runtimes.
if importlib.util.find_spec("numpy") is None:
    sys.modules["numpy"] = types.ModuleType("numpy")
import worker
from test_specialists import SpecialistContracts
from test_late_interaction import LateInteractionContracts
from test_selection import SelectionContracts
from test_claim_validation import ClaimValidationContracts


class OffsetTokenizer:
    def encode(self, text, add_special_tokens=False):
        offsets = [(m.start(), m.end()) for m in re.finditer(r"\S+", text)]
        return types.SimpleNamespace(offsets=offsets, ids=list(range(len(offsets) + (2 if add_special_tokens else 0))))


class PrecisionContracts(unittest.TestCase):
    def test_late_correction_and_all_source_spans_survive(self):
        text = " ".join("word" + str(i) for i in range(4000)) + " Corrected price: 300 only after arrival. Not paid."
        document = {"id": "parent", "text": text, "payload": {"ownerId": "npc_b", "sourceType": "summary", "timelineId": "a"}}
        chunks = worker.contextual_chunks(document, OffsetTokenizer())
        self.assertGreater(len(chunks), 8)
        self.assertTrue(any("Not paid." in c["text"] for c in chunks))
        self.assertEqual(chunks[0]["payload"]["sourceStart"], 0)
        self.assertEqual(chunks[-1]["payload"]["sourceEnd"], len(text))
        self.assertTrue(all(a["payload"]["sourceEnd"] >= b["payload"]["sourceStart"] for a,b in zip(chunks,chunks[1:])))
        self.assertTrue(all(len(OffsetTokenizer().encode(c["text"],True).ids)<=512 for c in chunks))
        self.assertEqual(chunks, worker.contextual_chunks(document,OffsetTokenizer()))

    def test_hidden_and_private_mental_override_shared_audience(self):
        base = {"eligibilityVersion":2,"ownerId":"npc_a","knownBy":["npc_b"],"privateMental":False,"hiddenFrom":[],"timelineId":"a","restoreGeneration":"new","worldDay":20}
        filters = {"observerId":"npc_b","timelineId":"a","restoreGeneration":"new","asOfWorldDay":30}
        self.assertTrue(worker.payload_matches_filters(base,filters))
        for field,value in [("privateMental",True),("hiddenFrom",["npc_b"]),("timelineId","b"),("restoreGeneration","old"),("worldDay",31),("knownBy",["npc_b_child"]),("eligibilityVersion",1)]:
            with self.subTest(field=field):
                self.assertFalse(worker.payload_matches_filters(dict(base,**{field:value}),filters))
        self.assertFalse(worker.payload_matches_filters(dict(base,ownerId="npc_b",hiddenFrom=["npc_b"]),filters))

    def test_thousands_of_chunks_cannot_displace_other_documents(self):
        hits = [{"id":str(i),"score":1-i*.00001,"payload":{"documentId":"one"}} for i in range(2000)]
        hits.append({"id":"late","score":.2,"payload":{"documentId":"answer"}})
        self.assertEqual([h["payload"]["documentId"] for h in worker.unique_document_hits(hits,2)],["one","answer"])

    def test_restored_snapshot_is_an_alternate_timeline_not_an_alternate_observer(self):
        filters = {"observerId": "npc_b", "timelineId": "branch", "restoreGeneration": "new",
                   "restoredSavePointId": "exact_save", "asOfWorldDay": 30}
        source = {"eligibilityVersion": 2, "ownerId": "npc_a", "knownBy": ["npc_b"], "privateMental": False,
                  "hiddenFrom": [], "timelineId": "parent", "restoreGeneration": "new", "worldDay": 20,
                  "restoredSavePointId": "exact_save"}
        self.assertTrue(worker.payload_matches_filters(source, filters))
        for field, value in [("restoredSavePointId", "other"), ("restoreGeneration", "old"), ("privateMental", True),
                             ("knownBy", ["npc_b_child"]), ("hiddenFrom", ["npc_b"]), ("worldDay", 31)]:
            with self.subTest(field=field):
                self.assertFalse(worker.payload_matches_filters(dict(source, **{field: value}), filters))
        self.assertTrue(worker.payload_matches_filters(dict(source, timelineId="branch", restoredSavePointId=""), filters))
        self.assertFalse(worker.payload_matches_filters(source, {k: v for k, v in filters.items() if k != "restoreGeneration"}))

    def test_rerank_gate_requires_all_accuracy_and_latency_evidence(self):
        certificate={"schema":"reign-memory-rerank-certificate-v1","modelSha256":"model","tokenizerSha256":"tokenizer","heldOutCorpusSha256":"a"*64,"heldOut":True,"caseCount":100,"criticalErrors":0,"requiredFactCoverage":.99,"quality":.99,"baselineQuality":.97,"rerankP95Ms":110,"baselineP95Ms":100}
        self.assertTrue(worker.rerank_certificate_valid(certificate,"model","tokenizer"))
        for field,value in [("criticalErrors",1),("requiredFactCoverage",.97),("quality",.96),("rerankP95Ms",121),("heldOut",False),("caseCount",10),("modelSha256","stale"),("tokenizerSha256","other")]:
            with self.subTest(field=field):
                self.assertFalse(worker.rerank_certificate_valid(dict(certificate,**{field:value}),"model","tokenizer"))


class RuntimePrecisionContracts(unittest.TestCase):
    def test_compact_archive_reload_matches_native_and_preserves_all_sources(self):
        from compact_store import CompactQdrantLocal
        from qdrant_client import QdrantClient, models
        np = worker.np
        random = np.random.default_rng(62102)
        base = {"eligibilityVersion": 2, "ownerId": "npc_a", "knownBy": ["npc_b"],
                "privateMental": False, "hiddenFrom": [], "timelineId": "a",
                "restoreGeneration": "new", "worldDay": 20, "status": "active"}
        filters = {"observerId": "npc_b", "timelineId": "a", "restoreGeneration": "new", "asOfWorldDay": 30}
        vectors = random.normal(size=(600,384)).astype(np.float32)
        payloads = [dict(base, documentId=str(i), text="Original answer café 城堡 " + str(i),
                         hiddenFrom=["npc_b"] if i % 13 == 0 else []) for i in range(600)]
        with tempfile.TemporaryDirectory(prefix="reign-compact-") as directory:
            with closing(QdrantClient(path=directory)) as native:
                native.create_collection("fixture", vectors_config=models.VectorParams(size=384, distance=models.Distance.COSINE))
                native.upsert("fixture", points=[models.PointStruct(id=i, vector=v.tolist(), payload=p)
                                                for i,(v,p) in enumerate(zip(vectors,payloads))])
            # Native upsert normalizes before assignment but persists the
            # original vector. Compare cold loaders to the same saved state.
            with closing(QdrantClient(path=directory)) as native:
                state = worker.WorkerState(Path(directory)/"unused")
                baseline = state.load_local_index(native,"fixture")
                expected, _ = state.search_local_index(baseline,vectors[-1].tolist(),filters)
            store = Path(directory)/"collection"/"fixture"/"storage.sqlite"
            original_hash = hashlib.sha256(store.read_bytes()).hexdigest()
            with closing(CompactQdrantLocal(directory)) as compact:
                state = worker.WorkerState(Path(directory)/"unused")
                index = state.load_local_index(compact,"fixture")
                actual, _ = state.search_local_index(index,vectors[-1].tolist(),filters)
                self.assertEqual([r["id"] for r in actual],[r["id"] for r in expected])
                np.testing.assert_allclose([r["score"] for r in actual],[r["score"] for r in expected],atol=2e-7)
                self.assertEqual([r["payload"] for r in actual],[r["payload"] for r in expected])
                for ident in index["ids"]:
                    np.testing.assert_array_equal(index["matrix"][index["positions"][ident]],
                                                  baseline["matrix"][baseline["positions"][ident]])
                self.assertEqual(len(index["points"]),600)
                self.assertTrue(index["compact"])
                self.assertEqual(index["matrix"].dtype,np.float32)
                self.assertTrue(all(v is None for v,_ in index["points"].values()))
                archive = compact._get_collection("fixture")
                self.assertEqual(archive.vectors[""].dtype,np.float32)
                self.assertIs(index["points"]["1"][1],archive.payload[archive.ids[1]])
            self.assertEqual(hashlib.sha256(store.read_bytes()).hexdigest(),original_hash)

    def test_compact_archive_mutations_reload_and_native_rollback_agree(self):
        from compact_store import CompactQdrantLocal
        from qdrant_client import QdrantClient, models
        np = worker.np
        state, fixture, filters, query = self.local_fixture(12)
        with tempfile.TemporaryDirectory(prefix="reign-compact-mutation-") as directory:
            with closing(QdrantClient(path=directory)) as native:
                native.create_collection("fixture",vectors_config=models.VectorParams(size=384,distance=models.Distance.COSINE))
                native.upsert("fixture",points=[models.PointStruct(id=int(i),vector=v.tolist(),payload=p)
                                               for i,(v,p) in fixture["points"].items()])
            with closing(CompactQdrantLocal(directory)) as compact:
                # The original helper installs a noncompact fixture in its
                # cache. Use a fresh state to exercise the real archive loader.
                state = worker.WorkerState(Path(directory)/"reloaded-state")
                index = state.load_local_index(compact,"fixture")
                state.remove_documents(compact,"fixture",[index["points"]["3"][1]["documentId"]],True)
                payload = dict(index["points"]["4"][1],hiddenFrom=["npc_b"])
                docs = [{"id":str(i),"payload":dict(payload,hiddenFrom=[] if i>12 else ["npc_b"],documentId=str(i))}
                        for i in [4]+list(range(13,650))]
                vectors = np.random.default_rng(73102).normal(size=(len(docs),384)).tolist()
                compact.upsert("fixture",points=[models.PointStruct(id=int(d["id"]),vector=v,payload=d["payload"]) for d,v in zip(docs,vectors)])
                state.update_local_index("fixture",docs,vectors)
                expected,_ = state.search_local_index(index,query,filters)
                self.assertNotIn("3",index["points"])
                self.assertNotIn("4",[r["id"] for r in expected])
                self.assertTrue(all(v is None for v,_ in index["points"].values()))
                self.assertEqual(compact._get_collection("fixture").vectors[""].dtype,np.float32)
                self.assertEqual(compact._get_collection("fixture").ids[649],len(compact._get_collection("fixture").payload)-1)
            for factory in (CompactQdrantLocal,lambda p:QdrantClient(path=p)):
                with closing(factory(directory)) as reloaded:
                    fresh_state = worker.WorkerState(Path(directory)/"unused")
                    index = fresh_state.load_local_index(reloaded,"fixture")
                    actual,_ = fresh_state.search_local_index(index,query,filters)
                    self.assertEqual([r["id"] for r in actual],[r["id"] for r in expected])
                    np.testing.assert_allclose([r["score"] for r in actual],[r["score"] for r in expected],atol=2e-7)
                    self.assertEqual([r["payload"] for r in actual],[r["payload"] for r in expected])

    def test_compact_archive_preserves_native_exclusive_lock(self):
        from compact_store import CompactQdrantLocal
        from qdrant_client import QdrantClient
        with tempfile.TemporaryDirectory(prefix="reign-compact-lock-") as directory:
            with closing(QdrantClient(path=directory)) as native:
                lock = Path(directory)/".lock"
                identity = (lock.stat().st_ino,lock.read_bytes())
                with self.assertRaises(RuntimeError):
                    CompactQdrantLocal(directory)
                self.assertEqual((lock.stat().st_ino,lock.read_bytes()),identity)
            with closing(CompactQdrantLocal(directory)) as compact:
                with self.assertRaises(RuntimeError):
                    CompactQdrantLocal(directory)
                self.assertEqual((lock.stat().st_ino,lock.read_bytes()),identity)
            with closing(QdrantClient(path=directory)):
                pass

    def test_compact_document_map_removes_all_chunks_and_respects_changed_parents(self):
        from compact_store import CompactQdrantLocal
        from qdrant_client import models
        with tempfile.TemporaryDirectory(prefix="reign-compact-children-") as directory:
            with closing(CompactQdrantLocal(directory)) as compact:
                compact.create_collection("fixture",vectors_config=models.VectorParams(size=384,distance=models.Distance.COSINE))
                compact.upsert("fixture",points=[models.PointStruct(id=i,vector=[1.0]+[0.0]*383,
                    payload={"documentId":"old" if i<3 else "keep"}) for i in range(5)])
                state=worker.WorkerState(Path(directory)/"unused")
                index=state.load_local_index(compact,"fixture")
                compact.upsert("fixture",points=[models.PointStruct(id=1,vector=[1.0]+[0.0]*383,payload={"documentId":"new"})])
                state.update_local_index("fixture",[{"id":"1","payload":{"documentId":"new"}}],[[1.0]+[0.0]*383])
                state.remove_documents(compact,"fixture",["old","old","missing"],True)
                self.assertEqual(set(index["points"]),{"1","3","4"})
                self.assertEqual(index["documents"],{"new":{"1"},"keep":{"3","4"}})
                state.remove_documents(compact,"fixture",["1"],True)
                self.assertNotIn("new",index["documents"])
            with closing(CompactQdrantLocal(directory)) as reloaded:
                self.assertEqual({i for i,_,_ in reloaded.reference_records("fixture")},{"3","4"})

    def test_compact_adapter_rejects_untested_dependency_version(self):
        from unittest.mock import patch
        from compact_store import CompactQdrantLocal
        with tempfile.TemporaryDirectory(prefix="reign-compact-version-") as directory:
            with patch("compact_store.importlib.metadata.version",return_value="future-untested"):
                with self.assertRaisesRegex(RuntimeError,"pinned"):
                    CompactQdrantLocal(directory)
            self.assertEqual(list(Path(directory).iterdir()),[])

    def test_compact_legacy_duplicate_rows_preserve_native_resolution_and_storage(self):
        import pickle,sqlite3
        from compact_store import CompactQdrantLocal
        from qdrant_client import QdrantClient,models
        with tempfile.TemporaryDirectory(prefix="reign-compact-legacy-") as directory:
            with closing(QdrantClient(path=directory)) as native:
                native.create_collection("fixture",vectors_config=models.VectorParams(size=384,distance=models.Distance.COSINE))
                native.upsert("fixture",points=[models.PointStruct(id=1,vector=[1.0]+[0.0]*383,payload={"version":"first"})])
            path=Path(directory)/"collection"/"fixture"/"storage.sqlite"
            with sqlite3.connect(path) as connection:
                legacy=models.PointStruct(id=1,vector=[0.0,1.0]+[0.0]*382,payload={"version":"last"})
                connection.execute("INSERT INTO points VALUES (?,?)",("legacy-alternate-key",pickle.dumps(legacy)))
            original=hashlib.sha256(path.read_bytes()).hexdigest()
            outputs=[]
            for factory in (lambda p:QdrantClient(path=p),CompactQdrantLocal):
                with closing(factory(directory)) as client:
                    state=worker.WorkerState(Path(directory)/"unused")
                    index=state.load_local_index(client,"fixture")
                    outputs.append(state.search_local_index(index,[0.0,1.0]+[0.0]*382,{})[0])
                    if isinstance(client,CompactQdrantLocal):
                        self.assertEqual(client.archive_status()["fixture"]["duplicateStorageRowsAtLoad"],1)
                        self.assertEqual(client.archive_status()["fixture"]["loadedStorageRows"],2)
            self.assertEqual(outputs[0],outputs[1])
            self.assertEqual(len(outputs[1]),1)
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(),original)
            with closing(CompactQdrantLocal(directory)) as client:
                archive=client._get_collection("fixture")
                original_matrix=archive.vectors[""]
                client.upsert("fixture",points=[models.PointStruct(id=2,vector=[1.0]+[0.0]*383,payload={"version":"new"})])
                self.assertIs(archive.vectors[""],original_matrix)
                self.assertNotEqual(archive.ids[1],archive.ids[2])
                self.assertEqual(archive.payload[archive.ids[1]]["version"],"last")
                self.assertEqual(archive.payload[archive.ids[2]]["version"],"new")
            with closing(QdrantClient(path=directory)) as native:
                found={p.id:p.payload for p in native.retrieve("fixture",ids=[1,2])}
                self.assertEqual(found,{1:{"version":"last"},2:{"version":"new"}})

    def local_fixture(self, count=32):
        np = worker.np
        random = np.random.default_rng(41029)
        state = worker.WorkerState(Path("unused-worker-contract-data"))
        base = {"eligibilityVersion": 2, "ownerId": "npc_a", "knownBy": ["npc_b"],
                "privateMental": False, "hiddenFrom": [], "timelineId": "a",
                "restoreGeneration": "new", "worldDay": 20, "status": "active"}
        points = OrderedDict((str(i), (random.normal(size=384).astype(np.float32),
                              dict(base, documentId=str(worker.uuid.UUID(int=i+1000))))) for i in range(count))
        index = {"points": points, "ids": [], "payloads": [], "matrix": None}
        state.local_indexes["fixture"] = index
        filters = {"observerId": "npc_b", "timelineId": "a", "restoreGeneration": "new", "asOfWorldDay": 30}
        return state, index, filters, random.normal(size=384).astype(np.float32).tolist()

    def assert_matches_full_rebuild(self, state, index, filters, query):
        fresh = {"points": OrderedDict(index["points"]), "matrix": None}
        actual, _ = state.search_local_index(index, query, filters)
        expected, _ = state.search_local_index(fresh, query, filters)
        self.assertEqual([r["id"] for r in actual], [r["id"] for r in expected])
        worker.np.testing.assert_allclose([r["score"] for r in actual], [r["score"] for r in expected], atol=1e-7)
        self.assertEqual([r["payload"] for r in actual], [r["payload"] for r in expected])

    def test_incremental_index_matches_full_rebuild_through_replacement_and_growth(self):
        state, index, filters, query = self.local_fixture()
        state.search_local_index(index, query, filters)
        initial_matrix = index["matrix"]
        # Remove twice, replace a different existing row, and reuse the vacant
        # slot. Repeated deletion must not create duplicate reusable positions.
        state.delete_local_index("fixture", ["3", "3", "missing"], False)
        payload = dict(index["points"]["4"][1])
        state.update_local_index("fixture", [{"id": "4", "payload": payload}, {"id": "new", "payload": payload}],
                                 [query, [0.0]*384])
        self.assertIs(initial_matrix, index["matrix"])
        self.assert_matches_full_rebuild(state, index, filters, query)
        # Grow beyond the spare allocation; unchanged vectors must not be
        # renormalized and deleted slots must remain absent from the result.
        random = worker.np.random.default_rng(50930)
        for offset in range(0, 800, 100):
            documents = [{"id": "grown_"+str(i), "payload": dict(payload, documentId="grown_"+str(i))}
                         for i in range(offset, offset+100)]
            state.update_local_index("fixture", documents, random.normal(size=(100, 384)).tolist())
        self.assert_matches_full_rebuild(state, index, filters, query)
        self.assertEqual(index["matrix_rebuild_count"], 1)
        self.assertEqual(len(set(index["positions"].values())), len(index["points"]))
        self.assertNotIn("3", index["positions"])
        # Payload-only visibility changes apply on the same row, immediately.
        state.update_local_index("fixture", [{"id": "4", "payload": dict(payload, hiddenFrom=["npc_b"])}], [query])
        results, _ = state.search_local_index(index, query, filters)
        self.assertNotIn("4", [r["id"] for r in results])
        self.assert_matches_full_rebuild(state, index, filters, query)
        state.delete_local_index("fixture", list(index["points"]), False)
        self.assertEqual(state.search_local_index(index, query, filters)[0], [])
        state.delete_local_index("fixture", [], True)
        self.assertNotIn("fixture", state.local_indexes)

    def test_persisted_reload_agrees_with_incremental_search(self):
        from qdrant_client import QdrantClient, models
        state, index, filters, query = self.local_fixture(12)
        with tempfile.TemporaryDirectory(prefix="reign-worker-index-") as directory:
            # Qdrant's run-owned on-disk store is the persistence authority.
            with closing(QdrantClient(path=directory)) as client:
                client.create_collection("fixture", vectors_config=models.VectorParams(size=384, distance=models.Distance.COSINE))
                client.upsert("fixture", points=[models.PointStruct(id=int(i), vector=v.tolist(), payload=p)
                                                for i,(v,p) in index["points"].items()])
                state.search_local_index(index, query, filters)
                state.remove_documents(client, "fixture", [index["points"]["3"][1]["documentId"]], True)
                # This is the real shared delete path, including contextual children.
                self.assertNotIn("3", index["points"])
                expected, _ = state.search_local_index(index, query, filters)
            with closing(QdrantClient(path=directory)) as client:
                reloaded = worker.WorkerState(Path(directory)/"unused")
                fresh = reloaded.load_local_index(client, "fixture")
                actual, _ = reloaded.search_local_index(fresh, query, filters)
                self.assertEqual([r["id"] for r in actual], [r["id"] for r in expected])
                worker.np.testing.assert_allclose([r["score"] for r in actual], [r["score"] for r in expected], atol=1e-7)

    def test_incremental_update_work_and_timing_on_large_synthetic_archive(self):
        state, index, filters, query = self.local_fixture(20000)
        state.search_local_index(index, query, filters)
        timings = []
        for repeat in range(5):
            point_id = str(repeat)
            payload = dict(index["points"][point_id][1])
            started = time.perf_counter()
            state.delete_local_index("fixture", [point_id], False)
            state.update_local_index("fixture", [{"id": point_id, "payload": payload}], [query])
            update_ms = (time.perf_counter()-started)*1000
            actual, measurements = state.search_local_index(index, query, filters)
            fresh = {"points": OrderedDict(index["points"]), "matrix": None}
            started = time.perf_counter()
            state.rebuild_local_matrix(fresh)
            rebuild_ms = (time.perf_counter()-started)*1000
            expected, _ = state.search_local_index(fresh, query, filters)
            # Replaced vectors tie with each other; compare identity/score maps
            # rather than treating arbitrary equal-score ordering as accuracy.
            a = {r["id"]:r["score"] for r in actual}; b = {r["id"]:r["score"] for r in expected}
            self.assertEqual(a.keys(), b.keys())
            worker.np.testing.assert_allclose([a[k] for k in sorted(a)], [b[k] for k in sorted(a)], atol=1e-7)
            self.assertEqual(index["matrix_rebuild_count"], 1)
            timings.append(dict(measurements, incrementalUpdateMs=update_ms, fullRebuildMs=rebuild_ms))
        print(json.dumps({"schema":"reign-worker-index-benchmark-v1", "synthetic":True,
                          "sourcePoints":20000, "dimensions":384, "providerCalls":0,
                          "embeddingInference":False, "rankingAndEligibilityEquivalent":True,
                          "samples":timings}))

    def test_qdrant_groups_before_limit_under_chunk_saturation(self):
        from qdrant_client import QdrantClient, models
        with closing(QdrantClient(":memory:")) as client:
            client.create_collection("groups",vectors_config=models.VectorParams(size=384,distance=models.Distance.COSINE))
            points=[models.PointStruct(id=i,vector=[1.0]+[0.0]*383,payload={"documentId":"long"}) for i in range(2000)]
            points.append(models.PointStruct(id=2000,vector=[.8,.2]+[0.0]*382,payload={"documentId":"answer"}))
            client.upsert("groups",points=points)
            response=client.query_points_groups("groups",query=[1.0]+[0.0]*383,group_by="documentId",group_size=1,limit=2)
            self.assertEqual({g.id for g in response.groups},{"long","answer"})

    def test_actual_bge_tokenizer_preserves_late_unicode_conditions(self):
        from tokenizers import Tokenizer
        path=Path(os.environ["REIGN_PRECISION_TEST_TOKENIZER"])
        tokenizer=Tokenizer.from_file(str(path)); tokenizer.no_truncation(); tokenizer.no_padding()
        text=("horse-riding 城堡 café — word123 "*800)+" Corrected payment is 300 denars only after delivery. Not paid."
        chunks=worker.contextual_chunks({"id":"runtime_parent","text":text,"payload":{"sourceType":"memory","ownerId":"npc_b","timelineId":"a","worldDay":20}},tokenizer)
        self.assertTrue(all(len(tokenizer.encode(c["text"]).ids)<=512 for c in chunks))
        self.assertEqual(chunks[-1]["payload"]["sourceEnd"],len(text))
        self.assertTrue(any("Not paid." in c["text"] for c in chunks))
        print(json.dumps({"tokenizerSha256":hashlib.sha256(path.read_bytes()).hexdigest(),"windows":len(chunks),"actualModelWindow":512}))

    def test_qdrant_filter_matches_local_filter_before_limit(self):
        from qdrant_client import QdrantClient, models
        base={"eligibilityVersion":2,"ownerId":"npc_a","knownBy":["npc_b"],"privateMental":False,"hiddenFrom":[],"timelineId":"a","restoreGeneration":"new","worldDay":20}
        filters={"observerId":"npc_b","timelineId":"a","restoreGeneration":"new","asOfWorldDay":30}
        payloads=[base]+[dict(base,**{k:v}) for k,v in [("privateMental",True),("hiddenFrom",["npc_b"]),("timelineId","b"),("restoreGeneration","old"),("worldDay",31),("knownBy",["npc_b_child"]),("eligibilityVersion",1)]]
        with closing(QdrantClient(":memory:")) as client:
            client.create_collection("fixture",vectors_config=models.VectorParams(size=384,distance=models.Distance.COSINE))
            client.upsert("fixture",points=[models.PointStruct(id=i,vector=[1.0]+[0.0]*383,payload=p) for i,p in enumerate(payloads)])
            response=client.query_points("fixture",query=[1.0]+[0.0]*383,query_filter=worker.build_filter(filters),limit=1)
            self.assertEqual({p.id for p in response.points},{i for i,p in enumerate(payloads) if worker.payload_matches_filters(p,filters)})

    def test_qdrant_restored_timeline_matches_local_eligibility(self):
        from qdrant_client import QdrantClient, models
        source = {"eligibilityVersion": 2, "ownerId": "npc_a", "knownBy": ["npc_b"], "privateMental": False,
                  "hiddenFrom": [], "timelineId": "parent", "restoreGeneration": "new", "worldDay": 20,
                  "restoredSavePointId": "saved"}
        filters = {"observerId": "npc_b", "timelineId": "branch", "restoreGeneration": "new",
                   "restoredSavePointId": "saved", "asOfWorldDay": 30}
        payloads = [source, dict(source, timelineId="branch", restoredSavePointId="")]
        payloads += [dict(source, **{k: v}) for k, v in [("privateMental", True), ("hiddenFrom", ["npc_b"]),
                     ("restoreGeneration", "old"), ("restoredSavePointId", "other"), ("worldDay", 31), ("knownBy", [])]]
        with closing(QdrantClient(":memory:")) as client:
            client.create_collection("snapshot", vectors_config=models.VectorParams(size=384, distance=models.Distance.COSINE))
            client.upsert("snapshot", points=[models.PointStruct(id=i, vector=[1.0] + [0.0]*383, payload=p) for i, p in enumerate(payloads)])
            hits = client.query_points("snapshot", query=[1.0] + [0.0]*383, query_filter=worker.build_filter(filters), limit=20)
            self.assertEqual({p.id for p in hits.points}, {i for i, p in enumerate(payloads) if worker.payload_matches_filters(p, filters)})
            self.assertEqual({p.id for p in hits.points}, {0, 1})


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(PrecisionContracts)
    suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(SpecialistContracts))
    suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(SelectionContracts))
    suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(ClaimValidationContracts))
    if "--runtime-contracts" in sys.argv:
        suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimePrecisionContracts))
        suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(LateInteractionContracts))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print(json.dumps({"schema":"reign-memory-worker-contracts-v1","ok":result.wasSuccessful(),"tests":result.testsRun,"providerCalls":0,"embeddingInference":False}))
    sys.exit(0 if result.wasSuccessful() else 1)
