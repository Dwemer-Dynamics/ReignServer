"""No inference/downloads: production selection boundaries with injected scorers."""
import copy
import math
import tempfile
import time
import unittest
from selection import MemorySelection, SCHEMA, RANKING_POLICY, text_hash, select_segments, validate_segments, source_windows


class FakeModels:
    def __init__(self): self.calls = 0; self.score = .8; self.delay = 0
    def rank(self, query, text, deadline, backend="cpu"):
        self.calls += 1
        if self.delay: time.sleep(self.delay)
        return self.score
    def compress(self, item, deadline):
        self.calls += 1
        return select_segments(item['segments'], [.1] * len(item['segments']), item.get('rate', .65))


class SelectionContracts(unittest.TestCase):
    def setUp(self):
        self.models = FakeModels()
        self.helper = MemorySelection('unused-local-fixture', self.models)
        self.item = {'id': 'source', 'text': 'No payment before arrival.', 'textHash': text_hash('No payment before arrival.')}
        self.request = {'schema': SCHEMA, 'operation': 'rank', 'query': 'What was agreed?', 'timeoutMs': 500,
                        'candidates': [self.item]}

    def test_complete_source_and_model_receipt(self):
        result = self.helper.evaluate(self.request)
        self.assertTrue(result['ok']); self.assertEqual(result['remoteCalls'], 0)
        self.assertEqual(result['results'][0]['textHash'], self.item['textHash'])
        self.assertEqual(result['rankingPolicy'], RANKING_POLICY)

    def test_scoring_windows_cover_late_corrections_and_unicode_without_rewriting(self):
        text = '  ' + ('🙂 Alpha. '*100) + ' NPC: Correction: no payment before arrival.  '
        offsets = [(i,i+1) for i in range(2,len(text)-2)]
        windows = source_windows(text, offsets, 300, 192)
        self.assertGreater(len(windows),1)
        self.assertEqual(windows[0][0],0); self.assertEqual(windows[-1][1],len(text))
        self.assertTrue(all(b[0]<=a[1] for a,b in zip(windows,windows[1:])))
        self.assertIn('no payment before arrival.',text[windows[-1][0]:windows[-1][1]])
        self.assertTrue(all('�' not in text[a:b] for a,b in windows))

    def test_oversized_query_and_window_count_refuse_partial_coverage(self):
        with self.assertRaisesRegex(ValueError,'no_document_window'):
            source_windows('abc',[(0,1)],192)
        with self.assertRaisesRegex(ValueError,'window_limit'):
            source_windows('a'*5000,[(i,i+1) for i in range(5000)],256,192)
        with self.assertRaisesRegex(ValueError,'source_offsets'):
            source_windows('abc',[(0,4)],300)

    def test_changed_ranking_policy_is_not_a_cache_hit(self):
        self.helper.evaluate(self.request)
        self.request['rankingPolicy']='obsolete'
        self.assertFalse(self.helper.evaluate(self.request)['ok'])
        self.assertEqual(self.models.calls,1)

    def test_invalid_source_identity_and_size_never_run(self):
        for change in ({'textHash': 'changed'}, {'id': ''}, {'text': 'x'*24001}):
            req = copy.deepcopy(self.request); req['candidates'][0].update(change)
            self.assertFalse(self.helper.evaluate(req)['ok'])
        req = copy.deepcopy(self.request); req['candidates'] *= 2
        self.assertFalse(self.helper.evaluate(req)['ok']); self.assertEqual(self.models.calls, 0)

    def test_partial_or_nonfinite_ranking_never_published(self):
        for value in (math.nan, math.inf, -1, 2):
            self.models.score = value; result = self.helper.evaluate(self.request)
            self.assertFalse(result['ok']); self.assertEqual(result['results'], [])

    def test_query_and_source_bind_cache(self):
        self.helper.evaluate(self.request); self.helper.evaluate(self.request)
        self.assertEqual(self.models.calls, 1)
        req = copy.deepcopy(self.request); req['query'] = 'Who asked?'
        self.helper.evaluate(req); self.assertEqual(self.models.calls, 2)
        req['candidates'][0]['text'] = '300 only after arrival.'
        req['candidates'][0]['textHash'] = text_hash(req['candidates'][0]['text'])
        self.helper.evaluate(req); self.assertEqual(self.models.calls, 3)

    def test_exact_whitespace_and_uppercase_transport_fence(self):
        req = copy.deepcopy(self.request)
        req['candidates'][0].update(text='NPC: No.  \r\nPlayer: Agreed.')
        req['candidates'][0]['textHash'] = text_hash(req['candidates'][0]['text']).upper()
        self.assertTrue(self.helper.evaluate(req)['ok'])
        req['candidates'][0]['text'] = 'NPC: No.\nPlayer: Agreed.'
        self.assertFalse(self.helper.evaluate(req)['ok'])

    def test_deadline_discards_entire_partial_result(self):
        self.models.delay = .11; self.request['timeoutMs'] = 100
        result = self.helper.evaluate(self.request)
        self.assertFalse(result['ok']); self.assertEqual(result['results'], [])
        self.assertEqual(len(self.helper.cache), 0)

    def test_busy_falls_back_without_stealing_lock(self):
        self.helper.lock.acquire()
        try:
            result = self.helper.evaluate(self.request)
            self.assertFalse(result['ok']); self.assertTrue(self.helper.lock.locked())
            self.assertEqual(self.models.calls, 0)
        finally: self.helper.lock.release()

    def test_backend_is_explicit_and_binds_cache(self):
        self.helper.evaluate(self.request)
        self.request['backend'] = 'cuda'
        result = self.helper.evaluate(self.request)
        self.assertTrue(result['ok']); self.assertEqual(result['backend'], 'cuda')
        self.assertEqual(self.models.calls, 2)
        self.request['backend'] = 'remote'
        self.assertFalse(self.helper.evaluate(self.request)['ok'])
        self.assertEqual(self.models.calls, 2)

    def test_utf16_partition_and_protected_words(self):
        text = '🙂 NPC: No payment. '+('The curtains fluttered. '*10)
        segments = [{'index':0,'start':0,'text':'🙂 NPC: No payment. ','protected':True},
                    {'index':1,'start':20,'text':'The curtains fluttered. '*10,'protected':False}]
        item = {'text':text,'segments':segments}
        self.assertEqual(len(validate_segments(item)), 2)
        chosen = select_segments(segments, [.001,.999], .5)
        self.assertIn(0, chosen)
        segments[1]['start'] = 18
        with self.assertRaises(ValueError): validate_segments(item)

    def test_compression_cache_includes_protection_and_rate(self):
        text = 'Protected. Background.'
        item = {'id':'x','text':text,'textHash':text_hash(text),'rate':.5,'segments':[
            {'index':0,'start':0,'text':'Protected. ','protected':True},
            {'index':1,'start':11,'text':'Background.','protected':False}]}
        req = {'schema':SCHEMA,'operation':'compress','candidates':[item],'timeoutMs':500}
        self.assertTrue(self.helper.evaluate(req)['ok'])
        item['segments'][1]['protected'] = True
        self.assertEqual(self.helper.evaluate(req)['results'][0]['selectedSegments'], [0,1])
        self.assertEqual(self.models.calls, 2)

    def test_missing_optional_models_is_bounded_local_failure(self):
        with tempfile.TemporaryDirectory() as path:
            result = MemorySelection(path).evaluate(self.request)
            self.assertFalse(result['ok']); self.assertEqual(result['results'], [])
            self.assertEqual(result['remoteCalls'], 0)
