"""Provider-free memory index contracts; never starts the worker or a listener."""
import importlib.util
import json
import hashlib
import os
import re
import sys
import types
import unittest
from contextlib import closing
from pathlib import Path

# These contracts exercise source windows and eligibility, not embedding math.
# A minimal import placeholder permits execution without installing ML runtimes.
if importlib.util.find_spec("numpy") is None:
    sys.modules["numpy"] = types.ModuleType("numpy")
import worker


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

    def test_rerank_gate_requires_all_accuracy_and_latency_evidence(self):
        certificate={"schema":"reign-memory-rerank-certificate-v1","modelSha256":"model","tokenizerSha256":"tokenizer","heldOutCorpusSha256":"a"*64,"heldOut":True,"caseCount":100,"criticalErrors":0,"requiredFactCoverage":.99,"quality":.99,"baselineQuality":.97,"rerankP95Ms":110,"baselineP95Ms":100}
        self.assertTrue(worker.rerank_certificate_valid(certificate,"model","tokenizer"))
        for field,value in [("criticalErrors",1),("requiredFactCoverage",.97),("quality",.96),("rerankP95Ms",121),("heldOut",False),("caseCount",10),("modelSha256","stale"),("tokenizerSha256","other")]:
            with self.subTest(field=field):
                self.assertFalse(worker.rerank_certificate_valid(dict(certificate,**{field:value}),"model","tokenizer"))


class RuntimePrecisionContracts(unittest.TestCase):
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


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(PrecisionContracts)
    if "--runtime-contracts" in sys.argv:
        suite.addTests(unittest.defaultTestLoader.loadTestsFromTestCase(RuntimePrecisionContracts))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print(json.dumps({"schema":"reign-memory-worker-contracts-v1","ok":result.wasSuccessful(),"tests":result.testsRun,"providerCalls":0,"embeddingInference":False}))
    sys.exit(0 if result.wasSuccessful() else 1)
