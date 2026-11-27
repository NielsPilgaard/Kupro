---
title: 'Task 038: On-device transcription (proposal)'
description: >-
  Proposal, waiting for a customer ask. Let the browser turn speech into text on the person's own device when it can,
  so voice commands and dictation send text instead of audio: no audio leaves the machine and Kupro pays nothing for
  transcription. Two candidates: Chrome's on-device Web Speech recognition, and Whisper running in the browser.
status: 'Proposed'
phase: 2
depends_on: ['036']
purpose: 'Proposal for task 038; becomes an implementation spec when a customer asks for it.'
---

# Task 038: On-device transcription (proposal)

## TL;DR

- **Status**: proposed 2026-11-27. Start only when a customer asks (privacy, cost, or offline-ish field use). Until
  then, operating-system dictation already covers text fields (see [Today](#today)).
- **Idea**: voice control ([task 036](036-voice-control.md)) can interpret text as well as audio. If the browser
  transcribes on the device, the web sends text, so the audio never leaves the machine and no transcription is
  metered. Interpreting the command still uses the `voice-commands` route ([ADR-022](../docs/adr/ADR-022-ai-providers-byok.md)),
  with its budget or the workspace's own key.
- **Rule**: on-device means guaranteed on-device. A browser API that may send audio to its maker's servers (Chrome's
  Web Speech without `processLocally`, Safari's speech recognition) is a non-EU vendor ([ADR-006](../docs/adr/ADR-006-eu-vendor-policy.md))
  and is never used.

## Today

macOS dictation, the iOS keyboard microphone and Android voice typing already type into any text field, Kupro's
comment boxes and command palette included. That is the person's own operating system, not a Kupro vendor. Nothing
to build; the help pages could mention it.

## Candidates

### Chrome on-device Web Speech

- Chrome 139 and later can recognise speech on the device when the page sets
  [`processLocally`](https://developer.mozilla.org/docs/Web/API/SpeechRecognition/processLocally); each language is a
  separate pack of about 60 MB that Chrome downloads.
- Edge has it only in test builds; Safari doesn't support it (checked 2026-11-27). Whether a Danish pack exists is
  unconfirmed.
- Small: feature-detect, ask for the language pack, use it when available, else fall back to the server route.

### Whisper in the browser

- Whisper on the GPU through Transformers.js (WebGPU), which works in Safari on Apple Silicon too. The model downloads
  once and is cached.
- Small Danish fine-tunes exist (base about 63M parameters, small about 214M). Unchecked: a browser-ready (ONNX)
  export, the download size after quantization, and Danish accuracy against Scaleway's `whisper-large-v3`.
- Model files served from our own EU storage, never a third-party CDN.
- Medium: a web worker, model loading with progress, and a fallback on devices without WebGPU.

## Shape when it starts

- The web picks, per person and device: on-device when it is available and guaranteed local, otherwise the server
  route. A small indicator says which ("On this device").
- No API change expected: task 036's interpret endpoint already takes text.
- A short ADR-022 implementation note: the first provider that runs in the browser, not on Kupro's servers, and not
  metered.
- Accuracy check before claiming Danish support: the ADR-015 IMP-001 recordings through both candidates.

## Not in scope

- Meeting audio: meetings record long stretches from several people; keep them on the server route for now.
- Offline use: interpreting a command still needs the server.
