# NPC Trait Percentages and Court Virtues

Reign keeps the existing 43 foundation trait modifiers as integers from `-2` to `2`. Each NPC trait document also stores a stable `traitPercentages` value used for future action rolls:

| Modifier | Percentage band |
|---:|---:|
| -2 | 0-20 |
| -1 | 21-40 |
| 0 | 41-60 |
| 1 | 61-80 |
| 2 | 81-100 |

Initial values are deterministically distributed within the band using the hero ID, trait key, and percentage-model version. Existing files are supplemented without replacing foundation traits or Character Editor authority. Changing a modifier preserves the percentage's relative position in the old band when mapping it into the new band.

## Positive-facing virtues

All derived court virtues run from a negative pole at 0 to a positive pole at 100:

- Compassion: cruelty and disregard for suffering to kindness, mercy, empathy, and protection of dignity.
- Boldness: passive hesitation to approaching, speaking, or acting first while safe and comfortable.
- Honor: dishonesty and exploitation to honesty and fairness.
- Loyalty: betrayal and faithlessness to dependable allegiance.
- Responsibility: neglect to reliable fulfillment of entrusted duties.
- Courage: yielding to a tangible threat to acting despite credible violence, captivity, death, or destructive retaliation.
- Judgment: impulsive, poorly informed decisions to disciplined and informed decisions.

Virtues are rounded weighted averages. Inverse contributors use `100 - traitPercentage`. Neutral traits such as ambition, flirtatiousness, religion, wealth motivation, and risk tolerance remain action-specific motives rather than receiving a universal moral classification.

Boldness and Courage are deliberately separate. Boldness governs initiation while the NPC is reasonably safe: approaching first at court, speaking without prompting, raising a controversial subject, or taking command of a passive room. Courage applies only when a tangible threat is present. Embarrassment, rejection, disagreement, and attention are not tangible threats. Credible violence, captivity, death, or destructive retaliation are.

If an NPC is safe when initiating but must then follow through under danger, the action uses two sequential checks: Boldness first, then Courage. If the threat already exists and no separate initiation is required, only Courage is checked.

## Action evaluation

An action selects one primary virtue, a positive or negative polarity, a centered base chance, and bounded motive/situational adjustments:

```text
pressure = (baseChance - 50) + motiveAdjustments + situationalModifier
positiveThreshold = clamp(virtue + pressure, 0, 100)
negativeThreshold = clamp(virtue - pressure, 0, 100)
```

Positive actions pass on `d100 <= positiveThreshold`. Negative actions pass on `d100 > negativeThreshold`. Results include every input, adjustment, threshold, roll, and outcome for future court-history auditing.
