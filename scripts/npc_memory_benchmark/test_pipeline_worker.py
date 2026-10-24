import pathlib
import sys
import unittest
from pipeline_worker import validate_options, check_request

class PipelineIsolationTests(unittest.TestCase):
    def test_production_ports_and_unsupported_model_are_rejected(self):
        for args in [('bge', 5102, 'a'*32, 512), ('other', 49123, 'a'*32, 512),
                     ('minilm', 49123, 'a'*32, 512), ('bge', 49123, '../data', 512)]:
            with self.assertRaises(ValueError): validate_options(*args)
        validate_options('minilm', 49123, 'a'*32, 256)

    def test_live_campaigns_external_stores_and_control_routes_are_rejected(self):
        good = {'provider': 'local', 'collection': 'reign_memory_bge_small_en_v1_5_v1',
                'filters': {'campaignId': 'evidence_replay_'+'a'*32, 'observerId': '0:npc'}}
        check_request('/vectors/search', good)
        for body in [{**good, 'provider': 'qdrant'}, {**good, 'qdrantUrl': 'http://127.0.0.1:6333'},
                     {**good, 'filters': {'campaignId': 'live_campaign'}}, {**good, 'collection': 'installed'}]:
            with self.assertRaises(ValueError): check_request('/vectors/search', body)
        with self.assertRaises(ValueError): check_request('/shutdown', {})

    def test_short_model_window_preserves_long_source_and_tail_without_truncation(self):
        source = pathlib.Path(__file__).resolve().parents[2] / 'services/vector-worker'
        sys.path.insert(0, str(source))
        import worker
        from tokenizers import Tokenizer, models, pre_tokenizers, processors
        tokenizer = Tokenizer(models.WordLevel({'[UNK]': 0, '[CLS]': 1, '[SEP]': 2}, unk_token='[UNK]'))
        tokenizer.pre_tokenizer = pre_tokenizers.Whitespace()
        tokenizer.post_processor = processors.TemplateProcessing(single='[CLS] $A [SEP]', special_tokens=[('[CLS]', 1), ('[SEP]', 2)])
        text = ' '.join('source_word_'+str(i) for i in range(1400)) + ' FINAL_OUTCOME_PAID_700'
        doc = {'id': 'source', 'text': text, 'payload': {'sourceType': 'memory', 'ownerId': 'npc'}}
        for window in (256, 512):
            chunks = worker.contextual_chunks(doc, tokenizer, window)
            self.assertGreater(len(chunks), 1)
            self.assertIn('FINAL_OUTCOME_PAID_700', chunks[-1]['text'])
            covered = set()
            for chunk in chunks:
                self.assertLessEqual(len(tokenizer.encode(chunk['text']).ids), window)
                payload = chunk['payload']
                self.assertEqual(text[payload['sourceStart']:payload['sourceEnd']], chunk['text'].split('\n', 1)[1])
                covered.update(range(payload['sourceStart'], payload['sourceEnd']))
            self.assertTrue(all(i in covered for i, char in enumerate(text) if not char.isspace()))

if __name__ == '__main__': unittest.main()
