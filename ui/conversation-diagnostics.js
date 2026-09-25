// Embedded by ControlCenterHtml so isolated previews exercise the production UI.
(() => {
  const state = { key: '', responseLimit: 40, conversationLimit: 40, generation: 0, timer: null, searchTimer: null, loaded: new Map(), responses: [], busy: false };
  const el = id => document.getElementById(id);
  const make = (tag, text, className) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (className) n.className = className; return n; };
  const setText = (node, text) => { if (node.textContent !== text) node.textContent = text; };
  // Keep connected panels in place. Re-appending them between trace requests makes
  // the browser follow a moving scroll anchor and discards focus/text selection.
  function reconcileChildren(host, nodes) {
    const wanted = new Set(nodes);
    for (const child of [...host.childNodes]) if (!wanted.has(child)) child.remove();
    let next = host.firstChild;
    for (const node of nodes) {
      if (node === next) next = next.nextSibling;
      else if (host.moveBefore && node.parentNode === host) host.moveBefore(node, next);
      else host.insertBefore(node, next);
    }
  }
  const seconds = ms => typeof ms === 'number' ? `${(ms / 1000).toFixed(1)} s` : 'Timing unavailable';
  const label = value => String(value || '').replaceAll('_', ' ');
  const responseMeta = t => `${seconds(t.elapsedMs)} · ${t.model || t.mode || 'Model not recorded'} · ${label(t.status)}${t.repairCount ? ' · ' + t.repairCount + (t.repairCount === 1 ? ' repair' : ' repairs') : ''}${t.captureInProgress ? ' · Background capture running' : ''}`;
  const base = '/api/diagnostics/conversations';
  function clearResponseSelection() {
    state.responses = [];
    el('cdResponses').replaceChildren();
    el('cdExport').disabled = true;
    el('cdClear').disabled = true;
    el('cdMoreResponses').hidden = true;
  }
  async function api(path, params = {}, body) {
    const r = await fetch(base + path + (Object.keys(params).length ? '?' + new URLSearchParams(params) : ''), body === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const j = await r.json(); if (!r.ok || j.ok === false) throw new Error(j.error || `Request failed (${r.status})`); return j;
  }
  function filters() { return { campaignId: el('cdCampaign').value, search: el('cdSearch').value, mode: el('cdMode').value, status: el('cdStatus').value, from: el('cdFrom').value, to: el('cdTo').value }; }
  function options(id, values, first, extra = []) {
    const select = el(id), previous = select.value;
    const signature = JSON.stringify([values, first, extra, previous]);
    if (select.dataset.signature === signature) return;
    select.dataset.signature = signature;
    select.replaceChildren();
    for (const [value, title] of [['', first], ...extra, ...values.map(v => [v, label(v)])]) { const o = make('option', title); o.value = value; select.append(o); }
    if (previous && !Array.from(select.options).some(o => o.value === previous)) { const o = make('option', previous); o.value = previous; select.append(o); }
    select.value = previous;
  }
  async function captureStatus() {
    const j = await api('/capture'); el('cdCapture').checked = !!j.enabled;
    setText(el('cdCaptureStatus'), `${j.enabled ? 'Full capture ON' : 'Full capture OFF'} · ${((j.storageBytes || 0) / 1048576).toFixed(1)} MiB retained · ${j.activeCaptures || 0} active captures${j.error ? ' · Capture problem: ' + j.error : ''}`);
    el('cdCaptureStatus').classList.toggle('cdProblem', !!j.error);
  }
  window.setConversationCapture = async enabled => {
    const control = el('cdCapture'); control.disabled = true;
    try { await api('/capture', {}, { enabled }); await captureStatus(); }
    catch (e) { control.checked = !enabled; el('cdStatusText').textContent = e.message; }
    finally { control.disabled = false; }
  };
  function details(title, className) {
    const d = make('details', undefined, className); d.append(make('summary', title)); return d;
  }
  function addField(parent, name, value) {
    if (value === undefined || value === null || value === '') return;
    const block = make('div', undefined, 'cdField'); block.append(make('h4', label(name)));
    if (typeof value === 'string') {
      let parsed; try { parsed = JSON.parse(value); } catch (_) { }
      if (parsed && typeof parsed === 'object') renderValue(block, parsed, 0);
      else block.append(make('div', value, 'cdProse'));
    } else renderValue(block, value, 0);
    parent.append(block);
  }
  function renderValue(parent, value, depth) {
    if (Array.isArray(value)) {
      if (!value.length) { parent.append(make('p', 'Empty list (no entries).', 'hint')); return; }
      value.forEach((item, i) => {
        if (item && typeof item === 'object') {
          const d = details(item.role ? `Message ${i + 1} · ${item.role}` : item.type || item.title || `Entry ${i + 1}`, 'cdDataItem');
          d.open = depth < 1 && value.length < 5; renderValue(d, item, depth + 1); parent.append(d);
        } else parent.append(make('p', String(item), 'cdProse'));
      }); return;
    }
    if (value && typeof value === 'object') {
      const entries = Object.entries(value);
      if (!entries.length) { parent.append(make('p', 'Empty object — no fields.', 'cdProse')); return; }
      for (const [key, v] of entries) {
        if (v && typeof v === 'object') {
          const d = details(label(key), 'cdDataItem'); d.open = depth === 0 && ['issues','messages','received','result','detected'].includes(key); renderValue(d, v, depth + 1); parent.append(d);
        } else {
          const block = make('div', undefined, 'cdField'); block.append(make('strong', label(key)));
          let parsed; if (typeof v === 'string' && ['responseBody','requestBody','received','result','extracted','cleaned'].includes(key)) { try { parsed = JSON.parse(v); } catch (_) { } }
          if (parsed && typeof parsed === 'object') renderValue(block, parsed, depth + 1);
          else block.append(make('div', v == null ? 'Not reported' : String(v), 'cdProse'));
          parent.append(block);
        }
      } return;
    }
    parent.append(make('div', value == null ? 'Not reported' : String(value), 'cdProse'));
  }
  async function loadPayload(trace, evidence, host, generation) {
    host.replaceChildren(make('p', 'Loading complete evidence…', 'hint'));
    try {
      let offset = 0, text = '', page, cancelled = false;
      do {
        page = await api('/payload', { traceId: trace.traceId, campaignId: trace.campaignId || '', payloadId: evidence.payloadId, offset, length: 12000 });
        if (generation !== state.generation || !host.isConnected) { cancelled = true; break; }
        text += page.content || '';
        if (page.hasMore && page.nextOffset <= offset) throw new Error('Payload paging stopped making progress. Evidence is incomplete.');
        offset = page.nextOffset;
        host.firstChild.textContent = `Loading evidence… ${offset.toLocaleString()} characters`;
      } while (page.hasMore);
      if (cancelled) return;
      host.replaceChildren();
      const meta = page.metadata || {};
      host.append(make('p', `${text.length.toLocaleString()} characters · ${meta.sanitizedLegacy ? 'Historical sanitized evidence; may be incomplete' : meta.complete ? 'Complete captured payload' : 'Partial evidence'}${meta.credentialRedacted ? ' · Credentials removed' : ''}`, 'hint'));
      let parsed; try { parsed = JSON.parse(text); } catch (_) { }
      if (parsed && typeof parsed === 'object') {
        const before = parsed.received ?? parsed.extracted, after = parsed.result ?? parsed.cleaned;
        if (before !== undefined && after !== undefined) {
          const compare = make('div', undefined, 'cdComparison');
          addField(compare, 'Received', before); addField(compare, 'Result', after); host.append(compare);
          const rest = { ...parsed }; ['received','extracted','result','cleaned'].forEach(k => delete rest[k]); renderValue(host, rest, 0);
        } else renderValue(host, parsed, 0);
      } else host.append(make('div', text, 'cdProse'));
      const raw = details('Raw captured payload', 'cdRaw'); raw.append(make('pre', text));
      const actions = make('div', undefined, 'actions');
      const download = make('button', 'Download payload'); download.onclick = () => {
        const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' })); const a = make('a'); a.href = url; a.download = evidence.payloadId + '.txt'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
      }; actions.append(download); raw.append(actions); host.append(raw);
    } catch (e) { host.replaceChildren(make('p', e.message, 'cdProblem')); }
  }
  function stepNode(trace, step, generation, node) {
    if (!node) {
      node = details('', 'cdStep');
      node.append(make('p', '', 'cdProse'), make('div', undefined, 'cdStepEvidence'));
    }
    node.dataset.stepId = step.stepId;
    setText(node.firstChild, `${step.title} · ${label(step.status)}${step.durationMs ? ' · ' + seconds(step.durationMs) : ''}`);
    setText(node.querySelector(':scope > .cdProse'), step.explanation || '');
    const evidenceHost = node.querySelector(':scope > .cdStepEvidence');
    const previous = new Map([...evidenceHost.children].map(x => [x.dataset.payloadId, x])), evidenceNodes = [];
    for (const evidence of step.evidence || []) {
      let d = previous.get(evidence.payloadId);
      if (!d) {
        d = details('', 'cdEvidence'); d.dataset.payloadId = evidence.payloadId;
        const host = make('div'); d.append(host);
        d.addEventListener('toggle', () => { if (d.open && !d.dataset.loaded) { d.dataset.loaded = 'true'; loadPayload(trace, evidence, host, generation); } });
      }
      setText(d.firstChild, evidence.label || 'Evidence'); evidenceNodes.push(d);
    }
    if (!evidenceNodes.length) {
      const hint = evidenceHost.querySelector('.hint') || make('p', '', 'hint');
      setText(hint, step.payloadAvailability === 'capture_failed' ? 'Capture failed. See recording status.' : 'Full payload was not recorded. Enable full capture for future responses.'); evidenceNodes.push(hint);
    }
    reconcileChildren(evidenceHost, evidenceNodes);
    return node;
  }
  async function expandResponse(trace, host, refresh = false) {
    const generation = state.generation;
    try {
      const j = await api('/trace', { traceId: trace.traceId, campaignId: trace.campaignId || '' });
      if (generation !== state.generation || !host.isConnected) return;
      const signature = JSON.stringify([j.steps || [], trace.legacy, j.response?.evidenceNotice, j.response?.captureError, j.timingNote]);
      if (host.dataset.signature === signature) return;
      host.dataset.signature = signature;
      const previous = new Map([...host.querySelectorAll('details[data-step-id]')].map(x => [x.dataset.stepId, x]));
      const roots = host.querySelector(':scope > .cdSteps') || make('div', undefined, 'cdSteps');
      const nodes = new Map(), steps = j.steps || [], childrenByHost = new Map([[roots, []]]);
      for (const step of steps) {
        const old = previous.get(step.stepId), fingerprint = JSON.stringify(step);
        const node = old?.dataset.fingerprint === fingerprint ? old : stepNode(trace, step, generation, old);
        node.dataset.fingerprint = fingerprint;
        nodes.set(step.stepId, node);
        const children = node.querySelector(':scope > .cdChildren'); if (children) childrenByHost.set(children, []);
      }
      for (const step of steps) {
        const node = nodes.get(step.stepId), parent = nodes.get(step.parentStepId);
        if (parent && parent !== node) {
          let children = parent.querySelector(':scope > .cdChildren');
          if (!children) { children = make('div', undefined, 'cdChildren'); parent.append(children); childrenByHost.set(children, []); }
          childrenByHost.get(children).push(node);
        } else childrenByHost.get(roots).push(node);
      }
      for (const [parent, children] of childrenByHost) reconcileChildren(parent, children);
      const contents = [];
      for (const [name, text, style] of [
        ['legacy', trace.legacy ? j.response?.evidenceNotice || 'Partial historical evidence.' : '', 'cdNotice'],
        ['error', j.response?.captureError ? 'Capture problem: ' + j.response.captureError : '', 'cdProblem'],
        ['timing', j.timingNote, 'hint']
      ]) if (text) {
        const notice = host.querySelector(`[data-notice="${name}"]`) || make('p', '', style);
        notice.dataset.notice = name; setText(notice, text); contents.push(notice);
      }
      contents.push(roots);
      if (!steps.length) contents.push(make('p', 'No processing steps are retained for this response.', 'hint'));
      reconcileChildren(host, contents);
    } catch (e) { if (!refresh) host.replaceChildren(make('p', e.message, 'cdProblem')); }
  }
  function responseNode(trace) {
    const article = make('article', undefined, 'cdExchange'); article.dataset.traceId = trace.traceId;
    if (trace.playerText) { const player = make('div', undefined, 'cdPlayer'); player.append(make('h3', 'You'), make('div', trace.playerText, 'cdProse')); article.append(player); }
    const d = details('', 'cdResponse'), summary = d.firstChild;
    summary.append(make('strong', trace.heroName || trace.heroId || 'LLM activity'), make('span', responseMeta(trace), 'cdResponseMeta'), make('div', trace.reply || (trace.status === 'in_progress' ? 'Response in progress…' : 'No final dialogue was returned.'), 'cdProse'), make('span', 'Expand processing steps', 'cdExpandHint'));
    const body = make('div'); d.append(body);
    d.addEventListener('toggle', () => { if (d.open && !d.dataset.loaded) { d.dataset.loaded = 'true'; expandResponse(trace, body); } });
    article.append(d); return article;
  }
  async function responses(refresh = false) {
    if (!state.key) return;
    const generation = state.generation, all = [];
    // API pages stay bounded even when the user explicitly loads older/more turns.
    for (let offset = 0; offset < state.responseLimit; offset += 100) {
      const j = await api('', { ...filters(), conversationKey: state.key, offset, limit: Math.min(100, state.responseLimit - offset) });
      if (generation !== state.generation) return;
      all.push(...(j.responses || [])); el('cdMoreResponses').hidden = all.length >= (j.responseCount || 0);
      if (all.length >= (j.responseCount || 0)) break;
    }
    state.responses = all;
    const host = el('cdResponses'), known = new Map([...host.children].map(x => [x.dataset.traceId, x]));
    const nodes = [];
    for (const trace of all) {
      let node = known.get(trace.traceId);
      if (!node) { node = responseNode(trace); }
      else {
        const d = node.querySelector('.cdResponse'), meta = d.querySelector('.cdResponseMeta');
        setText(meta, responseMeta(trace));
        setText(d.querySelector('summary .cdProse'), trace.reply || (trace.status === 'in_progress' ? 'Response in progress…' : 'No final dialogue was returned.'));
      }
      nodes.push(node);
    }
    reconcileChildren(host, nodes);
    el('cdExport').disabled = !all.some(x => !x.legacy);
    el('cdClear').disabled = !all.some(x => !x.legacy && x.status !== 'in_progress' && !x.captureInProgress);
    if (!all.length) host.replaceChildren(make('p', 'No responses match these filters.', 'hint'));
    for (let i = 0; i < all.length; i++) {
      if (generation !== state.generation) return;
      const d = nodes[i].querySelector('.cdResponse');
      if (d.open) await expandResponse(all[i], d.lastChild, refresh);
    }
  }
  window.loadConversationDiagnostics = async () => {
    if (state.busy) { state.refreshPending = true; return; } state.busy = true;
    const generation = state.generation;
    try {
      const [j] = await Promise.all([api('', { ...filters(), limit: Math.min(100, state.conversationLimit) }), captureStatus()]);
      for (let offset = 100; offset < Math.min(state.conversationLimit, j.conversationCount || 0); offset += 100) {
        const page = await api('', { ...filters(), offset, limit: Math.min(100, state.conversationLimit - offset) });
        j.conversations.push(...(page.conversations || []));
      }
      if (generation !== state.generation) return;
      options('cdCampaign', j.campaigns || [], 'All captured campaigns'); options('cdMode', (j.modes || []).filter(x => x !== 'other'), 'All types', [['other','Other LLM activity']]);
      const host = el('cdConversations'), previous = new Map([...host.children].map(x => [x.dataset.conversationKey, x])), buttons = [];
      for (const c of j.conversations || []) {
        let b = previous.get(c.conversationKey);
        if (!b) {
          b = make('button', undefined, 'cdConversation'); b.dataset.conversationKey = c.conversationKey;
          b.append(make('strong'), make('span', '', 'hint'), make('span', '', 'hint'), make('span', '', 'cdPreview'));
          b.onclick = () => { state.key = c.conversationKey; state.generation++; state.responseLimit = 40; clearResponseSelection(); el('cdConversationTitle').textContent = b.firstChild.textContent; loadConversationDiagnostics(); };
        }
        b.setAttribute('aria-pressed', String(c.conversationKey === state.key));
        [c.title, `${label(c.mode)} · ${c.responseCount} ${c.responseCount === 1 ? 'response' : 'responses'}`, c.lastUtc ? new Date(c.lastUtc).toLocaleString() : 'Time not recorded', c.preview || ''].forEach((text, i) => setText(b.children[i], text));
        buttons.push(b);
      }
      reconcileChildren(host, buttons);
      if (!(j.conversations || []).length) host.append(make('p', 'No captures yet. Select a campaign to inspect retained historical audits, or start a new conversation.', 'hint'));
      el('cdMoreConversations').hidden = (j.conversations || []).length >= (j.conversationCount || 0);
      setText(el('cdStatusText'), j.error ? 'Capture problem: ' + j.error : `${j.conversationCount || 0} conversations match. Select a response to inspect its steps.`);
      await responses(true);
    } catch (e) { el('cdStatusText').textContent = e.message; }
    finally {
      state.busy = false; clearTimeout(state.timer);
      if (state.refreshPending) { state.refreshPending = false; queueMicrotask(loadConversationDiagnostics); return; }
      state.timer = setTimeout(() => { if (el('conversationdiagnostics')?.classList.contains('active') && el('cdLive')?.checked) loadConversationDiagnostics(); }, 5000);
    }
  };
  window.resetConversationDiagnostics = () => { state.key = ''; state.generation++; state.responseLimit = 40; state.conversationLimit = 40; clearResponseSelection(); el('cdConversationTitle').textContent = 'Select a conversation'; loadConversationDiagnostics(); };
  window.scheduleConversationSearch = () => { clearTimeout(state.searchTimer); state.searchTimer = setTimeout(resetConversationDiagnostics, 300); };
  window.moreDiagnosticConversations = () => { state.conversationLimit += 40; loadConversationDiagnostics(); };
  window.moreDiagnosticResponses = () => { state.responseLimit += 40; responses(); };
  window.exportConversationDiagnostics = () => { if (state.key) window.location.assign(base + '/export?' + new URLSearchParams({ conversationKey: state.key })); };
  window.clearConversationDiagnostics = async () => {
    const ids = state.responses.filter(x => !x.legacy && x.status !== 'in_progress' && !x.captureInProgress).slice(0, 500).map(x => x.traceId);
    if (!ids.length || !confirm(`Clear the ${ids.length} completed diagnostic captures currently loaded? Conversation history and game state will stay intact.`)) return;
    try { await api('/clear', {}, { traceIds: ids, confirmation: 'clear selected diagnostic captures' }); resetConversationDiagnostics(); }
    catch (e) { el('cdStatusText').textContent = e.message; }
  };
})();
