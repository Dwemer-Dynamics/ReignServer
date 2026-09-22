// Render only the isolated Verification Lab HTML fixture. Every request is intercepted;
// this harness never connects to or starts the installed Control Center/server.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [htmlPath, outputDirectory] = process.argv.slice(2);
if (!htmlPath || !outputDirectory) throw new Error('Usage: node portrait-profile-ui-contract.mjs <Verification Lab HTML> <evidence directory>');
const html = await fs.readFile(htmlPath, 'utf8');
await fs.mkdir(outputDirectory, { recursive: true });
const profiles = ['portrait', 'scenery', 'adultPortrait', 'adultScenery'];
const settings = {};
for (const prefix of profiles) Object.assign(settings, {
  [prefix + 'Provider']: prefix.startsWith('adult') ? 'AtlasCloud' : 'NanoGPT',
  [prefix + 'OpenRouterImageModel']: 'openai/gpt-image-1',
  [prefix + 'NanoGptImageModel']: 'gpt-image-1.5',
  [prefix + 'AtlasImageModel']: 'bytedance/seedream-v5.0-pro/edit',
  [prefix + 'AtlasWanNegativePrompt']: 'fixture negative',
  [prefix + 'ImageStrength']: 0.75, [prefix + 'InferenceSteps']: 28, [prefix + 'GuidanceScale']: 3.5
});
const browser = await chromium.launch({ channel: process.env.REIGN_BROWSER_CHANNEL || 'msedge', headless: true });
const results = [];
let saved;
try {
  const page = await browser.newPage();
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/preview') return route.fulfill({ contentType: 'text/html', body: html });
    let body = {};
    if (url.pathname === '/api/settings') {
      if (route.request().method() === 'POST') { saved = route.request().postDataJSON(); Object.assign(settings, saved); }
      body = { ...settings, ok: true };
    } else if (url.pathname === '/portraits/shared-generation/catalog') body = { ok: true, characters: [
      { cacheKey: 'fixture', heroStringId: 'fixture', characterName: 'Source-free fixture', hasSource: false,
        hasPortrait: true, hasDerivatives: true, canGenerate: true, state: 'ready' }
    ] };
    else if (url.pathname.includes('campaign')) body = { campaigns: [] };
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
  });
  await page.goto('http://reign-preview.invalid/preview');
  await page.evaluate(() => {
    document.querySelectorAll('.page').forEach(x => x.classList.remove('active'));
    document.getElementById('images').classList.add('active');
  });
  await page.waitForFunction(() => document.getElementById('adultPortraitProvider').value === 'AtlasCloud');
  if (await page.locator('#reignGeneratorStatus').count()) throw new Error('Retired local generator status remains visible');
  await page.evaluate(() => loadSharedPortraitCatalog());
  let rebuildConfirmation = false;
  page.on('dialog', async dialog => {
    rebuildConfirmation = dialog.type() === 'confirm' && dialog.message().includes('preserving its face and outfit');
    await dialog.dismiss(); // No generation or provider request is authorized by this preview.
  });
  await page.evaluate(() => generateSelectedSharedPortrait());
  if (!rebuildConfirmation) throw new Error('Source-free shared rebuild is blocked before its cost confirmation');
  for (const width of [1672, 1024, 600]) {
    await page.setViewportSize({ width, height: 941 });
    for (const provider of ['NanoGPT', 'OpenRouter', 'AtlasCloud']) {
      for (const prefix of profiles) await page.selectOption('#' + prefix + 'Provider', provider);
      const state = await page.evaluate(({ profiles, provider }) => profiles.map(prefix => {
        const card = document.getElementById(prefix + 'Provider').closest('.imageProfileCard');
        const model = document.getElementById(prefix + (provider === 'NanoGPT' ? 'NanoGpt' : provider === 'OpenRouter' ? 'OpenRouter' : 'Atlas') + 'ImageModel');
        const box = card.getBoundingClientRect();
        return { prefix, visible: model.checkVisibility(), options: [...model.options].map(x => x.value),
          overflow: card.scrollWidth > card.clientWidth + 1,
          inViewport: box.left >= 0 && box.right <= innerWidth + 1,
          background: getComputedStyle(card).backgroundColor };
      }), { profiles, provider });
      if (state.some(x => !x.visible || x.overflow || !x.inViewport || x.background !== 'rgb(18, 18, 17)'))
        throw new Error('Layout failure: ' + JSON.stringify({ width, provider, state }));
      if (!state.every(x => JSON.stringify(x.options) === JSON.stringify(state[0].options))) throw new Error('Model catalog drift');
      await page.locator('#adultPortraitProvider').focus();
      const screenshot = path.join(outputDirectory, `${width}-${provider}.png`);
      await page.locator('.imageProfileGrid').screenshot({ path: screenshot });
      await page.locator('.card').filter({ has: page.locator('#sharedPortraitCharacterSelect') }).screenshot({
        path: path.join(outputDirectory, `${width}-${provider}-shared.png`) });
      results.push({ width, provider, passed: true, state, screenshot });
    }
    for (const prefix of ['portrait', 'scenery']) await page.selectOption('#' + prefix + 'Provider', 'Codex');
    const codex = await page.evaluate(() => ['portrait', 'scenery'].map(prefix => {
      const card = document.getElementById(prefix + 'Provider').closest('.imageProfileCard');
      const model = document.getElementById(prefix + 'CodexImageModel');
      return { prefix, visible: model.checkVisibility(), disabled: model.disabled, value: model.value,
        overflow: card.scrollWidth > card.clientWidth + 1,
        hiddenOthers: !document.getElementById(prefix + 'NanoGptProfileSettings').checkVisibility()
          && !document.getElementById(prefix + 'AtlasProfileSettings').checkVisibility()
          && !document.getElementById(prefix + 'RequestParameters').checkVisibility() };
    }));
    if (codex.some(x => !x.visible || !x.disabled || x.value !== 'gpt-image-2' || x.overflow || !x.hiddenOthers))
      throw new Error('Codex profile failure: ' + JSON.stringify(codex));
    for (const prefix of ['adultPortrait', 'adultScenery']) {
      if (await page.locator('#' + prefix + 'Provider option[value="Codex"]').count()
        || await page.locator('#' + prefix + 'CodexImageModel').count()) throw new Error('Codex leaked into an adult profile');
    }
    const screenshot = path.join(outputDirectory, `${width}-Codex.png`);
    await page.locator('.imageProfileGrid').screenshot({ path: screenshot });
    results.push({ width, provider: 'Codex', passed: true, state: codex, screenshot });
  }
  await page.selectOption('#adultPortraitProvider', 'OpenRouter');
  await page.selectOption('#adultPortraitOpenRouterImageModel', 'bytedance-seed/seedream-5-0-pro');
  await page.selectOption('#adultPortraitProvider', 'NanoGPT');
  await page.selectOption('#adultPortraitNanoGptImageModel', 'flux-kontext');
  await page.fill('#adultPortraitImageStrength', '0.91');
  await page.evaluate(() => saveSettings());
  if (!saved || saved.adultPortraitOpenRouterImageModel !== 'bytedance-seed/seedream-5-0-pro' || saved.adultPortraitNanoGptImageModel !== 'flux-kontext' || saved.adultPortraitImageStrength !== 0.91
      || saved.adultSceneryProvider !== 'AtlasCloud' || saved.portraitProvider !== 'Codex' || saved.sceneryProvider !== 'Codex') throw new Error('Independent settings save failed');
  await page.evaluate(() => loadSettings());
  if (await page.inputValue('#adultPortraitNanoGptImageModel') !== 'flux-kontext') throw new Error('Settings reload failed');
  await fs.writeFile(path.join(outputDirectory, 'ui-report.json'), JSON.stringify({ ok: true, htmlPath, isolated: true, results, sourceFreeRebuildConfirmation: rebuildConfirmation, saveReload: true }, null, 2));
  console.log(JSON.stringify({ ok: true, report: path.join(outputDirectory, 'ui-report.json'), screenshots: results.length }));
} finally { await browser.close(); }
