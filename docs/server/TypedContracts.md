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
- Shared field reads live in the internal `Platform/WireReads` helper, which
  copies the server's `ReadString`/`ReadInt`/`ReadLong`/`ReadBool`/`ReadDouble`/
  `ReadDictionaryList` fallback rules.
- The first slice added byte-equality self-tests. Later slices add no new test
  code (programme decision) and rely on the existing module suites plus the
  build.

## Converted

| Route | Request | Response | Self-tests (`clan_conflicts`) |
|---|---|---|---|
| `GET /clan-conflicts/notices` | `ClanConflictNoticesRequest` (query) | `ClanConflictNoticesResponse` | `notice_contract_*`, `notice_routes_keep_legacy_wire_bytes` |
| `POST /clan-conflicts/acknowledge` | `ClanConflictAcknowledgeRequest` | `ClanConflictAcknowledgeResponse` | `notice_contract_*`, `notice_routes_keep_legacy_wire_bytes` |

Slice 2 (no new self-tests; existing suites cover the handlers):

| Route | Request | Response | Existing coverage |
|---|---|---|---|
| `GET /political-pressure/notices` | `PoliticalPressureNoticesRequest` (query) | `PoliticalPressureNoticesResponse` + `PoliticalPressureReadiness` | `RunPoliticalPressureSelfTests` |
| `POST /political-pressure/acknowledge` | `PoliticalPressureAcknowledgeRequest` + `PoliticalPressureEffectReceipt` | `PoliticalPressureAcknowledgeResponse` | `RunPoliticalPressureSelfTests` |
| `GET /diplomacy/events/next` | `DiplomaticEventsNextRequest` (query, body campaignId fallback) | `DiplomaticEventsNextResponse` (popup and public-history forms) | world test diplomacy announcement checks |
| `POST /diplomacy/events/acknowledge` | `DiplomaticEventAcknowledgeRequest` | `DiplomaticEventAcknowledgeResponse` | world test diplomacy checks |
| `GET /gossip/status` | `GossipStatusRequest` (query) | `GossipStatusResponse` (diagnostic), `GossipDeliveryReadinessResponse` (`deliveryOnly=true`), `GossipStatusProtocol.Failure` | personal gossip and witness suites |
| `GET`/`POST /communications/status` | `CommunicationStatusRequest` (hero, offset, limit; campaign and timeline stay on the shared `CommunicationApi` reads) | `CommunicationStatusResponse` (no-schema and snapshot forms), `CommunicationStatusProtocol.Failure` | communication suite |

Notes: gossip dictionaries keep their case-insensitive comparer. Event objects,
notice rows, readiness pairs, gossip message rows and topic projections,
communication rows and the gossip `communications` readiness block stay raw
dictionaries. The other `/communications/*` operations (`scope`, `tick`) are
not converted.

## Next modules

1. `/communications/scope` and `/communications/tick`.
2. Larger groups (Court, Correspondence, Save Sync) one route group per PR;
   Dialogue and terminal action reports last.

The client adopts the types in its own track after the server change merges.
