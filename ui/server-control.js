// The Apache lifecycle endpoint survives a stopped Reign process.
const serverPower = {state:'unknown', busy:false, reading:false, revision:0};

function renderServerPower(state, message = '') {
  serverPower.state = state;
  const button = document.getElementById('serverPowerButton');
  if (!button) return;
  const on = state === 'on';
  button.setAttribute('aria-checked', String(on));
  button.textContent = ({on:'Turn Server Off', off:'Turn Server On', starting:'Starting Server...', stopping:'Stopping Server...', unknown:'Server Unavailable'})[state];
  button.disabled = serverPower.busy || !['on','off'].includes(state);
  document.getElementById('serverPowerStatus').textContent = message || ({on:'Server on',off:'Server off',starting:'Starting...',stopping:'Stopping...',unknown:'Cannot determine server state.'})[state];
}

async function serverControlRequest(path, options = {}) {
  const response = await fetch('/reign-control/' + path, {cache:'no-store', ...options, signal:AbortSignal.timeout(10000)});
  let result;
  try { result = await response.json(); }
  catch { throw new Error('Server control is unavailable. Open the Control Center through the ReignServer launcher.'); }
  if (!response.ok || result.ok !== true) throw new Error(result.error || `Server control failed (HTTP ${response.status}).`);
  if (!['on','off','starting','stopping'].includes(result.state)) throw new Error('The managed server state could not be verified.');
  return result;
}

async function refreshServerPower() {
  if (serverPower.busy || serverPower.reading) return;
  serverPower.reading = true;
  const revision = serverPower.revision;
  try {
    const result = await serverControlRequest('status');
    if (revision !== serverPower.revision) return;
    renderServerPower(result.state, result.message);
    if (result.state === 'on' && document.body.dataset.serverOffline === 'true') location.replace('/');
  } catch (e) { if (revision === serverPower.revision) renderServerPower('unknown', e.message); }
  finally { serverPower.reading = false; }
}

async function toggleServerPower() {
  if (serverPower.busy || !['on','off'].includes(serverPower.state)) return;
  const action = serverPower.state === 'on' ? 'stop' : 'start';
  ++serverPower.revision;
  serverPower.busy = true;
  renderServerPower(action === 'start' ? 'starting' : 'stopping');
  try {
    const result = await serverControlRequest('power', {
      method:'POST', headers:{'Content-Type':'application/json','X-Reign-Control':'1'},
      body:JSON.stringify({action})
    });
    renderServerPower(result.state, result.message);
  } catch (e) { renderServerPower('unknown', e.message); }
  finally {
    serverPower.busy = false;
    renderServerPower(serverPower.state, document.getElementById('serverPowerStatus').textContent);
  }
}

refreshServerPower();
setInterval(refreshServerPower, 2000);
