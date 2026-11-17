# Government module (server)

LLM dialogue for the client's government system: party speaker statements, hearing discussions and crown
decisions, with payment-ownership validation of what speakers promise. The government, party and speaker state
arrives in each request payload; this module creates no tables of its own.

> Checked against the source on 2026-11-17. [ARCHITECTURE.md](../../../ARCHITECTURE.md) describes the whole server
> and the restructuring direction.

## Main files

`GovernmentSystem.cs` (`GovernmentSpeakerStatement`), `GovernmentHearingDiscussion.cs`, `GovernmentCrownDecision.cs`,
`GovernmentPaymentOwnership.cs`.

## Routes

`/government/meeting/speaker-statement`, `/government/hearing/discussion`, `/government/crown/decision`, each wrapped
in `RunInteractiveRequest`. (`/government/political-profiles/sync` is handled by Characters, `PoliticalBeliefs.cs`.)

## Tables

None.

## Self-tests

`RunGovernmentSystemSelfTests`, VerificationLab suite `government`. Test files compiled into the server:
`GovernmentSelfTests.cs`, `GovernmentCrownDecisionSelfTests.cs`, `GovernmentEdictSelfTests.cs`,
`GovernmentHearingSelfTests.cs`, `GovernmentParticipationSelfTests.cs`, `GovernmentPaymentOwnershipSelfTests.cs`,
`GovernmentSessionSelfTests.cs`.

## Dependencies

Other modules whose members this module references, most-used first: Platform (78), Dialogue (40), Characters (8),
Persistence (4), Relationships (3), Spymaster (1), WorldSimulation (1).

Counts are approximate: distinct identifiers defined in exactly one other module and referenced per file, summed.
They show coupling, not a layering rule.
