# Global API Keys

By default Reign uses the provider keys in DwemerDistro's existing Global API Keys store. You can also save a
**Reign-only key** for any slot: it is kept in Reign's own `data/settings.json` and installed vault, is used only by
Reign, and overrides any global key for that slot. For global keys Reign stores at most a reference per slot
(`globalKeyBindings: {slot: {keyId, provider, origin}}`) and reads the current value at each outbound request. It never
caches, logs or returns a global key's value.

## Which key a slot uses

Each outbound request resolves its slot in this order:

1. **Reign-only key.** A non-empty local value for the slot is used, whether or not Global API Keys is set up and
   whether or not the slot is bound. For the chat slots it is sent only to the slot's configured origin (the saved
   `apiUrl` origin for the custom endpoint); any other destination gets no key and nothing else is tried. Portrait
   Reign-only keys (`portraitNanoGptApiKey`, `portraitAtlasApiKey`) are not origin-confined: they go to the portrait URLs
   configured in AI Image Generation, which may point at another host (a proxy, for example), as before.
2. **Bound global key.** The global key selected for the slot, with the checks below.
3. **Automatic global key.** When the slot is unbound and has no Reign-only key, Global API Keys is set up, and the store's
   status lists exactly one key marked `configured: true` with the slot's provider and origin (see Slots; a key without
   that flag is never chosen), and every destination of the
   request has that origin, Reign resolves and uses that key. The choice is made again for every request and is never
   saved as a binding. With zero or several matching keys nothing is chosen automatically; select one with **Save key**
   or save a Reign-only key.
4. **No key.** The feature's usual missing-key message applies.

A bound or automatic global key that cannot be resolved (DwemerDistro stopped, key removed or changed) refuses the
request with a fixed error before anything is sent; it never falls back to sending without a key.

The store's status metadata (key ids, providers, origins, labels, previews and the provider catalog, never a value) is
kept in memory for 5 seconds and shared by every presence check and automatic choice; failures are kept for the same
time. Concurrent requests wait for the single status call in flight (bounded by its 15-second deadline) instead of
starting more. Key values are resolved on every request and never cached. Any Save key, select, rename or unbind
discards the cached metadata. Local (`127.0.0.1`/`localhost`) chat endpoints are accepted as configured without consulting
the store. Per request that means: a Reign-only key makes no store call; a bound key makes one `resolve`; an automatic
key makes one `resolve` plus, at most once per 5 seconds for the whole server, one `status`; a slot with no key makes at
most that shared `status` call.

Choosing a global key with **Save key** (create, replace its value, or select) is an explicit choice of the global key
for that slot and clears the slot's Reign-only key. Renaming the selected global key (no new value) and unbinding keep it. Saving a Reign-only key later on a bound slot keeps the binding and overrides it
until the Reign-only key is cleared; clearing it returns the slot to its bound or automatic global key. On the first load
after upgrading, a local value found beside a binding (a stale value from earlier versions, which never used it) is
cleared once; the `globalKeyMigrations.reignOnlyKeyOverrides` marker records that this has run. The vault stores a
Reign-only key like any other local key. It restores one into a bound slot only when the vault itself was written after
that cleanup (it carries the marker) and recorded the same binding for the slot; a vault value saved before the slot was
bound is never restored into it.

## Slots

| Slot | Provider | Origin |
|---|---|---|
| `openRouterApiKey` (chat and OpenRouter images) | `openrouter` | `https://openrouter.ai:443` |
| `nanoGptApiKey` (chat and NanoGPT vision) | `nano_gpt` | `https://nano-gpt.com:443` |
| `portraitNanoGptApiKey` | `nano_gpt` | `https://nano-gpt.com:443` |
| `portraitAtlasApiKey` | `atlascloud` | `https://api.atlascloud.ai:443` |
| `apiKey` (custom endpoint) | the live catalog provider whose fixed origin equals the `apiUrl` origin (for example `openai`), otherwise `custom_openai` | exact origin of `apiUrl` |

`ollamaCloudApiKey` uses `custom_openai` at the Ollama Cloud chat origin. `qdrantApiKey` is local vector configuration
and is unchanged. Every destination of a bound or automatic global key, including a request's `apiUrl` override, must
have the key's origin. A changed endpoint, missing key, wrong provider, concurrent change or stopped DwemerDistro stops
the request before anything is sent, with a fixed error. There is no fallback to another key or another provider. Codex
subscription requests never resolve a key.

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

Without setup, unbound slots keep the plain local key field: the main Save stores a typed key as that slot's Reign-only
key. Bound slots show the key controls disabled with this instruction. Wherever the key controls are shown, the main Save
never sends a typed key: it saves the other settings and keeps typed keys in their fields for **Save key** or **Save
Reign-only key**. `Start-ReignServer-Wsl.ps1` then also starts a hidden, flock-guarded
`ddistro_keys_api hold` session in `DwemerAI4Skyrim3`. That session does not start PostgreSQL; the DwemerDistro launcher
does. Stopping DwemerDistro makes bound keys unavailable and the UI says so.

Before sending the token Reign checks that the config file and `/etc/reignserver` are root-owned, that
`/mnt/wsl/dwemerdistro-keys` and its socket are root-owned with no replaceable ancestor, and that the listening peer is
root. Writes and replies are bounded; a lost reply after a write is reported as `unknown`.

## UI and routes

Each provider key field has a selector, an optional name and **Save key**. Choose `+ New global key` to save the typed
value as a new global key. Choose the selected key to replace its value (same UUID, revision checked, after a warning
that every product using it changes) or, with only a new name, to rename it. Choose another listed key to use it.
No selection is saved automatically; an automatic global key is shown as `Automatic: <label>` in the selector and in the
row's source line. A key save refreshes only its own row, so other unsaved settings stay.
Below the selector, each row states the key in use (`Using your Reign-only key`, `Using global key <label>`,
`Using global key <label> automatically`, or `No key`) and offers **Save Reign-only key** (saves the typed value through
the normal settings route; DwemerDistro need not be running) and, when one is saved, **Clear Reign-only key**. For the
custom endpoint, **Save Reign-only key** follows the same URL rule as **Save key** below: the save carries
`expectedApiUrl` and the settings route refuses it with `endpoint_changed`, storing nothing, unless its origin is the
endpoint the save leaves in place.
The custom endpoint key belongs to the API URL shown: with an unsaved URL, **Save key** sends nothing and asks for
**Save Changes** first (the typed key is kept). The save carries that URL (`expectedApiUrl`) and the server refuses it
with `endpoint_changed`, before any store call, unless its origin is the saved `apiUrl` origin (for example from a stale
tab). Replacing a key's value also requires the stored key to have the slot's provider and origin.
A legacy local custom key belongs to its saved origin. A settings save that changes the `apiUrl` origin clears it in
settings and in the installed vault, using the normal explicit-clear write, so the old key is never sent to the new
host. A path-only change keeps it. A bound reference, a merge that supplies its own key, and the other slots are unchanged.
A key typed for the new URL stays in its field until **Save key**.
A Reign-only custom endpoint key is cleared the same way when the origin changes, even on a bound slot.
**Clear** first clears a Reign-only key if the slot has one (returning `outcome: local_cleared`; `unchanged` when an
unbound slot had none); on a bound slot without
one it only removes Reign's reference, and the global key stays.

`GET /api/global-keys` returns masked rows (optionally `?slot=`). Each row carries `source` (`reign_only`,
`global_bound`, `global_auto` or `none`), `autoCandidates` (matching configured global keys) and, for `global_auto`,
`autoKey` with the key's id, label and preview only. `localKeyWorkflow` is always `true`: every installation may save a
Reign-only key. `POST /api/global-keys/save`, `/unbind` and `/api/settings/clear-key` when it unbinds are refused unless
all of these hold:

- The request has the `X-Reign-Keys: 1` header.
- `Origin` exactly matches the host.
- The caller is local: either a loopback direct peer, or Apache's `X-Reign-Peer`/`X-Reign-Host` (always overwritten by
  the vhost) showing loopback or the exact WSL default gateway on port 8089.

`X-Forwarded-*` headers are never trusted. Each save carries the binding the page saw (`expectedKeyId`). Saves are
serialized and checked again before the reference changes. A concurrent change returns `conflict`. A global write that
succeeds but cannot be recorded locally returns `saved_not_bound` with the key id, and is not retried. An `unknown`
outcome is never retried: reload and check Global API Keys.
# Local installations (2026-11-13 repair)

An installation without `/etc/reignserver/global-keys.json` keeps the existing local provider-key Save and masking workflow. No shared helper is installed or enrolled automatically. This preserves the user's explicit choice for the repaired installation. Unbound slots there do not show shared-store controls, and their local keys are Reign-only keys. Enrollment adds guarded per-row shared-store saves, including Ollama Cloud, and automatic global keys; Reign-only keys keep working and still override. A binding entry, including a malformed one, never resolves to another global key; with no Reign-only key it refuses the request while the store is unavailable.

