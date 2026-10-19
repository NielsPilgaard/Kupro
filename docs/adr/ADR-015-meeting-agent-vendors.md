---
title: 'ADR-015: Meeting agent — speech-to-text and LLM vendors'
status: 'Accepted'
date: '2026-10-12'
authors: 'Niels'
tags: ['ai', 'vendors', 'meeting-agent']
supersedes: ''
superseded_by: ''
description: >-
  Proposed vendors for task 015: Scaleway Generative APIs (Paris) for both transcription (whisper-large-v3, in short
  chunks cut by voice activity) and proposal extraction (deepseek-v4.1-flash with structured output, picked by a model
  spike over Scaleway's catalogue). Mistral's realtime
  Voxtral has no Danish; syv.ai's Hviske is the stronger Danish model but has no public streaming API. Both sit behind
  vendor-neutral interfaces with the model chosen per language, so a better model for one language (e.g. Hviske for
  Danish) is configuration plus, at most, one adapter.
---

# ADR-015: Meeting agent — speech-to-text and LLM vendors

## TL;DR

- **Speech-to-text**: Scaleway Generative APIs, `whisper-large-v3`, OpenAI-compatible `/audio/transcriptions`. The
  API cuts the browser's audio into utterances (voice activity detection, a few seconds each) and transcribes each one.
- **LLM**: Scaleway Generative APIs, `deepseek-v4.1-flash` (low reasoning effort) with structured output, one call per
  utterance plus the recent transcript and the session's candidate issues. Chosen over Mistral Small in a spike (IMP-006).
- One EU vendor (FR) that is already in the [ADR-006](ADR-006-eu-vendor-policy.md) register for email (TEM), one bill,
  one data processing agreement.
- Vendor-neutral: models are configured per language; any OpenAI-compatible endpoint works without code changes.
  Fallback for Danish accuracy: syv.ai Hviske (DK), hosted API or the open weights on our own GPU.

## Status

**Accepted** 2026-10-12 (owner: Scaleway for both now, vendor-agnostic so specific models can serve specific languages).

## Context

Task [015](../../tasks/015-meeting-agent.md) needs a proposal on screen within 5 s of speech, Danish and English
meetings, and EU-only processing ([ADR-006](ADR-006-eu-vendor-policy.md)). The register lists syv.ai (Hviske) and
Scaleway for speech, and Mistral, Scaleway-hosted models or syv.ai for the LLM, all as candidates. Checked 2026-10-12:

| Option | Danish | Streaming | Notes |
|---|---|---|---|
| Mistral Voxtral Realtime (FR) | No (13 languages, Dutch but not Danish) | WebSocket, sub-200 ms | Ruled out by Danish. |
| Scaleway `whisper-large-v3` (FR) | Yes | No; REST per file, 30 s chunks | Batch per utterance; beta API. |
| Scaleway `voxtral-small-24b-2507` (FR) | Not in Voxtral's language list | Chat with audio input | Could transcribe and extract in one call, but no Danish. |
| syv.ai Hviske (DK) | Best available Danish WER (beats Whisper large v3) | Not documented; "local or API" | No public API docs; open weights on Hugging Face. |

## Decision

1. **Transcription**: Scaleway `whisper-large-v3`. The browser streams audio to the API over the existing SignalR
   connection ([ADR-002](ADR-002-backend-stack.md)); the API splits it into utterances with voice activity
   detection and sends each one (WAV, under 30 s) to `/audio/transcriptions` with the meeting's language. Audio is
   kept in memory only and dropped after transcription.
2. **Proposals**: Scaleway `deepseek-v4.1-flash` with `reasoning_effort: low` (changed 2026-10-17 from
   `mistral-small-3.2-24b-instruct-2506` after IMP-006), structured output whose schema is the proposal
   (operation, target issue reference, new value, confidence, snippet). Issue references are resolved by our own
   search ([task 009](../../tasks/completed/009-search.md)), never by the model. Scaleway serves the open weights in
   Paris, so the model's origin doesn't move any data out of the EU.
3. **Seams**: `ISpeechToText` and `IProposalExtractor`, the only interfaces the feature adds (external systems that
   tests replace). Both default to an OpenAI-compatible HTTP implementation.
4. **Per-language routing**: configuration maps a meeting language to endpoint + model for each seam, with a default
   (`MeetingAgent:Transcription:Languages:da = { Endpoint, Model, ApiKey }`). Danish can move to Hviske, or English to
   another model, without touching the feature code.

## Consequences

**Positive**

- **POS-001**: No new vendor relationship; Scaleway is already approved for email.
- **POS-002**: Utterance-sized batches keep latency at roughly utterance length + 1–2 s, inside the 5 s budget.
- **POS-003**: OpenAI-compatible endpoints: plain `HttpClient`, no vendor SDK.

**Negative**

- **NEG-001**: Whisper's Danish is weaker than Hviske's, especially on names and Danish–English code-switching
  common in dev teams. Mitigation: the accuracy check below, and suggest mode by default.
- **NEG-002**: Scaleway's audio API is beta.
- **NEG-003**: Utterance batching is not true streaming: no partial words on screen while someone talks.
- **NEG-004**: The extraction model costs about 3.5× Mistral Small per meeting hour, and its slowest calls (95th
  percentile 3.4–3.9 s, rare vendor spikes up to 10 s) leave little of the 5 s budget for transcription. The
  [ADR-013](ADR-013-seats-billing.md) allowance was cut to match.

## Alternatives Considered

### Mistral API directly (Voxtral Realtime)

- **ALT-001**: **Description**: Mistral's own API with `voxtral-mini-transcribe-realtime-2602` over WebSocket, for live
  captions with sub-second delay. Re-checked 2026-10-15.
- **ALT-002**: **Rejection Reason**: no Danish (13 languages); $0.006 per audio minute, double Scaleway's Whisper; a new
  streaming adapter for about 1 s less time to proposal. Scaleway stays. Mistral offers pay-as-you-go API access, so it
  remains a viable fallback vendor (for English, or if Danish support arrives).

## Implementation Notes

- **IMP-001**: Accuracy check once the feature runs: 10 recorded standups (5 DA, 5 EN, with issue keys and names),
  compare WER and proposal precision for Whisper vs Hviske. If Danish proposal precision is below ~90 %, route Danish
  transcription to Hviske (decision 4).
- **IMP-006**: Extraction model spike, run 2026-10-15 to 2026-10-17 with typed samples (the IMP-001 recordings are
  still to come). 30 Danish and English utterances, including discussion that must produce nothing, issues named by
  title, "that one", code-switching and keys as Whisper writes them ("flow 12"), through the production extractor
  against all 12 Scaleway chat models. Cost assumes 400 calls an hour and the production context of 60 issues
  (~2,000 input tokens per call).

  | Model | Precision | Recall | Wrong at confidence ≥ 0.8 | Latency p50 / p95 | € per meeting hour |
  |---|---|---|---|---|---|
  | mistral-small-3.2 (before) | 74–77 % | 93–100 % | 7–10 | 0.5 / 0.8–1.3 s | 0.14 |
  | llama-3.3-70b | 84 % | 93 % | 5 | 0.7 / 1.9 s | 0.85 |
  | gpt-oss-120b | 78–88 % | 96–100 % | 3–8 | 1.2–2.8 / 2.1–5.4 s | 0.21 |
  | **deepseek-v4.1-flash, low effort** | **97–100 %** | **100 %** | **0–1** | **0.9–1.0 / 3.4–3.9 s** | **≈ 0.51** |
  | glm-5.3, low effort | 93 % | 96 % | 1 | 0.3 / 0.7 s | 1.76 |
  | qwen3.8-27b | 97 % | 100 % | 1 | 1.7 / 4.2–5.0 s | 0.99 |

  Mistral Small changed issues during mere discussion ("should we even do dark mode?" became canceled) and gave every
  proposal confidence 1.0, so full auto's confidence gate could not hold anything back. Default reasoning effort let
  deepseek think for up to 2,000 tokens (6–9 s) on a few utterances; low effort kept the precision. Remaining doubts:
  one run per configuration (± 1–2 cases), and no transcription errors in typed text. Re-run with the IMP-001
  recordings.
- **IMP-007**: Extractor guards from the spike: `max_tokens` 4,000 (a model padded the schema with whitespace until the
  timeout when it wanted a number where the schema says string); output cut off at the cap yields no proposals rather
  than a failed transcript request; a vendor error or timeout keeps the utterance and falls back to keyword matching;
  the context lists what stays fixed through a meeting (language, type, issues sorted by key, people, sprints) before
  the transcript, so Scaleway's cached-input price applies (38 % of input tokens cached in the spike);
  `reasoning_effort` is per route (decision 4).
- **IMP-002**: Scaleway Generative APIs is Chosen in the ADR-006 register; syv.ai stays Candidate.
- **IMP-003**: Parameter `scaleway-ai-key` (secret) and the model ids in the AppHost (`MeetingAgent.cs`); the key is
  not in the repo. A route without a key counts as not configured: no microphone, keyword fallback for proposals.
- **IMP-004**: Voice activity detection runs in the browser instead of the API (decision 1): the browser cuts
  utterances and posts each as 16 kHz WAV to `POST …/meetings/{id}/audio`. Simpler than binary streaming over
  SignalR, smaller uploads, and the same privacy: the API still keeps audio in memory only.
- **IMP-005**: Online calls (Teams, Meet, Zoom): the browser can mix a shared tab's or screen's audio into the
  microphone stream (`getDisplayMedia`, Chromium only), so one person captures the whole call. No call-platform bot or
  integration, so no US vendor in the register. Several microphones in one room hear the same sentence; the API drops
  a proposal that repeats an open one from the last two minutes (field changes match exactly, comments and new issues by
  similar words).

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md): proposals are API resources with CLI and MCP parity.
- [ADR-006](ADR-006-eu-vendor-policy.md): vendor register.
- [ADR-013](ADR-013-seats-billing.md): the monthly AI budget that limits what these vendors cost per workspace.

## References

- [Mistral realtime transcription](https://docs.mistral.ai/studio/audio/speech_to_text/realtime_transcription), [Voxtral Transcribe 2](https://mistral.ai/news/voxtral-transcribe-2/)
- [Scaleway: query audio models](https://www.scaleway.com/en/docs/generative-apis/how-to/query-audio-models/), [supported models](https://www.scaleway.com/en/docs/generative-apis/reference-content/supported-models/)
- [syv.ai products](https://syv.ai/produkter), [Hviske v3 on Hugging Face](https://huggingface.co/syvai/hviske-v3-conversation)
