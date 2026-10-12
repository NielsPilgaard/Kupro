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
  chunks cut by voice activity) and proposal extraction (a Mistral model with structured output). Mistral's realtime
  Voxtral has no Danish; syv.ai's Hviske is the stronger Danish model but has no public streaming API. Both sit behind
  vendor-neutral interfaces with the model chosen per language, so a better model for one language (e.g. Hviske for
  Danish) is configuration plus, at most, one adapter.
---

# ADR-015: Meeting agent — speech-to-text and LLM vendors

## TL;DR

- **Speech-to-text**: Scaleway Generative APIs, `whisper-large-v3`, OpenAI-compatible `/audio/transcriptions`. The
  API cuts the browser's audio into utterances (voice activity detection, a few seconds each) and transcribes each one.
- **LLM**: Scaleway Generative APIs, `mistral-small-3.2-24b-instruct-2506` with structured output, one call per
  utterance plus the recent transcript and the session's candidate issues.
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
2. **Proposals**: Scaleway `mistral-small-3.2-24b-instruct-2506`, structured output whose schema is the proposal
   (operation, target issue reference, new value, confidence, snippet). Issue references are resolved by our own
   search ([task 009](../../tasks/completed/009-search.md)), never by the model.
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

## Implementation Notes

- **IMP-001**: Accuracy check once the feature runs: 10 recorded standups (5 DA, 5 EN, with issue keys and names),
  compare WER and proposal precision for Whisper vs Hviske. If Danish proposal precision is below ~90 %, route Danish
  transcription to Hviske (decision 4).
- **IMP-002**: Scaleway Generative APIs is Chosen in the ADR-006 register; syv.ai stays Candidate.
- **IMP-003**: Parameters `scaleway-ai-key` (secret) and the model ids in the AppHost; nothing in the repo.

## Related Decisions

- [ADR-001](ADR-001-agent-parity.md): proposals are API resources with CLI and MCP parity.
- [ADR-006](ADR-006-eu-vendor-policy.md): vendor register.

## References

- [Mistral realtime transcription](https://docs.mistral.ai/studio/audio/speech_to_text/realtime_transcription), [Voxtral Transcribe 2](https://mistral.ai/news/voxtral-transcribe-2/)
- [Scaleway: query audio models](https://www.scaleway.com/en/docs/generative-apis/how-to/query-audio-models/), [supported models](https://www.scaleway.com/en/docs/generative-apis/reference-content/supported-models/)
- [syv.ai products](https://syv.ai/produkter), [Hviske v3 on Hugging Face](https://huggingface.co/syvai/hviske-v3-conversation)
