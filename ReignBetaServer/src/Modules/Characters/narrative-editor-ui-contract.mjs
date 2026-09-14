// Exercise the exact provider-free HTML emitted by character_narrative.
// All browser traffic is intercepted. No installed server, campaign or provider is contacted.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [fixtureDirectory, outputDirectory] = process.argv.slice(2);
if (!fixtureDirectory || !outputDirectory) throw new Error('Provide the Verification Lab fixture directory and evidence output directory.');
const html = await fs.readFile(path.join(fixtureDirectory, 'control-center.html'), 'utf8');
const fixture = JSON.parse(await fs.readFile(path.join(fixtureDirectory, 'fixture.json'), 'utf8'));
await fs.mkdir(outputDirectory, { recursive: true });
const browser = await chromium.launch({ channel: process.env.REIGN_BROWSER_CHANNEL || 'msedge', headless: true });
const results = [];
try {
  const page = await browser.newPage();
  await page.route('**/*', async route => route.fulfill(route.request().url() === 'http://reign-preview.invalid/preview'
    ? { contentType: 'text/html', body: html } : { contentType: 'application/json', body: '{}' }));
  await page.goto('http://reign-preview.invalid/preview');
  await page.evaluate(fixture => {
    document.body.innerHTML = '<main id="characterEditorPanel" style="max-width:1180px;margin:16px auto"></main>';
    characterEditorState.data = { documents: { narrative: fixture }, records: { dynamicCharacteristics: [] } };
    characterEditorState.section = 'interests';
    characterEditorState.dirty = new Set();
    window.renderCharacterEditor = () => renderCharacterEditorPanel();
    renderCharacterEditorPanel();
  }, fixture);
  for (const width of [1672, 1024, 600]) {
    await page.setViewportSize({ width, height: 941 });
    for (const state of ['current', 'expanded', 'long-text', 'history']) {
      await page.evaluate(({ fixture, state }) => {
        characterEditorState.data.documents.narrative = structuredClone(fixture);
        characterEditorState.data.records.dynamicCharacteristics = [];
        if (state === 'long-text') characterEditorState.data.documents.narrative.items[0].description = ('A meaningful personal interest with room for a full explanation. ').repeat(15);
        if (state === 'history') characterEditorState.data.records.dynamicCharacteristics = [
          { status: 'superseded', category: 'narrative_development', text: 'An earlier interpretation remains in the record.', payload_json: JSON.stringify({ narrativeDevelopment: { decision: 'accepted', influence: 8, worldDay: 20 } }) },
          { status: 'active', category: 'narrative_development', text: 'A later considered change shapes present choices.', payload_json: JSON.stringify({ narrativeDevelopment: { decision: 'accepted', influence: 4, worldDay: 80 } }) },
          { status: 'rejected', category: 'narrative_development', text: 'One conversation has not changed this concern.', payload_json: JSON.stringify({ narrativeDevelopment: { decision: 'pending', influence: 3, worldDay: 90 } }) }
        ];
        renderCharacterEditorPanel();
        if (state !== 'current') document.querySelectorAll('.narrativeInterest').forEach(x => x.open = true);
      }, { fixture, state });
      const layout = await page.evaluate(() => ({
        overflow: document.documentElement.scrollWidth > innerWidth + 1,
        cards: [...document.querySelectorAll('.narrativeInterest')].map(x => ({
          overflow: x.scrollWidth > x.clientWidth + 1,
          background: getComputedStyle(x).backgroundColor,
          border: getComputedStyle(x).borderTopColor
        })),
        defining: document.querySelectorAll('.narrativeInterest .pill').length,
        inputs: [...document.querySelectorAll('.narrativeInterest input')].map(x => ({ min: x.min, max: x.max, step: x.step }))
      }));
      if (layout.overflow || layout.cards.some(x => x.overflow || x.border !== 'rgb(126, 106, 77)')
        || layout.defining !== 3 || layout.inputs.some(x => x.min !== '1' || x.max !== '10' || x.step !== '1'))
        throw new Error('Narrative layout contract failed: ' + JSON.stringify({ width, state, layout }));
      await page.evaluate(() => scrollTo(0, 0));
      await page.screenshot({ path: path.join(outputDirectory, `${width}-${state}-first.png`) });
      await page.locator('.narrativeInterest').last().scrollIntoViewIfNeeded();
      await page.locator('.narrativeInterest').last().screenshot({ path: path.join(outputDirectory, `${width}-${state}-last.png`) });
      if (state === 'history') {
        const records = page.locator('.narrativeEditor > article');
        if (await records.count() !== 3 || !(await records.allTextContents()).every((text, index) =>
          text.includes(['Earlier version', 'Accepted development', 'Awaiting more evidence'][index])))
          throw new Error('Development history labels or records are missing.');
        await records.last().scrollIntoViewIfNeeded();
        await page.screenshot({ path: path.join(outputDirectory, `${width}-history-records.png`) });
      }
      results.push({ width, state, layout, passed: true });
    }
  }
  await page.evaluate(() => { characterEditorState.section = 'story'; renderCharacterEditorPanel(); });
  if (await page.locator('[data-path="seed"],[data-path="schema"],[data-path^="authoringAttempts"]').count())
    throw new Error('Internal generation data appeared in Life & Voice.');
  if (await page.locator('.narrativeEditor textarea').count() !== 10)
    throw new Error('Life & Voice descriptions need readable multiline fields.');
  await page.evaluate(() => scrollTo(0, 0));
  await page.screenshot({ path: path.join(outputDirectory, 'life-and-voice.png') });
  await page.locator('.narrativeEditor textarea').last().scrollIntoViewIfNeeded();
  await page.screenshot({ path: path.join(outputDirectory, 'life-and-voice-last.png') });
  await page.evaluate(() => { characterEditorState.section = 'interests'; renderCharacterEditorPanel(); addNarrativeInterest(); });
  const countBefore = await page.locator('.narrativeInterest').count();
  await page.locator('.narrativeInterest').last().locator('summary').click();
  const literal = '</textarea><script>window.narrativeInjection=true</script> & a private thought';
  await page.locator('.narrativeInterest').last().locator('textarea[data-path$="~title"]').fill(literal);
  await page.locator('.narrativeInterest').last().locator('textarea[data-path$="~title"]').dispatchEvent('change');
  const edit = await page.evaluate(() => ({ title: characterEditorState.data.documents.narrative.items.at(-1).title,
    injected: window.narrativeInjection === true, dirty: characterEditorState.dirty.has('interests') }));
  if (edit.title !== literal || edit.injected || !edit.dirty) throw new Error('Interest editing or escaping failed.');
  await page.evaluate(() => removeNarrativeInterest(characterEditorState.data.documents.narrative.items.length - 1));
  if (await page.locator('.narrativeInterest').count() !== countBefore - 1) throw new Error('Remove interest did not update the draft.');
  await page.evaluate(() => { characterEditorState.data.documents.narrative = {}; renderCharacterEditorPanel(); });
  if (!(await page.locator('.narrativeEditor').innerText()).includes('earlier background')) throw new Error('Legacy empty-state missing.');
  await page.screenshot({ path: path.join(outputDirectory, 'legacy-empty.png') });
  const report = { schema: 'reign-narrative-editor-ui-v1', ok: true, isolated: true, results,
    editing: edit, storyInternalsHidden: true, legacyEmpty: true, source: fixtureDirectory,
    fidelityScope: 'Existing Control Center web editor; exact palette tokens and responsive behavior. No new native shell, portrait aperture or approved-reference raster is modified.' };
  await fs.writeFile(path.join(outputDirectory, 'ui-report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ ok: true, report: path.join(outputDirectory, 'ui-report.json'), matrixCases: results.length }));
} finally { await browser.close(); }
