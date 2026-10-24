"""Isolated, pinned worker adapter for artifact-bound full-pipeline benchmarks.

Uses production vector handlers/index/filter/cache code. Only the candidate encoder
and its certified chunk window differ. Never point this at an installed data tree.
"""
from __future__ import annotations
import argparse
import functools
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import socket
import sys
import threading
import time
import types

HERE = Path(__file__).resolve().parent

def inventory(root):
    files = [{'path': p.relative_to(root).as_posix(), 'bytes': p.stat().st_size,
              'sha256': hashlib.sha256(p.read_bytes()).hexdigest()}
             for p in sorted(root.rglob('*')) if p.is_file()]
    return files, hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()

def validate_options(model, port, token, window):
    if model not in ('bge', 'minilm') or not 40000 <= port <= 65000:
        raise ValueError('Only the two pinned models and high loopback ports are allowed.')
    if not re.fullmatch(r'[0-9a-f]{32}', token):
        raise ValueError('A fresh 128-bit nonce is required.')
    if window not in (256, 512) or (model == 'minilm' and window != 256):
        raise ValueError('Invalid model window.')

def check_request(path, body):
    if path not in ('/vectors/upsert', '/vectors/search', '/vectors/status', '/replay/stop'):
        raise ValueError('Only isolated vector operations are enabled.')
    if path in ('/vectors/status', '/replay/stop'):
        return
    if body.get('provider', 'local') != 'local' or body.get('qdrantUrl') or body.get('qdrantApiKey'):
        raise ValueError('External providers and credentials are forbidden.')
    if body.get('collection') != 'reign_memory_bge_small_en_v1_5_v1':
        raise ValueError('Only the production collection alias inside this fresh store is allowed.')
    payloads = [d.get('payload', {}) for d in body.get('documents', [])] if path.endswith('upsert') else [body.get('filters', {})]
    if not payloads or any(not re.fullmatch(r'evidence_replay_[0-9a-fA-F]{32}', str(p.get('campaignId', ''))) for p in payloads):
        raise ValueError('Only owned evidence-replay campaigns are accepted.')

def block_external_network():
    connect, resolve = socket.socket.connect, socket.getaddrinfo
    def local(host):
        return str(host) in ('127.0.0.1', '::1', 'localhost')
    def guarded_connect(instance, address):
        if isinstance(address, tuple) and not local(address[0]):
            raise RuntimeError('External network disabled for pipeline benchmark.')
        return connect(instance, address)
    def guarded_resolve(host, *args, **kwargs):
        if not local(host):
            raise RuntimeError('External DNS disabled for pipeline benchmark.')
        return resolve(host, *args, **kwargs)
    socket.socket.connect = guarded_connect
    socket.getaddrinfo = guarded_resolve

def memory():
    import resource
    pages = int(Path('/proc/self/statm').read_text().split()[1])
    return {'residentBytes': pages * os.sysconf('SC_PAGE_SIZE'),
            'peakResidentBytes': resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * 1024}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', choices=('bge', 'minilm'), required=True)
    parser.add_argument('--model-dir', type=Path, required=True)
    parser.add_argument('--data-dir', type=Path, required=True)
    parser.add_argument('--ready', type=Path, required=True)
    parser.add_argument('--port', type=int, required=True)
    parser.add_argument('--token', required=True)
    parser.add_argument('--threads', type=int, choices=range(1, 5), default=2)
    parser.add_argument('--window', type=int)
    args = parser.parse_args()
    window = args.window or (512 if args.model == 'bge' else 256)
    validate_options(args.model, args.port, args.token, window)
    model_dir = args.model_dir.resolve(strict=True)
    data_dir = args.data_dir.resolve()
    if data_dir.name != 'vectors-' + args.token or '.codex-build' not in data_dir.parts:
        raise ValueError('Data must be nonce-named beneath a task .codex-build evidence directory.')
    if args.ready.resolve().parent != data_dir.parent:
        raise ValueError('Ready receipt must stay beside the owned vector directory.')
    if data_dir.exists() or args.ready.exists() or data_dir == model_dir or data_dir in model_dir.parents:
        raise ValueError('Fresh run-owned data and ready paths are required.')
    specs = json.loads((HERE / 'models.json').read_text(encoding='utf-8'))
    spec = next(m for m in specs['models'] if m['name'] == args.model)
    for item in spec['files']:
        path = model_dir / item['file']
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != item['sha256']:
            raise ValueError('Pinned model artifact mismatch: ' + item['file'])
    files, tree_hash = inventory(model_dir)
    for key in ('OMP_NUM_THREADS', 'OPENBLAS_NUM_THREADS', 'MKL_NUM_THREADS'):
        os.environ[key] = str(args.threads)
    os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', TOKENIZERS_PARALLELISM='false',
                      REIGN_VECTOR_MODEL_DIR=str(model_dir), REIGN_VECTOR_THREADS=str(args.threads))
    block_external_network()
    source = HERE.parents[1] / 'services/vector-worker'
    sys.path.insert(0, str(source))
    import worker
    from tokenizers import Tokenizer
    data_dir.mkdir(parents=True)
    model_started = time.perf_counter()
    worker.STATE = worker.WorkerState(data_dir)
    if args.model == 'minilm':
        from benchmark import Encoder
        encoder = Encoder(types.SimpleNamespace(model_dir=str(model_dir), backend='onnx', pooling='mean',
            max_tokens=256, threads=args.threads, model_file='model.onnx', query_prefix='', document_prefix=''))
        class Adapter:
            def embed(self, texts, batch_size=4):
                texts = list(texts)
                if any(len(encoder.tokenizer.encode(t).ids) > 256 for t in texts):
                    raise ValueError('MiniLM input exceeds256 tokens; refuse truncation.')
                yield from encoder.encode(texts, 'document')
        worker.STATE.model = Adapter()
        worker.MODEL_NAME = 'sentence-transformers/all-MiniLM-L6-v2'
        worker.MODEL_VERSION = 'isolated-minilm-' + tree_hash
    worker.STATE.tokenizer = Tokenizer.from_file(str(model_dir / 'tokenizer.json'))
    worker.STATE.tokenizer.no_truncation(); worker.STATE.tokenizer.no_padding()
    worker.contextual_chunks = functools.partial(worker.contextual_chunks, model_window_tokens=window)
    worker.STATE.embed(['Bannerlord Reign semantic memory initialization.'])
    load_ms = (time.perf_counter()-model_started)*1000
    base = {'schema': 'reign-dense-pipeline-worker-v1', 'isolated': True,
            'externalNetworkBlocked': True, 'model': args.model, 'modelTreeSha256': tree_hash,
            'modelWindowTokens': window, 'threads': args.threads, 'pid': os.getpid(),
            'workerSourceSha256': hashlib.sha256((source/'worker.py').read_bytes()).hexdigest(),
            'adapterSourceSha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            'modelFiles': files, 'warmupMs': load_ms}
    class Handler(worker.Handler):
        def log_message(self, *unused): pass
        def send_json(self, status, body):
            body = dict(body); body['denseReplay'] = {**base, 'memory': memory()}
            if self.path == '/vectors/search':
                with (data_dir/'searches.jsonl').open('a', encoding='utf-8') as audit:
                    json.dump({'request': getattr(self, 'audit_body', {}), 'response': body}, audit, ensure_ascii=False)
                    audit.write('\n')
            return super().send_json(status, body)
        def do_POST(self):
            prefix = '/' + args.token
            if not self.path.startswith(prefix + '/'):
                self.send_json(403, {'ok': False, 'error': 'nonce_required'}); return
            self.path = self.path[len(prefix):]
            try:
                body = self.read_json()
                check_request(self.path, body)
                self.audit_body = body
                self.read_json = lambda: body
                if self.path == '/replay/stop':
                    self.send_json(200, {'ok': True})
                    threading.Thread(target=worker.SERVER.shutdown, daemon=True).start()
                else: super().do_POST()
            except Exception as ex:
                self.send_json(400, {'ok': False, 'error': str(ex)})
        def do_GET(self): self.send_json(405, {'ok': False, 'error': 'post_only'})
    worker.SERVER = worker.ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
    signal.signal(signal.SIGTERM, lambda *_: threading.Thread(target=worker.SERVER.shutdown, daemon=True).start())
    with args.ready.open('x', encoding='utf-8') as f:
        json.dump({**base, 'port': args.port, 'memory': memory()}, f, indent=2)
    try: worker.SERVER.serve_forever(poll_interval=.1)
    finally:
        worker.SERVER.server_close()
        for client in worker.STATE.clients.values(): client.close()
        (data_dir/'final-memory.json').write_text(json.dumps(memory()), encoding='utf-8')

if __name__ == '__main__': main()
