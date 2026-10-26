/**
 * Browser audio for the meeting agent (task 015, ADR-015): the microphone, optionally mixed with a shared call's audio,
 * is cut into utterances by a simple energy voice-activity detector, and each utterance is sent as 16 kHz mono WAV.
 * Audio never leaves the browser except as those utterances, and the server drops them after transcription.
 */

const TARGET_RATE = 16_000;
const WINDOW_MS = 30;
const PRE_ROLL_MS = 300;
const END_SILENCE_MS = 800;
const MIN_SPEECH_MS = 400;
const MAX_UTTERANCE_MS = 25_000;

// Copies each 128-frame block to the main thread; analysis happens there.
const WORKLET = `
class Tap extends AudioWorkletProcessor {
  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (channel) this.port.postMessage(channel.slice(0));
    return true;
  }
}
registerProcessor('kupro-tap', Tap);
`;

export interface Microphone {
  /**
   * Mixes in the audio of a shared tab or screen, so the people on a Teams, Meet or Zoom call are heard too. Resolves
   * false when the user shared something without audio; rejects when they cancel. `onEnded` runs when they stop
   * sharing from the browser's own controls.
   */
  addCallAudio(onEnded: () => void): Promise<boolean>;
  removeCallAudio(): void;
  stop(): void;
}

export interface MicrophoneEvents {
  onUtterance(wav: Blob): void;
  onSpeaking?(speaking: boolean): void;
}

/** Tab and screen sharing with audio; Chromium-based browsers only, so the UI checks for it. */
export const canCaptureCallAudio = () => typeof navigator !== 'undefined' && !!navigator.mediaDevices?.getDisplayMedia;

// Chromium options lib.dom doesn't type yet: offer "share system audio" for whole screens (desktop Teams), and keep
// Kupro's own tab out of the picker.
type CallAudioOptions = DisplayMediaStreamOptions & { systemAudio: 'include'; selfBrowserSurface: 'exclude' };

export async function startMicrophone(events: MicrophoneEvents): Promise<Microphone> {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true },
  });
  const context = new AudioContext();
  const url = URL.createObjectURL(new Blob([WORKLET], { type: 'application/javascript' }));
  await context.audioWorklet.addModule(url);
  URL.revokeObjectURL(url);

  const source = context.createMediaStreamSource(stream);
  const tap = new AudioWorkletNode(context, 'kupro-tap');
  source.connect(tap);

  const detector = new UtteranceDetector(context.sampleRate, events);
  tap.port.onmessage = (e: MessageEvent<Float32Array>) => detector.push(e.data);

  // Both sources feed the one tap, where Web Audio sums them: one stream, so a sentence is never sent twice.
  let call: { stream: MediaStream; source: MediaStreamAudioSourceNode } | null = null;
  const removeCallAudio = () => {
    if (!call) return;
    call.source.disconnect();
    for (const track of call.stream.getTracks()) track.stop();
    call = null;
  };

  return {
    async addCallAudio(onEnded) {
      // Browsers share audio only along with a picture; the picture isn't needed.
      const options: CallAudioOptions = { video: true, audio: true, systemAudio: 'include', selfBrowserSurface: 'exclude' };
      const shared = await navigator.mediaDevices.getDisplayMedia(options);
      for (const track of shared.getVideoTracks()) track.stop();
      const [audio] = shared.getAudioTracks();
      if (!audio) {
        for (const track of shared.getTracks()) track.stop();
        return false;
      }
      removeCallAudio();
      const source = context.createMediaStreamSource(new MediaStream([audio]));
      source.connect(tap);
      call = { stream: shared, source };
      audio.addEventListener('ended', () => {
        if (call?.stream !== shared) return;
        removeCallAudio();
        onEnded();
      });
      return true;
    },
    removeCallAudio,
    stop() {
      removeCallAudio();
      detector.flush();
      tap.port.onmessage = null;
      source.disconnect();
      tap.disconnect();
      for (const track of stream.getTracks()) track.stop();
      void context.close();
    },
  };
}

/** A microphone left on in an empty room pauses the meeting after this long without speech (ADR-013 guards). */
export const SILENCE_PAUSE_MS = 10 * 60_000;

/** Calls `onSilent` once when `heard()` hasn't been called for `ms`. */
export function silenceTimer(ms: number, onSilent: () => void) {
  let timer = setTimeout(onSilent, ms);
  return {
    heard() {
      clearTimeout(timer);
      timer = setTimeout(onSilent, ms);
    },
    stop() {
      clearTimeout(timer);
    },
  };
}

/** Energy VAD with an adaptive noise floor. Exported for tests. */
export class UtteranceDetector {
  private readonly windowSize: number;
  private window: Float32Array[] = [];
  private windowLength = 0;
  private preRoll: Float32Array[] = [];
  private speech: Float32Array[] = [];
  private speaking = false;
  private silenceMs = 0;
  private speechMs = 0;
  private noiseFloor = 0.005;

  constructor(
    private readonly rate: number,
    private readonly events: MicrophoneEvents,
  ) {
    this.windowSize = Math.round((rate * WINDOW_MS) / 1000);
  }

  push(block: Float32Array) {
    this.window.push(block);
    this.windowLength += block.length;
    if (this.windowLength >= this.windowSize) this.analyse();
  }

  /** Sends what is buffered, e.g. when the microphone is turned off mid-sentence. */
  flush() {
    if (this.speaking) this.finish();
  }

  private analyse() {
    const frames = this.window;
    this.window = [];
    this.windowLength = 0;
    let sum = 0;
    let count = 0;
    for (const f of frames) {
      for (const s of f) sum += s * s;
      count += f.length;
    }
    const rms = Math.sqrt(sum / Math.max(count, 1));
    const threshold = Math.max(0.012, this.noiseFloor * 3);

    if (!this.speaking) {
      // Slowly follow the room's background level while nobody talks.
      this.noiseFloor = this.noiseFloor * 0.95 + rms * 0.05;
      this.preRoll.push(...frames);
      while (this.preRoll.length * 128 > (this.rate * PRE_ROLL_MS) / 1000) this.preRoll.shift();
      if (rms > threshold) {
        this.speaking = true;
        this.speech = [...this.preRoll];
        this.preRoll = [];
        this.silenceMs = 0;
        this.speechMs = 0;
        this.events.onSpeaking?.(true);
      }
      return;
    }

    this.speech.push(...frames);
    this.speechMs += WINDOW_MS;
    this.silenceMs = rms > threshold ? 0 : this.silenceMs + WINDOW_MS;
    if (this.silenceMs >= END_SILENCE_MS || this.speechMs >= MAX_UTTERANCE_MS) this.finish();
  }

  private finish() {
    const voiced = this.speechMs - this.silenceMs;
    const frames = this.speech;
    this.speaking = false;
    this.speech = [];
    this.events.onSpeaking?.(false);
    if (voiced >= MIN_SPEECH_MS) this.events.onUtterance(encodeWav(resample(concat(frames), this.rate, TARGET_RATE), TARGET_RATE));
  }
}

function concat(frames: Float32Array[]) {
  const out = new Float32Array(frames.reduce((n, f) => n + f.length, 0));
  let offset = 0;
  for (const f of frames) {
    out.set(f, offset);
    offset += f.length;
  }
  return out;
}

/** Linear-interpolation resampling; speech-to-text models expect 16 kHz. */
export function resample(input: Float32Array, from: number, to: number) {
  if (from === to) return input;
  const out = new Float32Array(Math.floor((input.length * to) / from));
  const ratio = from / to;
  for (let i = 0; i < out.length; i++) {
    const pos = i * ratio;
    const j = Math.floor(pos);
    const next = input[Math.min(j + 1, input.length - 1)]!;
    out[i] = input[j]! + (next - input[j]!) * (pos - j);
  }
  return out;
}

/** 16-bit PCM mono WAV. */
export function encodeWav(samples: Float32Array, rate: number) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const text = (offset: number, s: string) => [...s].forEach((c, i) => view.setUint8(offset + i, c.charCodeAt(0)));
  text(0, 'RIFF');
  view.setUint32(4, 36 + samples.length * 2, true);
  text(8, 'WAVE');
  text(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  text(36, 'data');
  view.setUint32(40, samples.length * 2, true);
  samples.forEach((s, i) => view.setInt16(44 + i * 2, Math.max(-1, Math.min(1, s)) * 0x7fff, true));
  return new Blob([buffer], { type: 'audio/wav' });
}
