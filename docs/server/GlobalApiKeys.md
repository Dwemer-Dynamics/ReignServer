# Global API Keys

Reign saves new external provider keys only in DwemerDistro's existing Global API Keys store, never in
`data/settings.json` or the installed vault. Reign stores only a reference per slot
(`globalKeyBindings: {slot: {keyId, provider, origin}}`) and reads the current value at each outbound request. It never
caches, logs or returns the value. Keys saved locally by earlier versions keep working until you save them to Global API
Keys; no new local value is accepted.

## Slots

| Slot | Provider | Origin |
|---|---|---|
| `openRouterApiKey` (chat and OpenRouter images) | `openrouter` | `https://openrouter.ai:443` |
| `nanoGptApiKey` (chat and NanoGPT vision) | `nano_gpt` | `https://nano-gpt.com:443` |
| `portraitNanoGptApiKey` | `nano_gpt` | `https://nano-gpt.com:443` |
| `portraitAtlasApiKey` | `atlascloud` | `https://api.atlascloud.ai:443` |
| `apiKey` (custom endpoint) | the live catalog provider whose fixed origin equals the `apiUrl` origin (for example `openai`), otherwise `custom_openai` | exact origin of `apiUrl` |

`qdrantApiKey` is local vector configuration and is unchanged. Every destination of a bound slot, including a request's
`apiUrl` override, must have the bound origin. A changed endpoint, missing key, wrong provider, concurrent change or
stopped DwemerDistro stops the request before anything is sent, with a fixed error. There is no fallback to a local
value, another key or another provider. Codex subscription requests never resolve a key.

## One-time setup

Saving keys needs both of these:

1. **DwemerDistro** (`DwemerAI4Skyrim3`) updated so `/usr/local/bin/ddistro_keys_api` is installed (Core `update.sh`),
   with Global API Keys set up there.
2. **ReignServer** (`ReignServer` distro) with this version's `reignctl` (`global-keys-enroll`).

Then run `scripts/Connect-GlobalKeys.ps1` once from Windows. It is the only setup step and never runs automatically:

- DwemerDistro stages a new `reign` token beside any current one (`register`); the token is piped over stdin into
  `reignctl global-keys-enroll`, which writes `/etc/reignserver/global-keys.json` (root:reign 0640, outside `data/`)
  atomically after checking the directory and any existing file. Only then is the token committed. If storing fails,
  the staged token is discarded and the previous enrollment keeps working. Nothing is retried.
- Commit and discard name the run's stage id, and DwemerDistro refuses a second registration while one is staged, so
  two concurrent runs can never commit each other's token. If a run crashed, wait 10 minutes and drop its stage with
  `wsl -d DwemerAI4Skyrim3 -u root --exec /usr/local/bin/ddistro_keys_api discard reign --abandoned`, then rerun.
- It starts the socket and records `globalKeysDistro` in `~/.reign/installation.json` (atomic replace).
  `scripts/Deploy-LocalWsl.ps1` keeps that value when it rewrites the record for the same dedicated installation.

Until setup is done the key controls are shown disabled with this instruction. The main Save never sends a typed key:
it saves the other settings and keeps typed keys in their fields. `Start-ReignServer-Wsl.ps1` then also starts a hidden, flock-guarded
`ddistro_keys_api hold` session in `DwemerAI4Skyrim3`. That session does not start PostgreSQL; the DwemerDistro launcher
does. Stopping DwemerDistro makes bound keys unavailable and the UI says so.

Before sending the token Reign checks that the config file and `/etc/reignserver` are root-owned, that
`/mnt/wsl/dwemerdistro-keys` and its socket are root-owned with no replaceable ancestor, and that the listening peer is
root. Writes and replies are bounded; a lost reply after a write is reported as `unknown`.

## UI and routes

Each provider key field has a selector, an optional name and **Save key**. Choose `+ New global key` to save the typed
value as a new global key. Choose the selected key to replace its value (same UUID, revision checked, after a warning
that every product using it changes) or, with only a new name, to rename it. Choose another listed key to use it.
Nothing is selected or imported automatically. A key save refreshes only its own row, so other unsaved settings stay.
The custom endpoint key belongs to the API URL shown: with an unsaved URL, **Save key** sends nothing and asks for
**Save Changes** first (the typed key is kept). The save carries that URL (`expectedApiUrl`) and the server refuses it
with `endpoint_changed`, before any store call, unless its origin is the saved `apiUrl` origin (for example from a stale
tab). Replacing a key's value also requires the stored key to have the slot's provider and origin.
A legacy local custom key belongs to its saved origin. A settings save that changes the `apiUrl` origin clears it in
settings and in the installed vault, using the normal explicit-clear write, so the old key is never sent to the new
host. A path-only change keeps it. A bound reference, a merge that supplies its own key, and the other slots are unchanged.
A key typed for the new URL stays in its field until **Save key**.
**Clear** on a bound slot only removes Reign's reference; the global key stays.

`GET /api/global-keys` returns masked rows (optionally `?slot=`). `POST /api/global-keys/save`, `/unbind` and
`/api/settings/clear-key` on a bound slot are refused unless all of these hold:

- The request has the `X-Reign-Keys: 1` header.
- `Origin` exactly matches the host.
- The caller is local: either a loopback direct peer, or Apache's `X-Reign-Peer`/`X-Reign-Host` (always overwritten by
  the vhost) showing loopback or the exact WSL default gateway on port 8089.

`X-Forwarded-*` headers are never trusted. Each save carries the binding the page saw (`expectedKeyId`). Saves are
serialized and checked again before the reference changes. A concurrent change returns `conflict`. A global write that
succeeds but cannot be recorded locally returns `saved_not_bound` with the key id, and is not retried. An `unknown`
outcome is never retried: reload and check Global API Keys.
