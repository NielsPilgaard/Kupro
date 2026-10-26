---
name: pricing
description: "Tune Kupro's prices and limits in one pass: seat price (monthly/yearly, EUR/DKK), trial length and meeting hours, meeting-agent hours per seat, AI vendor rates or model, and the AI-share guard for any discount. Computes every derived number (cost per meeting hour, per seat, per trial, the doc examples) with calc.cs, then updates ADR-013 and every mirror. USE FOR: 'change the trial', 'trial hours', 'hours per seat', 'seat price', 'pricing', 'AI price changed', 'Scaleway prices', 'what does a meeting hour cost', 'can we afford N hours', new extraction model. DO NOT USE FOR: billing code (task 017 / billing task), Alunta setup."
---

# Pricing

Prices and limits move while AI prices settle. [ADR-013](../../../docs/adr/ADR-013-seats-billing.md) owns the
decisions; everything else mirrors it. Derived numbers come from `calc.cs`, never hand arithmetic, so the docs agree.

## Files

- `pricing.json`: the current knobs plus the measured AI usage. Calculator input, kept in step with ADR-013.
- `calc.cs`: prints every derived number. Overrides like `trial.meetingHours=3` are what-ifs; add `--write` to save.
- [`scripts/meeting-agent-spike.cs`](../../../scripts/meeting-agent-spike.cs): re-measures tokens per call (and
  precision, latency) through the production extractor. Run it when the model, prompt or context changes.

```powershell
dotnet run .claude/skills/pricing/calc.cs                                         # current numbers
dotnet run .claude/skills/pricing/calc.cs -- trial.meetingHours=3 allowance.hoursPerSeat=1.5   # what-if
dotnet run .claude/skills/pricing/calc.cs -- trial.meetingHours=3 --write         # save
dotnet run scripts/meeting-agent-spike.cs -- deepseek-v4.1-flash                  # re-measure tokens (a few cents)
```

The spike needs the Scaleway key (`SCALEWAY_AI_KEY` or the AppHost user-secrets). If `aspire run` locks
`Kupro.Api.exe`, add `--property:OutDir=<scratch dir>` after the script path.

## Steps

1. **Run `calc.cs`** to see where things stand.
2. **Refresh inputs** for the change:
   - AI rates: fetch <https://www.scaleway.com/en/pricing/model-as-a-service/> and update
     `meetingAgent.*.eurPer*` (cached input has its own price).
   - New model, prompt or context shape: run the spike, copy `tokIn`, `cached` and `tokOut` into
     `meetingAgent.extraction.{inputTokens,cachedShare,outputTokens}` and set `model`/`measured`. A model switch also
     changes `ExtractionModel` in `src/Kupro.AppHost/MeetingAgent.cs` and ADR-015 (decision 2, IMP-006).
3. **Try what-ifs** and show the user the tradeoff: AI share of a seat, cost per trial, whether the standup example
   still fits. Customer-facing price changes are the user's call; apply limits they asked for directly.
4. **Save** with `--write`, then **update every location** below with the calculator's numbers.
5. **Sweep** for the old values (e.g. `grep -rn "€1.40\|2 h in total"` over `docs/ tasks/`), and leave the rejected
   tier options in index.html (Free/Team/Business, bands) alone.
6. Run `ryni` or `/verify` for links; nothing here touches code unless step 2 changed the model.

## Where the numbers live

| Knob / derived number | Location (search for) |
|---|---|
| Seat price, months free | ADR-013: frontmatter `description`, TL;DR "Plans", Decision "**Monthly**" / "**Yearly**"; index.html: "2 · Pricing model" strip, landing price strips ("€9 per person a month"), Monthly/Yearly tiers; terms.html "Plans and payment" |
| DKK prices | Same places as EUR (`69 kr`, `690 kr`); keep kr/€ near 7.5 and round to a price that reads well |
| Discounts | None: the founding-team discount was dropped ([ADR-013](../../../docs/adr/ADR-013-seats-billing.md) ALT-007). Before proposing any discount, check the calculator's guard line |
| Non-profits and education | ADR-013 Decision "**Non-profits and education**" and index.html "Non-profits and education" note: free, no meeting agent, reason quotes the cost per meeting hour |
| Trial days | ADR-013 TL;DR and "**Trial, no free plan**"; index.html "30-day trial", "30 days free", Trial tier; terms.html "30-day trial"; task 017 "whole 30 days" |
| Trial meeting hours | ADR-013 TL;DR and "**Allowance**"; index.html Trial tier "Meeting agent N h in total"; task 017 "hours for a whole trial" |
| Hours per seat | ADR-013 TL;DR and "**Allowance**"; index.html Monthly tier "per seat/mo, pooled" and the note under the cost table; task 017 "hours per seat a month" and the "≈ 3.5 of N hours" panel example (team × hours) |
| Cost per meeting hour | index.html cost-basis table (three rows plus the team row); ADR-013 "**Allowance**" history line; ADR-015 IMP-006 table (language model only) and NEG-004 multiple |
| € per seat / per trial at most | index.html note under the cost table; ADR-013 "**Allowance**" |
| Runtime config | Constants in `src/Kupro.AppHost/MeetingAgent.cs`: hours per seat, euro per hour (the calculator's hour cost), daily ceiling, and each model route's rates (they become `MeetingAgent__Budget__*` and `MeetingAgent__{Transcription\|Extraction}__EuroPer*`). Trial hours come with billing |

## Rules

- ADR-013 first, mirrors second. In the Allowance bullet keep one dated line on the latest change and why, not a log.
- Quote "about" figures as the calculator prints them (rounded up to the next 10 cents), so the cost is never understated.
- Keep `pricing.json` and ADR-013 in the same commit.
