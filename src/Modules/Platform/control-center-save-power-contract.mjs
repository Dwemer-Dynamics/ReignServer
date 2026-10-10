// Exact Lab HTML; all browser requests are intercepted, including lifecycle commands.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [fixtureDirectory, outputDirectory] = process.argv.slice(2);
if (!fixtureDirectory || !outputDirectory) throw new Error('Supply the immutable Lab fixture and evidence directories.');
const html = await fs.readFile(path.join(fixtureDirectory, 'control-center.html'), 'utf8');
const offlineHtml = await fs.readFile(path.join(fixtureDirectory, 'server-offline.html'), 'utf8');
const files = {'world_tone.txt':'World baseline','tavern_house_portrait_clothing.txt':'Tavern baseline','portrait_identity.txt':'Identity baseline'};
const catalog = Object.keys(files).map(name => ({name,label:name,description:'Fixture prompt',visible:true,category:name === 'world_tone.txt' ? 'general' : 'image'}));
let mode = '', state = 'on', settingsSaves = 0, promptSaves = 0, powerCalls = [], releaseSave, releaseLoad, waitSave = false, waitLoad = false;
const results = [], errors = [], requests = [];
const browser = await chromium.launch({channel:process.env.REIGN_BROWSER_CHANNEL || 'msedge',headless:true});
await fs.mkdir(outputDirectory, {recursive:true});
try {
  const page = await browser.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', dialog => dialog.dismiss());
  await page.addInitScript(() => { window.setInterval = () => 0; });
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    requests.push({path:url.pathname,method:request.method()});
    const json = (data, status=200) => route.fulfill({status,contentType:'application/json',body:JSON.stringify(data)});
    if (url.pathname === '/preview') return route.fulfill({contentType:'text/html',body:html});
    if (url.pathname === '/offline') return route.fulfill({contentType:'text/html',body:offlineHtml});
    if (url.pathname === '/reign-control/status') return mode === 'power-error' ? json({ok:false,error:'Cannot inspect service.'},503) : json({ok:true,state});
    if (url.pathname === '/reign-control/power') {
      assert.equal(request.headers()['x-reign-control'],'1');
      const {action} = request.postDataJSON(); powerCalls.push(action);
      if (mode === 'power-error') return json({ok:false,error:'Service start failed.'},503);
      state = action === 'start' ? 'starting' : 'stopping';
      return json({ok:true,state},202);
    }
    if (url.pathname === '/api/prompts') {
      if (request.method() === 'GET') {
        const snapshot = {...files};
        if (waitLoad) {waitLoad=false; await new Promise(resolve => {releaseLoad=resolve;});}
        return json({ok:true,files:snapshot,catalog});
      }
      const payload = request.postDataJSON(); ++promptSaves;
      if (waitSave) {waitSave=false; await new Promise(resolve => {releaseSave=resolve;});}
      if (mode === 'non-json') return route.fulfill({status:503,contentType:'text/html',body:'Service offline'});
      if (mode === 'network') return route.abort('failed');
      if (mode === 'save-error') return json({ok:false,error:'Permission denied.'},500);
      if (mode === 'zero-saved') return json({ok:true,saved:0,files:{...files}});
      for (const name of Object.keys(payload.files)) {
        if (payload.expectedFiles[name] !== files[name] && payload.files[name] !== files[name])
          return json({ok:false,error:'Prompt changed on the server.'});
      }
      Object.assign(files,payload.files);
      return json({ok:true,saved:Object.keys(payload.files).length,files:{...files}});
    }
    if (url.pathname === '/api/settings' && request.method() === 'POST') {
      ++settingsSaves;
      return mode === 'settings-error' ? json({ok:false,error:'Settings write failed.'},500) : json({ok:true});
    }
    return json({ok:true,autoOpenBrowser:true,campaigns:[],models:[],cases:[],results:[]});
  });
  await page.goto('http://reign-preview.invalid/preview');
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => selectControlCenterTab('images'));
  await page.waitForFunction(() => document.getElementById('imagePromptEditor').value === 'Tavern baseline');
  const editor = page.locator('#imagePromptEditor');
  const text = '  Exact user text — no rewriting.\nA second line.\n';
  await editor.fill(text);
  await page.selectOption('#imagePromptSelect','portrait_identity.txt');
  await page.selectOption('#imagePromptSelect','tavern_house_portrait_clothing.txt');
  assert.equal(await editor.inputValue(),text);
  await page.evaluate(() => selectControlCenterTab('prompting'));
  await page.waitForLoadState('networkidle');
  await page.locator('#promptEditor').fill('Separate world draft');
  await page.evaluate(() => selectControlCenterTab('images'));
  await page.waitForLoadState('networkidle');
  assert.equal(await editor.inputValue(),text);
  await page.locator('#saveChangesButton').click();
  await page.waitForFunction(() => document.getElementById('status').textContent === 'Changes saved');
  assert.equal(files['tavern_house_portrait_clothing.txt'],text);
  assert.equal(files['world_tone.txt'],'Separate world draft');
  assert.equal(settingsSaves,1);
  results.push('Header saves settings and every prompt draft byte-for-byte; category/tab reloads preserve drafts.');
  for (const failure of ['save-error','non-json','network','zero-saved']) {
    mode=failure;
    await editor.fill('Unsaved '+failure);
    await page.evaluate(() => saveSelectedPrompt('image'));
    assert.match(await page.locator('#imagePromptStatus').innerText(),/Prompt save failed/);
    assert.equal(await editor.inputValue(),'Unsaved '+failure);
    assert.equal(files['tavern_house_portrait_clothing.txt'],text);
    assert.equal(await page.locator('button[onclick=\'saveSelectedPrompt("image")\']').isDisabled(),false);
    results.push('Failed save retains draft and reports '+failure+'.');
  }
  mode='';
  files['tavern_house_portrait_clothing.txt']='Changed in another tab';
  await page.evaluate(() => saveSelectedPrompt('image'));
  assert.match(await page.locator('#imagePromptStatus').innerText(),/changed on the server/);
  await page.evaluate(() => loadPrompts());
  assert.equal(await editor.inputValue(),'Unsaved zero-saved');
  await page.evaluate(() => discardSelectedPrompt('image'));
  assert.equal(await editor.inputValue(),'Changed in another tab');
  results.push('Conflicting external saves require review; explicit discard reloads only the selected draft.');
  await editor.fill('First save'); waitSave=true;
  await page.evaluate(() => { window.saveInFlight=saveSelectedPrompt('image'); });
  for (let attempt=0;!releaseSave && attempt<500;attempt++) await new Promise(resolve => setTimeout(resolve,10));
  assert.ok(releaseSave,'The save request must reach the isolated fixture.');
  await editor.fill('Newer edit');
  releaseSave(); releaseSave=null;
  await page.evaluate(() => window.saveInFlight);
  assert.equal(files['tavern_house_portrait_clothing.txt'],'First save');
  assert.equal(await editor.inputValue(),'Newer edit');
  assert.match(await page.locator('#imagePromptStatus').innerText(),/newer edits/);
  await page.evaluate(() => saveSelectedPrompt('image'));
  assert.equal(files['tavern_house_portrait_clothing.txt'],'Newer edit');
  results.push('Edits typed during a save remain unsaved until the next save.');
  waitLoad=true;
  await page.evaluate(() => { window.loadInFlight=loadPrompts(); });
  for (let attempt=0;!releaseLoad && attempt<500;attempt++) await new Promise(resolve => setTimeout(resolve,10));
  assert.ok(releaseLoad,'The load request must reach the isolated fixture.');
  await editor.fill('After stale load');
  await page.evaluate(() => saveSelectedPrompt('image'));
  releaseLoad(); releaseLoad=null;
  await page.evaluate(() => window.loadInFlight);
  assert.equal(await editor.inputValue(),'After stale load');
  results.push('A delayed load cannot roll back a completed save.');
  mode='settings-error';
  await page.evaluate(() => saveSettings());
  assert.match(await page.locator('#status').innerText(),/Save failed: Settings write failed/);
  assert.equal(await page.locator('#saveChangesButton').isDisabled(),false);
  mode='';
  await page.evaluate(() => refreshServerPower());
  await page.locator('#serverPowerButton').click();
  await page.waitForFunction(() => document.getElementById('serverPowerButton').textContent === 'Stopping Server...');
  assert.equal(await page.locator('#serverPowerButton').isDisabled(),true);
  state='off'; await page.evaluate(() => refreshServerPower());
  await page.locator('#serverPowerButton').click();
  await page.waitForFunction(() => document.getElementById('serverPowerButton').textContent === 'Starting Server...');
  assert.deepEqual(powerCalls,['stop','start']);
  state='on'; await page.evaluate(() => refreshServerPower());
  assert.equal(await page.locator('#serverPowerButton').getAttribute('aria-checked'),'true');
  mode='power-error'; await page.locator('#serverPowerButton').click();
  await page.waitForFunction(() => document.getElementById('serverPowerButton').textContent === 'Server Unavailable');
  assert.match(await page.locator('#serverPowerStatus').innerText(),/failed/);
  results.push('One on/off control covers both directions, pending states, repeat-click blocking, and truthful failure.');
  mode='';
  for (const width of [1672,1024,600]) {
    await page.setViewportSize({width,height:941});
    for (const value of ['on','off','starting','stopping']) {
      state=value; await page.evaluate(() => refreshServerPower());
      const box=await page.locator('#serverPowerButton').boundingBox();
      assert.ok(box.x>=0 && box.x+box.width<=width);
      await page.screenshot({path:path.join(outputDirectory,`power-${value}-${width}.png`)});
    }
  }
  state='off';
  await page.goto('http://reign-preview.invalid/offline');
  await page.waitForLoadState('networkidle');
  assert.equal(await page.locator('#serverPowerButton').innerText(),'Turn Server On');
  for (const width of [1672,1024,600]) {
    await page.setViewportSize({width,height:941});
    await page.screenshot({path:path.join(outputDirectory,`offline-${width}.png`)});
  }
  await page.locator('#serverPowerButton').click();
  await page.waitForFunction(() => document.getElementById('serverPowerButton').textContent === 'Starting Server...');
  assert.equal(powerCalls.at(-1),'start');
  results.push('Reloaded offline page shares the same working start control at all three widths.');
  assert.deepEqual(errors,[]);
  assert.equal(requests.some(request=>request.path==='/api/shutdown'),false);
  await fs.writeFile(path.join(outputDirectory,'report.json'),JSON.stringify({schema:'reign-control-center-save-power-v1',ok:true,isolated:true,htmlSha256:crypto.createHash('sha256').update(html).digest('hex'),results,screenshots:15,settingsSaves,promptSaves,powerCalls,errors},null,2));
  console.log(JSON.stringify({ok:true,cases:results.length,screenshots:15,report:path.join(outputDirectory,'report.json')}));
} finally { await browser.close(); }
