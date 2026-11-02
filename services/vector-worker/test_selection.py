"""No inference/downloads: production selection boundaries with injected scorers."""
import copy
import math
import tempfile
import time
import threading
import unittest
import types
from pathlib import Path
from unittest.mock import patch
from selection import QWEN_CPU_HASH, QWEN_HASH, FILES, OnnxSelectionModels, validate_rank_canary, MemorySelection, SCHEMA, RANKING_POLICY, text_hash, select_segments, validate_segments, source_windows


class TensorFixture:
    """Shape/value recorder for dependency-free input contracts, not inference."""
    def __init__(self, shape, values=None, finite=True):
        self.shape, self.values, self.finite = shape, values, finite
    def __getitem__(self, index):
        if index == (None, slice(None)):
            return TensorFixture((1,) + self.shape, [self.values], self.finite)
        if index == 0:
            return TensorFixture(self.shape[1:], self.values[0] if self.values is not None else None, self.finite)
        raise AssertionError('Unexpected tensor indexing')
    def astype(self, dtype): return self
    def tolist(self): return self.values


def tensor_math_fixture():
    return types.SimpleNamespace(int64=int, float16=float, float32=float, float64=float,
        asarray=lambda values, dtype: TensorFixture((len(values), len(values[0])), values),
        ones=lambda shape, dtype: TensorFixture(shape),
        zeros=lambda shape, dtype: TensorFixture(shape),
        arange=lambda start, stop, dtype: TensorFixture((stop-start,), list(range(start, stop))),
        isfinite=lambda value: types.SimpleNamespace(all=lambda: value.finite))


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

    def test_cpu_canary_rejects_saturation_and_nonfinite_scores(self):
        validate_rank_canary(.996425, .00005994)
        for pair in ((.999705,.999602),(math.nan,0),(1,math.inf),(.6,.2),(-1,0),(1,-1)):
            with self.assertRaisesRegex(ValueError, 'selection_cpu_canary_failed'):
                validate_rank_canary(*pair)

    def test_prefix_reuse_checks_exact_ids_offsets_and_releases_query_state(self):
        models = OnnxSelectionModels('unused')
        prefix = list(range(32))
        inputs = [types.SimpleNamespace(name=f'past_key_values.{i}.{kind}', type='tensor(float)')
                  for i in range(28) for kind in ('key','value')]
        session = types.SimpleNamespace(get_inputs=lambda:inputs)
        calls = []
        def run(_, feed, deadline, outputs=None):
            calls.append(feed)
            return ([TensorFixture((1,8,32,128)) for _ in range(56)] if outputs else TensorFixture((1,2)))
        with patch('selection.np', tensor_math_fixture()), patch.object(models, 'binary_score', return_value=.5), \
                patch.object(models, 'run', side_effect=run):
            self.assertEqual(models.rank_cached_tokens(session,prefix+[99],prefix,time.perf_counter()+1),.5)
            self.assertEqual(models.rank_cached_tokens(session,prefix+[100],prefix,time.perf_counter()+1),.5)
            self.assertEqual(len(calls),3)
            self.assertEqual(calls[1]['position_ids'].tolist(),[[32]])
            self.assertEqual(calls[1]['attention_mask'].shape,(1,33))
            self.assertEqual(calls[1]['past_key_values.27.value'].shape,(1,8,32,128))
            with patch.object(models,'rank_tokens',return_value=.9) as full:
                self.assertEqual(models.rank_cached_tokens(session,[999]+prefix,prefix,time.perf_counter()+1),.9)
                full.assert_called_once()
        models.end_request()
        self.assertIsNone(models._prefix_past)
        self.assertIsNone(models._prefix_ids)
        self.assertEqual(len(models.common_prefix(list(range(1500)),list(range(1500)))),1024)

    def test_cold_initializer_is_single_flight_and_does_not_block_request(self):
        models = OnnxSelectionModels('unused')
        helper = MemorySelection('unused',models)
        entered, release = threading.Event(), threading.Event()
        calls = []
        def initialize(*args):
            calls.append(1); entered.set(); release.wait(2)
            models.sessions['rank:cpu'] = object()
        with patch.object(models,'load',side_effect=initialize):
            try:
                result = helper.evaluate(self.request)
                self.assertEqual(result['status'],'selection_initializing')
                self.assertEqual(result['results'],[])
                self.assertLess(result['elapsedMs'],100)
                self.assertTrue(entered.wait(1))
                self.assertFalse(models.prepare_rank('cpu'))
                self.assertFalse(models.prepare_rank('cuda'))
                self.assertEqual(len(calls),1)
                self.assertTrue(helper.status()['cpuInitializing'])
            finally:
                release.set(); models._warm_thread.join(2)
        self.assertTrue(models.prepare_rank('cpu'))

    def test_nonfinite_prefix_cache_is_never_published(self):
        models = OnnxSelectionModels('unused')
        with patch('selection.np', tensor_math_fixture()), \
                patch.object(models,'run',return_value=[TensorFixture((1,8,32,128),finite=False) for _ in range(56)]):
            session = types.SimpleNamespace(get_inputs=lambda:[])
            with self.assertRaisesRegex(ValueError,'invalid_rank_prefix_cache'):
                models.rank_cached_tokens(session,list(range(40)),list(range(32)),time.perf_counter()+1)
        self.assertIsNone(models._prefix_past)

    def test_shadow_admission_is_nonblocking_single_flight_and_receipt_bound(self):
        entered, release = threading.Event(), threading.Event()
        original = self.models.rank
        observed = []
        def blocked(query,text,deadline,backend):
            entered.set(); release.wait(2); observed.append(text)
            return original(query,text,deadline,backend)
        request = copy.deepcopy(self.request)
        request.update(shadow=True,backend='cpu')
        with patch.object(self.models,'rank',side_effect=blocked):
            try:
                started=time.perf_counter(); receipt=self.helper.evaluate_shadow(request)
                self.assertLess(time.perf_counter()-started,.1)
                self.assertTrue(receipt['ok']); self.assertEqual(receipt['status'],'shadow_queued')
                self.assertEqual(receipt['results'],[])
                self.assertTrue(entered.wait(1))
                request['candidates'][0]['text']='caller changed after submission'
                self.assertEqual(self.helper.evaluate_shadow(dict(self.request,shadow=True,backend='cpu'))['status'],'shadow_busy')
                # Regular requests have the existing100ms lock bound, never wait
                # for the background inference's15second budget.
                result=self.helper.evaluate(self.request)
                self.assertEqual(result['status'],'selection_busy')
                self.assertFalse(release.is_set())
            finally:
                release.set()
                for _ in range(200):
                    if not self.helper._shadow_busy: break
                    time.sleep(.005)
        terminal=self.helper.status()['lastShadow']
        self.assertEqual(terminal['jobId'],receipt['shadowJobId'])
        self.assertEqual(terminal['state'],'completed'); self.assertTrue(terminal['result']['ok'])
        self.assertEqual(observed,[self.item['text']])
        self.assertNotIn('No payment',str(terminal))

    def test_shadow_refuses_invalid_unready_and_busy_work_without_phantom_job(self):
        request=dict(self.request,shadow=True,backend='cuda')
        self.assertFalse(self.helper.evaluate_shadow(request)['ok'])
        request['backend']='cpu'
        self.models.prepare_rank=lambda backend:False
        receipt=self.helper.evaluate_shadow(request)
        self.assertEqual(receipt['status'],'selection_initializing')
        self.assertNotIn('shadowJobId',receipt)
        self.assertIsNone(self.helper.status()['lastShadow'])
        del self.models.prepare_rank
        self.helper.lock.acquire()
        try:
            receipt=self.helper.evaluate_shadow(request)
            self.assertTrue(receipt['ok'])
            for _ in range(200):
                if not self.helper._shadow_busy:break
                time.sleep(.005)
            self.assertEqual(self.helper.status()['lastShadow']['result']['status'],'selection_busy')
            self.assertEqual(self.models.calls,0)
        finally:self.helper.lock.release()

    def test_unready_backend_does_not_clear_initializer_owned_kv(self):
        models=OnnxSelectionModels('unused')
        models.sessions['rank:cuda']=object()
        sentinel=object();models._prefix_past=sentinel
        with patch.object(models,'prepare_rank',return_value=False):
            result=MemorySelection('unused',models).evaluate(dict(self.request,backend='cuda'))
        self.assertEqual(result['status'],'selection_initializing')
        self.assertIs(models._prefix_past,sentinel)

    def test_shadow_deadline_publishes_terminal_failure_and_releases_slot(self):
        self.models.delay=.11
        receipt=self.helper.evaluate_shadow(dict(self.request,shadow=True,backend='cpu',timeoutMs=100))
        self.assertTrue(receipt['ok'])
        for _ in range(200):
            if not self.helper._shadow_busy:break
            time.sleep(.005)
        terminal=self.helper.status()['lastShadow']
        self.assertEqual(terminal['jobId'],receipt['shadowJobId'])
        self.assertFalse(terminal['result']['ok'])
        self.assertEqual(terminal['result']['results'],[])
        self.assertEqual(terminal['result']['status'],'selection_deadline')
        self.assertFalse(self.helper._shadow_busy)
        self.assertFalse(self.helper.lock.locked())

    def test_failed_cpu_load_never_publishes_session_or_tokenizer(self):
        prompts = []
        tokenizer = types.SimpleNamespace(no_truncation=lambda: None, no_padding=lambda: None,
            encode=lambda text, **_: (prompts.append(text) or types.SimpleNamespace(ids=[1,2])))
        ort = types.SimpleNamespace(__version__='1.22.0', SessionOptions=lambda:types.SimpleNamespace(add_session_config_entry=lambda *args:None),
            InferenceSession=lambda *args, **kwargs: object())
        with tempfile.TemporaryDirectory() as directory:
            for name in ('qwen-reranker-cpu.onnx','qwen-reranker-tokenizer.json'):
                (Path(directory)/name).touch()
            models = OnnxSelectionModels(directory)
            with patch.dict('sys.modules', {'onnxruntime':ort,
                    'tokenizers':types.SimpleNamespace(Tokenizer=types.SimpleNamespace(from_file=lambda _:tokenizer))}), \
                    patch('selection.file_hash', side_effect=lambda path: FILES[path.name]):
                with patch.object(models, 'rank_tokens', side_effect=[.999705,.999602]):
                    with self.assertRaisesRegex(ValueError,'canary_failed'): models.load('rank')
                self.assertEqual(models.sessions, {}); self.assertEqual(models.tokenizers, {})
                with patch.object(models, 'rank_tokens', side_effect=TimeoutError('selection_deadline')):
                    with self.assertRaises(TimeoutError): models.load('rank')
                self.assertEqual(models.sessions, {}); self.assertEqual(models.tokenizers, {})
                with patch.object(models, 'rank_tokens', side_effect=[.996425,.00005994]), \
                        patch.object(models, 'rank_cached_tokens', side_effect=[.996425,.00005994]): models.load('rank')
                self.assertEqual(list(models.sessions), ['rank:cpu'])
                self.assertIn('What price did we agree for the caravan?', prompts[0])
                self.assertIn('No money has been paid.', prompts[0])

    def test_backend_model_hash_is_not_substitutable(self):
        self.assertEqual(self.helper.evaluate(self.request)['modelHash'], QWEN_CPU_HASH)
        self.request['backend'] = 'cuda'
        self.assertEqual(self.helper.evaluate(self.request)['modelHash'], QWEN_HASH)
        self.assertNotEqual(QWEN_CPU_HASH, QWEN_HASH)

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
