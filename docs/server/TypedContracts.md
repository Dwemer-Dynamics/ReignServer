# Typed client contracts

Client-facing routes are being given typed request/response contracts in
`shared/Reign.Core.Contracts` (netstandard2.0, referenced by the server, the
Bannerlord client and Reign MCP). The contracts are additive: the wire does not
change.

## Pattern

- One file per route group under the owning module folder, for example
  `Relationships/ClanConflictNoticeContracts.cs`. Follow the style of
  `Actions/TerminalActionReport.cs`: a `*Protocol` static class holds routes,
  wire field names, defaults and fixed error strings.
- Contracts take no JSON library dependency. The server keeps its
  `JavaScriptSerializer` and the client keeps Newtonsoft. Each DTO has explicit
  `FromDictionary`/`FromQuery` and `ToDictionary` helpers that reproduce the
  exact `Dictionary<string, object>` the server built before: same keys and
  casing, same insertion order (the serializer writes it), same CLR value types
  (so number formatting is unchanged) and the same omission rules.
- Request parsing mirrors the server's `ReadString`/`ReadDouble` fallbacks so a
  handler can swap its hand-written reads for the DTO without behaviour change.
- Rows the server passes through from SQL (`SELECT *`) stay raw dictionaries in
  the DTO; a `*Fields` class names the columns clients read. Typing those rows
  would change key sets or value types and is left to a later, wire-changing
  step.
- Adopt a DTO in a handler only when it is a pure refactor. If byte equality of
  a response cannot be guaranteed, adopt the request side only.
- Every adopted route gets byte-equality checks in its module's existing
  self-test suite: serialize the legacy dictionary and the DTO output with the
  server's `Json` and compare strings, plus a live handler call where the suite
  already has a persistence fixture.

## Converted

| Route | Request | Response | Self-tests (`clan_conflicts`) |
|---|---|---|---|
| `GET /clan-conflicts/notices` | `ClanConflictNoticesRequest` (query) | `ClanConflictNoticesResponse` | `notice_contract_*`, `notice_routes_keep_legacy_wire_bytes` |
| `POST /clan-conflicts/acknowledge` | `ClanConflictAcknowledgeRequest` | `ClanConflictAcknowledgeResponse` | `notice_contract_*`, `notice_routes_keep_legacy_wire_bytes` |

## Next modules

1. `/political-pressure/notices` and `/political-pressure/acknowledge` (same
   notice/acknowledge shape plus `nativeEffects`, `readinessPressure` and typed
   `effectReceipts`; covered by `RunPoliticalPressureSelfTests`).
2. `/diplomacy/events/next` and `/diplomacy/events/acknowledge` (projected
   event objects the client already reads field by field).
3. `/gossip/status` and `/communications/status`.
4. Larger groups (Court, Correspondence, Save Sync) one route group per PR;
   Dialogue and terminal action reports last.

The client adopts the types in its own track after the server change merges.
