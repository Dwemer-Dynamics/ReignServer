// Exact emitted HTML and real retained diagnostic evidence; every request is intercepted.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [htmlPath, fixturePath, out] = process.argv.slice(2);
if (!out) throw new Error('Provide exact Control Center HTML, diagnostics browser-fixture.json and evidence directory.');
const html = await fs.readFile(htmlPath, 'utf8'), fixture = JSON.parse(await fs.readFile(fixturePath, 'utf8'));
await fs.mkdir(out, { recursive: true });
const browser = await chromium.launch({ channel: process.env.REIGN_BROWSER_CHANNEL || 'msedge', headless: true });
const report = { schema: 'reign-conversation-diagnostics-ui-v1', htmlHash: crypto.createHash('sha256').update(html).digest('hex'), fixturePath, cases: [], errors: [], requests: [] };
try {
  for (const width of [1672, 1024, 600]) {
    const page = await browser.newPage({ viewport: { width, height: 941 } });
    page.on('pageerror', e => report.errors.push(e.message));
    let enabled = false, cleared = false, many = false, fail = false;
    let holdTrace = false, releaseTrace, traceSeen;
    const meta = fixture.trace.response, key = meta.conversationKey;
    let responseRows = [meta], updateSteps = false;
    await page.route('**/*', async route => {
      const req = route.request(), url = new URL(req.url()), q = url.searchParams;
      report.requests.push({ method: req.method(), path: url.pathname, query: url.search });
      if (url.pathname === '/preview') return route.fulfill({ contentType: 'text/html', body: html });
      let data = { ok: true, campaigns: [], models: [], cases: [], results: [], autoOpenBrowser: true };
      if (url.pathname.startsWith('/api/diagnostics/conversations')) {
        if (fail) return route.fulfill({ status: 503, contentType: 'application/json', body: '{"ok":false,"error":"Fixture storage unavailable"}' });
        const suffix = url.pathname.slice('/api/diagnostics/conversations'.length);
        if (!suffix) {
          if (q.get('conversationKey')) {
            const offset = Number(q.get('offset') || 0), limit = Number(q.get('limit') || 40);
            data = { ...fixture.responseList, responses: cleared ? [] : responseRows.slice(offset, offset + limit), responseCount: cleared ? 0 : responseRows.length };
          }
          else {
            const real = fixture.list.conversations.find(c => c.conversationKey === key);
            let conversations = cleared ? [] : [real];
            if (many) conversations = [...conversations, ...Array.from({ length: 125 }, (_, i) => ({ ...real, conversationKey: i.toString(16).padStart(32, '0'), title: `Fixture ${i}` }))];
            if (q.get('search') && q.get('search') !== 'Corein') conversations = [];
            const offset = Number(q.get('offset') || 0), limit = Number(q.get('limit') || 40);
            data = { ...fixture.list, conversations: conversations.slice(offset, offset + limit), conversationCount: conversations.length };
          }
        } else if (suffix === '/capture') {
          if (req.method() === 'POST') enabled = req.postDataJSON().enabled;
          data = { ok: true, enabled, storageBytes: 45590, activeCaptures: 0, error: '', survivesRestart: true, retention: 'until_cleared' };
        } else if (suffix === '/trace') {
          if (holdTrace) {
            holdTrace = false;
            traceSeen();
            await new Promise(resolve => { releaseTrace = resolve; });
          }
          // Allow rendering between replies, as real trace requests do.
          await new Promise(resolve => setTimeout(resolve, 40));
          data = updateSteps ? { ...fixture.trace, steps: [
            { stepId: 'later-visible-step', title: 'Additional recorded step', status: 'completed', evidence: [] },
            ...fixture.trace.steps.map(s => s.title === 'Large complete evidence' ? { ...s, status: 'completed', durationMs: 50, explanation: 'Capture completed.\n'.repeat(12) } : s)
          ] } : fixture.trace;
        } else if (suffix === '/payload') {
          const payload = fixture.payloads[q.get('payloadId')]; assert.ok(payload, 'Unknown production payload reference');
          const offset = Number(q.get('offset') || 0); let end = Math.min(payload.content.length, offset + Number(q.get('length') || 12000));
          if (end < payload.content.length && /[\uD800-\uDBFF]/.test(payload.content[end - 1])) end--;
          data = { ok: true, content: payload.content.slice(offset, end), nextOffset: end, hasMore: end < payload.content.length, metadata: payload.metadata };
        } else if (suffix === '/clear') {
          assert.deepEqual(req.postDataJSON().traceIds, [meta.traceId]); assert.equal(req.postDataJSON().confirmation, 'clear selected diagnostic captures');
          cleared = true; data = { ok: true, deletedCaptures: 1 };
        } else throw new Error('Unexpected diagnostic request: ' + suffix);
      }
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) });
    });
    await page.goto('http://reign-preview.invalid/preview');
    await page.evaluate(() => selectControlCenterTab('conversationdiagnostics'));
    await page.locator('#cdLive').uncheck();
    await page.locator('#cdConversations .cdConversation').first().waitFor();
    assert.equal(await page.locator('#cdCapture').isChecked(), false);
    await page.locator('#cdCapture').check();
    await page.waitForFunction(() => document.getElementById('cdCaptureStatus').textContent.includes('Full capture ON'));
    await page.locator('#cdConversations .cdConversation').first().click();
    await page.locator('.cdResponse > summary').click();
    await page.locator('.cdStep').first().waitFor();
    assert.equal(await page.locator('.cdResponse > summary').textContent().then(x => x.includes('tournament victories')), true);
    // Keyboard opens a step; subsequent evidence remains independently expandable.
    const repair = page.locator('.cdStep > summary').filter({ hasText: /^Repair the answer format/ }).first();
    await repair.focus(); await page.keyboard.press('Enter');
    const negotiation = page.locator('.cdStep > summary').filter({ hasText: /^Check negotiation metadata/ }).first();
    await negotiation.click();
    const rejected = page.locator('.cdStep').filter({ has: page.locator(':scope > summary', { hasText: 'Check negotiation metadata · rejected' }) });
    await rejected.locator(':scope > summary').click();
    assert.ok((await rejected.textContent()).includes('proposalDecisions'));
    await page.locator('#conversationdiagnostics').screenshot({ path: path.join(out, `${width}-repairs.png`) });
    // Large payload crosses Unicode/page boundaries and must load without truncation.
    const large = page.locator('.cdStep').filter({ has: page.locator(':scope > summary', { hasText: /^Large complete evidence/ }) });
    await large.locator(':scope > summary').click();
    await large.locator('.cdEvidence > summary').click();
    await large.locator('.cdRaw').waitFor();
    const expected = Object.values(fixture.payloads).find(p => p.content.includes('fictional-royal-plan')).content;
    assert.equal(await large.locator('.cdRaw pre').textContent(), expected);
    assert.ok(report.requests.some(r => r.path.endsWith('/payload') && r.query.includes('offset=11999')));
    // Refresh retains response, nested step and loaded evidence state.
    await page.evaluate(() => loadConversationDiagnostics());
    assert.equal(await repair.locator('..').getAttribute('open'), '');
    assert.equal(await large.locator('.cdEvidence').getAttribute('open'), '');
    assert.equal(await large.locator('.cdRaw pre').textContent(), expected);
    assert.equal(await page.evaluate(() => typeof unsafe), 'undefined');
    await page.locator('#conversationdiagnostics').screenshot({ path: path.join(out, `${width}-payload.png`) });
    const bounds = await page.evaluate(() => ({ width: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }));
    assert.ok(bounds.scroll <= bounds.width + 1, JSON.stringify(bounds));
    // Several open replies expose re-parenting between asynchronous trace requests.
    responseRows = [{ ...meta, traceId: 'earlier-1' }, { ...meta, traceId: 'earlier-2' }, meta, { ...meta, traceId: 'later-1' }];
    await page.evaluate(() => loadConversationDiagnostics());
    for (const id of ['earlier-1', 'earlier-2', 'later-1']) {
      const response = page.locator(`.cdExchange[data-trace-id="${id}"] .cdResponse`);
      await response.locator(':scope > summary').click(); await response.locator('.cdStep').first().waitFor();
    }
    const readingResponse = page.locator(`.cdExchange[data-trace-id="${meta.traceId}"]`);
    const readingRaw = readingResponse.locator('.cdRaw');
    await readingRaw.locator(':scope > summary').click();
    await readingRaw.evaluate(node => {
      const summary = node.firstElementChild, pre = node.querySelector('pre');
      summary.focus(); summary.scrollIntoView({ block: 'center' }); pre.scrollTop = 80;
      const selection = getSelection(), range = document.createRange();
      range.setStart(pre.firstChild, 0); range.setEnd(pre.firstChild, 30);
      selection.removeAllRanges(); selection.addRange(range);
      window.cdReadingProbe = { node, summary, pre, text: selection.toString() };
    });
    const settle = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const readingPosition = () => page.evaluate(() => {
      const p = window.cdReadingProbe;
      return { y: scrollY, top: p.summary.getBoundingClientRect().top, connected: p.node.isConnected,
        focus: document.activeElement === p.summary, text: getSelection().toString(), payloadScroll: p.pre.scrollTop,
        open: p.node.open && p.node.closest('.cdEvidence').open && p.node.closest('.cdStep').open && p.node.closest('.cdResponse').open };
    });
    const assertReading = (before, after, operation, grows = false) => {
      assert.equal(after.connected, true, operation + ': evidence node must remain connected');
      assert.equal(after.open, true, operation + ': nested details must remain expanded');
      assert.equal(after.focus, true, operation + ': keyboard focus must remain on the reading control');
      assert.equal(after.text, before.text, operation + ': selected text must survive');
      assert.equal(after.payloadScroll, before.payloadScroll, operation + ': payload scroll must survive');
      assert.ok(Math.abs(after.top - before.top) <= 2, operation + ': reading position moved ' + JSON.stringify({ before, after }));
      if (!grows) assert.ok(Math.abs(after.y - before.y) <= 2, operation + ': page scroll changed');
    };
    await settle();
    let beforeReading = await readingPosition();
    assert.ok(beforeReading.y > 1000 && beforeReading.text.length > 0, 'Exercise a scrolled page with a real selection');
    await page.evaluate(() => loadConversationDiagnostics()); await settle();
    assertReading(beforeReading, await readingPosition(), 'Unchanged refresh');
    // Real five-second live refresh uses the same stable update, including focus.
    await page.evaluate(() => { document.getElementById('cdLive').checked = true; });
    const liveTrace = page.waitForResponse(r => new URL(r.url()).pathname.endsWith('/conversations/trace'));
    await liveTrace;
    await page.evaluate(() => { document.getElementById('cdLive').checked = false; });
    await page.waitForTimeout(500); await settle();
    assertReading(beforeReading, await readingPosition(), 'Live timer refresh');
    // A user may keep scrolling while the server is slow: never restore an old snapshot.
    const slowTrace = new Promise(resolve => { traceSeen = resolve; }); holdTrace = true;
    await page.evaluate(() => { window.cdPendingRefresh = loadConversationDiagnostics(); }); await slowTrace;
    await page.evaluate(() => scrollBy(0, 120)); await settle(); beforeReading = await readingPosition();
    releaseTrace(); await page.evaluate(() => window.cdPendingRefresh); await settle();
    assertReading(beforeReading, await readingPosition(), 'User scroll during delayed refresh');
    // Changed step metadata, new steps and new responses above the reader retain loaded evidence.
    updateSteps = true; responseRows.unshift({ ...meta, traceId: 'newly-discovered' });
    const payloadRequests = report.requests.filter(r => r.path.endsWith('/payload')).length;
    await page.evaluate(() => loadConversationDiagnostics()); await settle();
    assertReading(beforeReading, await readingPosition(), 'New response and changed steps', true);
    assert.equal(await readingRaw.locator('pre').textContent(), expected);
    assert.equal(report.requests.filter(r => r.path.endsWith('/payload')).length, payloadRequests, 'Refresh must reuse immutable loaded evidence');
    assert.ok((await readingResponse.textContent()).includes('Additional recorded step'));
    assert.ok((await readingResponse.textContent()).includes('Large complete evidence · completed'));
    await page.screenshot({ path: path.join(out, `${width}-reading-position.png`) });
    // Paging must not rebuild already open trace/evidence trees.
    responseRows.push(...Array.from({ length: 40 }, (_, i) => ({ ...meta, traceId: `page-${i}` })));
    await page.evaluate(() => loadConversationDiagnostics()); await settle(); beforeReading = await readingPosition();
    await page.evaluate(() => { document.getElementById('cdMoreResponses').click(); });
    await page.waitForFunction(() => document.querySelectorAll('.cdExchange').length === 45);
    await page.waitForTimeout(500); await settle();
    assertReading(beforeReading, await readingPosition(), 'Load more responses');
    // The conversation rail has its own independent scroll and keyboard focus.
    many = true; await page.evaluate(() => loadConversationDiagnostics());
    const railBefore = await page.evaluate(() => {
      const rail = document.querySelector('.cdRail'), button = document.querySelectorAll('.cdConversation')[12];
      button.focus({ preventScroll: true }); rail.scrollTop = 900;
      window.cdRailProbe = { rail, button }; return rail.scrollTop;
    });
    assert.ok(railBefore > 0);
    await page.evaluate(() => loadConversationDiagnostics()); await settle();
    assert.deepEqual(await page.evaluate(() => ({ scroll: cdRailProbe.rail.scrollTop, focus: document.activeElement === cdRailProbe.button })), { scroll: railBefore, focus: true });
    // Restore the single production trace for existing filter/clear contract checks.
    many = false; updateSteps = false; responseRows = [meta];
    await page.evaluate(() => loadConversationDiagnostics());
    // A response arriving after a filter reset must not restore the old selection.
    const traceWait = new Promise(resolve => { traceSeen = resolve; });
    holdTrace = true;
    await page.evaluate(() => { void loadConversationDiagnostics(); });
    await traceWait;
    await page.locator('#cdSearch').fill('missing');
    await page.waitForFunction(() => document.getElementById('cdConversationTitle').textContent === 'Select a conversation');
    releaseTrace();
    await page.waitForFunction(() => document.getElementById('cdConversations').textContent.includes('No captures yet'));
    assert.equal(await page.locator('.cdExchange').count(), 0);
    assert.equal(await page.locator('#cdExport').isDisabled(), true);
    assert.equal(await page.locator('#cdClear').isDisabled(), true);
    assert.equal(await page.locator('#cdMoreResponses').isVisible(), false);
    await page.locator('#conversationdiagnostics').screenshot({ path: path.join(out, `${width}-empty.png`) });
    many = true; await page.locator('#cdSearch').fill('');
    await page.waitForFunction(() => document.querySelectorAll('.cdConversation').length === 40);
    for (let i = 0; i < 3; i++) { await page.locator('#cdMoreConversations').click(); await page.waitForFunction(n => document.querySelectorAll('.cdConversation').length === n, Math.min(126, 80 + 40 * i)); }
    assert.equal(await page.locator('.cdConversation').count(), 126);
    assert.equal(await page.locator('#cdMoreConversations').isVisible(), false);
    many = false; await page.evaluate(() => resetConversationDiagnostics());
    await page.locator('#cdConversations .cdConversation').first().click();
    await page.locator('#cdClear').waitFor({ state: 'visible' });
    page.once('dialog', dialog => dialog.accept()); await page.locator('#cdClear').click();
    await page.waitForFunction(() => document.getElementById('cdConversations').textContent.includes('No captures yet'));
    fail = true; await page.evaluate(() => loadConversationDiagnostics());
    assert.ok((await page.locator('#cdStatusText').textContent()).includes('Fixture storage unavailable'));
    await page.locator('#conversationdiagnostics').screenshot({ path: path.join(out, `${width}-error.png`) });
    report.cases.push({ width, nestedRepairs: true, keyboard: true, fullPagedPayload: true, refreshPreservesExpansion: true, refreshPreservesReadingPosition: true, liveTimer: true, delayedRefreshUserScroll: true, newResponseAndChangedSteps: true, focusAndTextSelection: true, responsePaging: true, railScrollAndFocus: true, filterResetDiscardsLateRefresh: true, captureToggle: true, search: true, conversationPaging: true, selectedClear: true, errorsVisible: true, noOverflow: true });
    await page.close();
  }
  assert.deepEqual(report.errors, []);
  assert.ok(report.requests.every(r => r.path !== '/api/settings' || r.method === 'GET'));
  await fs.writeFile(path.join(out, 'ui-report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ok: true, widths: report.cases.length, screenshots: report.cases.length * 5, report: path.resolve(out, 'ui-report.json') }));
} catch (error) {
  report.failure = error.stack; await fs.writeFile(path.join(out, 'ui-report.json'), JSON.stringify(report, null, 2)); throw error;
} finally { await browser.close(); }
