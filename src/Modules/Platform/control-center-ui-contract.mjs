// Reuses the exact HTML emitted by the existing interaction_architecture Lab suite.
// Browser traffic is fulfilled locally; page loaders are observed at their dispatch boundary.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [fixtureDirectory, outputDirectory] = process.argv.slice(2);
if (!fixtureDirectory || !outputDirectory) throw new Error('Provide the Lab fixture directory and evidence output directory.');
const html = await fs.readFile(path.join(fixtureDirectory, 'control-center.html'), 'utf8');
const palette = JSON.parse(await fs.readFile(path.resolve(process.env.REIGN_WORKSPACE || process.cwd(), 'ReignBeta/artwork/ui-modern-style-kit/palette.json'), 'utf8')).tokens;
const sourceHash = crypto.createHash('sha256').update(html).digest('hex');
const main = ['general','options','campaigns','llm','models','router','images','characters','memory','prompting','castlechat','diagnostic-tools'];
const diagnostics = ['diagnostics','conversationdiagnostics','logging','logs','audit','dialogueaudit','history','npclab','directorlab','worldtest','testlab','livebridge','verification'];
const loaders = {
  general: [], options: [], models: [], router: [], logging: [], conversationdiagnostics: ['loadConversationDiagnostics'],
  prompting: ['loadPrompts'], castlechat: ['loadCastleChatPrompts'], campaigns: ['loadCampaignManager'],
  images: ['loadPrompts','loadPortraitDerivativeStatus','loadSharedPortraitCatalog','loadSharedPortraitGenerationStatus'],
  memory: ['loadEmbeddingStatus'], llm: ['loadPromptCacheStatus'], logs: ['loadCampaigns','loadLogs'],
  audit: ['loadCampaigns','loadAudit'], dialogueaudit: ['loadCampaigns','loadNpcDialogueAudit'],
  history: ['loadWorldHistory','loadRebellions','loadNegotiations'], npclab: ['inspectNpcLabMemory'],
  directorlab: ['loadDirectorStatus'], worldtest: ['initializeWorldTest'], testlab: ['loadCampaigns','loadTestCases'],
  verification: ['loadVerificationStatus','loadVerificationResults'], livebridge: ['liveBridgeRuntime','liveBridgeReadiness'],
  diagnostics: ['loadDiagnostics'], characters: ['loadCampaigns','loadCharacterRoster']
};
await fs.mkdir(outputDirectory, { recursive: true });
const browser = await chromium.launch({ channel: process.env.REIGN_BROWSER_CHANNEL || 'msedge', headless: true });
const results = [], errors = [], requests = [], styleResults = [];
try {
  const page = await browser.newPage();
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => { window.setInterval = () => 0; });
  await page.route('**/*', async route => {
    const url = route.request().url();
    requests.push({ url, method: route.request().method() });
    await route.fulfill(url === 'http://reign-preview.invalid/preview'
      ? { contentType: 'text/html', body: html }
      : { contentType: 'application/json', body: JSON.stringify({ ok: true, autoOpenBrowser: true, campaigns: [], models: [], cases: [], results: [] }) });
  });
  await page.goto('http://reign-preview.invalid/preview');
  await page.waitForLoadState('networkidle');
  const permittedColors = await page.evaluate(tokens => {
    const probe = document.createElement('span'); probe.style.display = 'none'; document.body.append(probe);
    const colors = Object.values(tokens).map(value => { probe.style.color = value; return getComputedStyle(probe).color; });
    probe.remove(); return colors.concat('rgba(0, 0, 0, 0)');
  }, palette);
  async function auditStyle(label) {
    const audit = await page.evaluate(permitted => {
      const failures = []; let inspected = 0;
      for (const element of [document.body, ...document.body.querySelectorAll('*')]) {
        if (!element.getClientRects().length) continue;
        const style = getComputedStyle(element);
        if (style.visibility === 'hidden') continue;
        const name = `${element.tagName}#${element.id}.${String(element.className)}`;
        for (const property of ['color', 'backgroundColor']) if (!permitted.includes(style[property])) failures.push({ name, property, value: style[property] });
        for (const side of ['Top','Right','Bottom','Left']) {
          if (parseFloat(style[`border${side}Width`]) && !permitted.includes(style[`border${side}Color`])) failures.push({ name, property: `border${side}Color`, value: style[`border${side}Color`] });
        }
        for (const property of ['boxShadow','textShadow','backgroundImage']) if (style[property] !== 'none') failures.push({ name, property, value: style[property] });
        inspected++;
      }
      return { inspected, failures };
    }, permittedColors);
    styleResults.push({ label, ...audit });
  }
  assert.deepEqual(await page.locator('#mainNavigation .tab').evaluateAll(xs => xs.map(x => x.dataset.tab)), main);
  assert.deepEqual(await page.locator('#diagnosticNavigation .tab').evaluateAll(xs => xs.map(x => x.dataset.tab)), diagnostics);
  assert.equal(await page.locator('#diagnosticNavigation').isVisible(), false);
  assert.equal(await page.locator('.page.active').getAttribute('id'), 'general');
  assert.equal(await page.evaluate(() => typeof runDirectorSample), 'undefined');
  assert.equal(html.includes('/relationships/simulation/'), false);
  await page.evaluate(names => {
    window.navigationLoads = [];
    for (const name of names) {
      if (typeof window[name] !== 'function') throw new Error(`Missing production loader: ${name}`);
      window[name] = async () => { window.navigationLoads.push(name); };
    }
  }, [...new Set(Object.values(loaders).flat())]);
  // Navigation must never recreate page DOM or discard unsaved settings/test drafts.
  await page.locator('#host').fill('unsaved-host');
  await page.evaluate(() => { document.getElementById('testCaseEditor').value = 'unsaved replay case'; });
  for (const width of [1672, 1024, 600]) {
    await page.setViewportSize({ width, height: 941 });
    for (const target of [...main.slice(0, -1), ...diagnostics]) {
      const diagnostic = diagnostics.includes(target);
      if (diagnostic && !(await page.locator('#diagnosticNavigation').isVisible()))
        await page.locator('#mainNavigation [data-tab="diagnostic-tools"]').click();
      await page.evaluate(() => { window.navigationLoads = []; });
      const button = page.locator(`${diagnostic ? '#diagnosticNavigation' : '#mainNavigation'} [data-tab="${target}"]`);
      await button.click();
      await page.waitForFunction(() => document.querySelectorAll('.page.active').length === 1);
      assert.deepEqual(await page.evaluate(() => window.navigationLoads), loaders[target]);
      assert.equal(await page.locator('.page.active').getAttribute('id'), target);
      assert.equal(await page.locator('#diagnosticNavigation').isVisible(), diagnostic);
      assert.equal(await page.locator('#mainNavigation .active').getAttribute('data-tab'), diagnostic ? 'diagnostic-tools' : target);
      assert.equal(await button.getAttribute('aria-pressed'), 'true');
      const geometry = await page.locator('#mainNavigation .tab, #diagnosticNavigation:not([hidden]) .tab').evaluateAll(buttons => buttons.map(button => {
        const rect = button.getBoundingClientRect();
        return { label: button.textContent.trim(), left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, height: rect.height };
      }));
      for (const rect of geometry) assert.ok(rect.left >= 0 && rect.right <= width && rect.height >= 36, JSON.stringify(rect));
      for (let a = 0; a < geometry.length; a++) for (let b = a + 1; b < geometry.length; b++) {
        const x = geometry[a], y = geometry[b];
        assert.ok(x.right <= y.left || y.right <= x.left || x.bottom <= y.top || y.bottom <= x.top, 'Navigation hit targets overlap');
      }
      await auditStyle(`${width}-${target}`);
      await page.locator('#mainNavigation').scrollIntoViewIfNeeded();
      await page.evaluate(() => window.scrollTo(0, document.getElementById('mainNavigation').offsetTop - 8));
      const screenshot = `${width}-${target}.png`;
      await page.screenshot({ path: path.join(outputDirectory, screenshot) });
      results.push({ width, target, screenshot, loaderDispatch: loaders[target], visibleButtons: geometry.length });
    }
  }
  await page.locator('#mainNavigation [data-tab="general"]').click();
  assert.equal(await page.locator('#host').inputValue(), 'unsaved-host');
  const hub = page.locator('#mainNavigation [data-tab="diagnostic-tools"]');
  await hub.focus();
  await page.keyboard.press('Enter');
  assert.equal(await page.locator('.page.active').getAttribute('id'), 'verification');
  await page.locator('#diagnosticNavigation [data-tab="testlab"]').focus();
  await page.keyboard.press('Space');
  assert.equal(await page.locator('#testCaseEditor').inputValue(), 'unsaved replay case');
  await page.evaluate(() => selectControlCenterTab('unknown-page'));
  assert.equal(await page.locator('.page.active').getAttribute('id'), 'testlab');
  await page.locator('#mainNavigation [data-tab="general"]').click();
  await page.keyboard.press('Tab');
  assert.equal(await page.evaluate(() => !!document.activeElement.closest('#diagnosticNavigation')), false);
  const colors = await hub.evaluate(button => ({ background: getComputedStyle(button).backgroundColor, color: getComputedStyle(button).color }));
  assert.deepEqual(colors, { background: 'rgb(26, 25, 23)', color: 'rgb(197, 189, 175)' });
  await hub.hover();
  const hover = await hub.evaluate(button => ({ background: getComputedStyle(button).backgroundColor,
    image: getComputedStyle(button).backgroundImage, shadow: getComputedStyle(button).boxShadow }));
  assert.deepEqual(hover, { background: 'rgb(26, 25, 23)', image: 'none', shadow: 'none' });
  await hub.click();
  const active = await hub.evaluate(button => ({ background: getComputedStyle(button).backgroundColor,
    image: getComputedStyle(button).backgroundImage, shadow: getComputedStyle(button).boxShadow }));
  assert.deepEqual(active, { background: 'rgb(35, 35, 35)', image: 'none', shadow: 'none' });
  // Exercise production renderers with deterministic local data, including states absent from empty pages.
  for (const width of [1672, 1024, 600]) {
    await page.setViewportSize({ width, height: 941 });
    await page.evaluate(() => {
      selectControlCenterTab('characters');
      characterEditorState.heroId = 'fixture-noble';
      characterEditorState.data = { documents: { traits: {
        visibleBannerlordTraits: { valor: 2, honor: -1, mercy: 0, generosity: 1, calculating: -2 },
        courtVirtues: { compassion: 90, boldness: 10, honor: 50, loyalty: 75, responsibility: 40, courage: 85, judgment: 55 }
      } }, socialStanding: {
        activeRumors: [{ classification: 'positive', label: 'Generous host', description: 'A remembered act of kindness.', world_day: 10 },
          { classification: 'negative', label: 'Broken promise', description: 'An unresolved obligation.', world_day: 9 }],
        activeReputations: [{ classification: 'neutral', label: 'Well traveled', description: 'Known in several towns.' }]
      } };
      characterEditorState.section = 'traits';
      document.getElementById('characterEditorWorkspace').style.display = 'block';
      document.getElementById('characterEditorEmpty').style.display = 'none';
      renderCharacterEditor();
      document.getElementById('characterEditorPanel').innerHTML = renderCourtVirtues() + renderCharacterSocialStanding();
    });
    await auditStyle(`${width}-populated-character`);
    await page.locator('#characterEditorPanel').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(outputDirectory, `${width}-populated-character.png`), fullPage: true });
    await page.evaluate(() => {
      selectControlCenterTab('worldtest');
      renderWorldTest({ campaignId: 'preview', timelineId: 'main', overallStatus: 'healthy', firstObservedDay: 1, latestObservedDay: 8,
        kingdomLeaders: { status: 'warning', activeKingdoms: 2, matrix: [
          { observerId: 'a', targetId: 'b', observerName: 'Ruler A', targetName: 'Ruler B', effectiveAttitude: 75, band: 'friendly' },
          { observerId: 'b', targetId: 'a', observerName: 'Ruler B', targetName: 'Ruler A', effectiveAttitude: -65, band: 'hostile' }
        ] }, pipeline: { status: 'error', anomalies: ['Fixture failure state'] } });
    });
    await auditStyle(`${width}-populated-world`);
    await page.locator('#worldTestKingdomLeadersCard').scrollIntoViewIfNeeded();
    await page.screenshot({ path: path.join(outputDirectory, `${width}-populated-world.png`) });
  }
  await page.evaluate(() => selectControlCenterTab('general'));
  await page.locator('#host').focus();
  await auditStyle('focused-input');
  assert.equal(await page.locator('#autoOpenBrowser').isChecked(), true);
  assert.equal(await page.locator('#autoOpenBrowser').isDisabled(), true);
  const disabledColor = await page.locator('#autoOpenBrowser').evaluate(element => getComputedStyle(element).color);
  assert.equal(disabledColor, 'rgb(74, 73, 69)');
  await page.evaluate(() => selectControlCenterTab('logging'));
  const checkbox = page.locator('#enableRequestLogging');
  await checkbox.check(); assert.equal(await checkbox.isChecked(), true);
  await auditStyle('checked-checkbox');
  await checkbox.uncheck(); assert.equal(await checkbox.isChecked(), false);
  await page.evaluate(() => selectControlCenterTab('campaigns'));
  const fileControl = await page.locator('#campaignImportFile').evaluate(element => ({
    background: getComputedStyle(element, '::file-selector-button').backgroundColor,
    color: getComputedStyle(element, '::file-selector-button').color }));
  assert.deepEqual(fileControl, { background: 'rgb(26, 25, 23)', color: 'rgb(197, 189, 175)' });
  await fs.writeFile(path.join(outputDirectory, 'style-audit.json'), JSON.stringify(styleResults, null, 2));
  assert.deepEqual(styleResults.flatMap(result => result.failures.map(failure => ({ label: result.label, ...failure }))), [], 'Off-palette or legacy decoration');
  assert.deepEqual(errors, []);
  const report = { schema: 'reign-control-center-navigation-ui-v1', ok: true, isolated: true, sourceHash,
    source: path.resolve(fixtureDirectory), results, styleResults, palette, errors, interceptedRequests: requests,
    rememberedPage: true, keyboardAccess: true, unsavedDraftsPreserved: true, retiredClientRemoved: true,
    coverage: 'Navigation/loader dispatch and rendered empty-state matrix. Providers, campaigns and backend tool execution are outside this fixture.',
    fidelityScope: 'All Control Center CSS uses frozen palette tokens, with computed-style audits, populated renderer fixtures and control-state checks; existing logo/portrait bytes, native shells and Bannerlord adapters are unchanged.' };
  await fs.writeFile(path.join(outputDirectory, 'ui-report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ok: true, matrixCases: results.length, report: path.resolve(outputDirectory, 'ui-report.json') }));
} finally { await browser.close(); }
