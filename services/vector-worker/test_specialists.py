"""Provider-free boundary tests; no model download or campaign state required."""
import copy
import io
import re
import unittest
from types import SimpleNamespace
from specialists import MemoryExtractor, SCHEMA, text_hash, source_chunks, validate_extraction


class Tokenizer:
    def __call__(self, text, **kwargs):
        return {"offset_mapping": [(m.start(), m.end()) for m in re.finditer(r"\S+", text)]}


class Model:
    def __init__(self):
        self.processor = SimpleNamespace(tokenizer=Tokenizer())
        self.calls = 0
    def create_schema(self): return self
    def entities(self, schema): return self
    def classification(self, task, labels, **kwargs):
        assert isinstance(labels, list) and kwargs["multi_label"]
        return self
    def extract(self, text, schema, **kwargs):
        self.calls += 1
        entities = [{"text": m.group(), "start": m.start(), "end": m.end(), "confidence": .9}
                    for m in re.finditer("Ira|Michael|Maxax", text)]
        return {"entities": {"person": entities}, "topics": [{"label": "botany", "confidence": .9}]}


class SpecialistContracts(unittest.TestCase):
    def request(self, text, source="turn"):
        return {"text": text, "sourceId": source, "textHash": text_hash(text)}
    def test_span_identity_and_utf16(self):
        text = "🌿 Ira asked Michael."
        result = validate_extraction(text, {"entities": {"person": [
            {"text": "Ira", "start": 2, "end": 5, "confidence": .9, "actorId": "Michael"},
            {"text": "Ira", "start": 0, "end": 3, "confidence": .99}]}})
        self.assertEqual(result["entities"][0]["start"], 3)
        self.assertNotIn("actorId", result["entities"][0])
        self.assertEqual(result["rejectedSpans"], 1)
    def test_malformed_output(self):
        for raw in [None, [], {"entities": []}, {"classifications": []}, {"entities": {"person": [None, "Ira"]}}]:
            self.assertEqual(validate_extraction("Ira", raw)["entities"], [])
    def test_invalid_confidence_and_types(self):
        for score in [True, float("nan"), float("inf"), -.1, 1.1, .49]:
            self.assertEqual(validate_extraction("Ira", {"entities": {"person": [{"text":"Ira", "start":0,"end":3,"confidence":score}]}})["entities"], [])
        self.assertEqual(validate_extraction("Ira", {"entities":{"world_action": []}})["entities"], [])
    def test_input_rejected_without_loading(self):
        def unexpected(): raise AssertionError("should not load")
        extractor=MemoryExtractor(loader=unexpected)
        for raw in [None, [], {}, self.request(" "), self.request("x"*12001), {**self.request("Ira"),"textHash":"wrong"}]:
            self.assertFalse(extractor.extract(raw)["ok"])
    def test_unconfigured_and_busy_are_not_success(self):
        extractor=MemoryExtractor()
        extractor.model_dir=""
        self.assertEqual(extractor.extract(self.request("Ira"))["status"], "not_configured")
        extractor.loader=Model
        extractor.lock.acquire()
        try: self.assertEqual(extractor.extract(self.request("Ira"))["status"], "busy")
        finally: extractor.lock.release()
    def test_cache_rebinds_source_and_does_not_share_mutable_output(self):
        extractor=MemoryExtractor(loader=Model)
        first=extractor.extract(self.request("Ira asked Michael.","one"))
        first["entities"].clear()
        second=extractor.extract(self.request("Ira asked Michael.","two"))
        self.assertTrue(second["ok"] and second["cacheHit"])
        self.assertEqual(second["sourceId"], "two")
        self.assertEqual(len(second["entities"]),2)
        self.assertEqual(extractor.model.calls,1)
        self.assertEqual(second["remoteCalls"],0)
    def test_whole_long_source_and_late_name(self):
        text="🌿 " + "ordinary words "*300 + "Maxax"
        chunks=source_chunks(text,Tokenizer())
        covered=set()
        for lo,hi in chunks: covered.update(range(lo,hi))
        self.assertEqual(len(covered),len(text))
        result=MemoryExtractor(loader=Model).extract(self.request(text))
        self.assertTrue(result["ok"])
        self.assertEqual([e["text"] for e in result["entities"]], ["Maxax"])
        self.assertEqual(result["entities"][0]["end"], len(text)+1)
        self.assertGreater(result["chunkCount"],1)
    def test_failure_remains_unavailable(self):
        def broken(): raise RuntimeError("failed fixture")
        extractor=MemoryExtractor(loader=broken)
        result=extractor.extract(self.request("Ira"))
        self.assertFalse(result["ok"])
        self.assertEqual(result["status"],"unavailable")
        self.assertEqual(extractor.status()["lastStatus"],"unavailable")


def run_contracts():
    stream=io.StringIO()
    result=unittest.TextTestRunner(stream=stream).run(unittest.defaultTestLoader.loadTestsFromTestCase(SpecialistContracts))
    if not result.wasSuccessful(): raise ValueError(stream.getvalue())
    return {"schema": SCHEMA, "passed": result.testsRun, "networkCalls":0}


if __name__ == "__main__": unittest.main()
