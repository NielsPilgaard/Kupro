import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { encodeWav, resample, startMicrophone, UtteranceDetector } from './microphone';

const RATE = 48_000;
const block = (amplitude: number) => Float32Array.from({ length: 128 }, (_, i) => amplitude * Math.sin(i / 3));
const feed = (detector: UtteranceDetector, amplitude: number, ms: number) => {
  for (let i = 0; i < Math.round((RATE * ms) / 1000 / 128); i++) detector.push(block(amplitude));
};

describe('encodeWav', () => {
  it('writes a 16-bit mono PCM header for the given rate', async () => {
    const wav = encodeWav(new Float32Array([0, 0.5, -1]), 16_000);
    const view = new DataView(await wav.arrayBuffer());
    expect(wav.type).toBe('audio/wav');
    expect(wav.size).toBe(44 + 6);
    expect(view.getUint32(24, true)).toBe(16_000);
    expect(view.getUint16(34, true)).toBe(16);
    expect(view.getInt16(46, true)).toBe(Math.round(0.5 * 0x7fff - 0.5));
    expect(view.getInt16(48, true)).toBe(-0x7fff);
  });
});

describe('resample', () => {
  it('turns 48 kHz into 16 kHz by thirds', () => {
    expect(resample(new Float32Array(48_000), 48_000, 16_000)).toHaveLength(16_000);
  });
});

describe('UtteranceDetector', () => {
  it('cuts speech followed by silence into one utterance', () => {
    const onUtterance = vi.fn();
    const onSpeaking = vi.fn();
    const detector = new UtteranceDetector(RATE, { onUtterance, onSpeaking });

    feed(detector, 0.001, 500);
    feed(detector, 0.3, 1200);
    feed(detector, 0.001, 1000);

    expect(onUtterance).toHaveBeenCalledOnce();
    expect(onSpeaking.mock.calls).toEqual([[true], [false]]);
    const wav = onUtterance.mock.calls[0]![0] as Blob;
    expect(wav.size).toBeGreaterThan(44 + 16_000 * 2 * 1.2);
  });

  it('ignores a click shorter than a word', () => {
    const onUtterance = vi.fn();
    const detector = new UtteranceDetector(RATE, { onUtterance });

    feed(detector, 0.001, 500);
    feed(detector, 0.3, 90);
    feed(detector, 0.001, 1000);

    expect(onUtterance).not.toHaveBeenCalled();
  });
});

describe('startMicrophone call audio', () => {
  const track = (kind: 'audio' | 'video') => ({ kind, stop: vi.fn(), addEventListener: vi.fn() });
  const stream = (...tracks: ReturnType<typeof track>[]) => ({
    getTracks: () => tracks,
    getAudioTracks: () => tracks.filter((t) => t.kind === 'audio'),
    getVideoTracks: () => tracks.filter((t) => t.kind === 'video'),
  });
  const sources: { connect: ReturnType<typeof vi.fn>; disconnect: ReturnType<typeof vi.fn> }[] = [];

  beforeEach(() => {
    sources.length = 0;
    vi.stubGlobal(
      'AudioContext',
      class {
        sampleRate = RATE;
        audioWorklet = { addModule: vi.fn() };
        createMediaStreamSource() {
          const source = { connect: vi.fn(), disconnect: vi.fn() };
          sources.push(source);
          return source;
        }
        close = vi.fn();
      },
    );
    vi.stubGlobal('AudioWorkletNode', class { port = {}; disconnect = vi.fn(); });
    vi.stubGlobal('MediaStream', class {});
    URL.createObjectURL = vi.fn(() => 'blob:tap');
    URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => vi.unstubAllGlobals());

  const start = async (shared: ReturnType<typeof stream>) => {
    const mic = stream(track('audio'));
    const getDisplayMedia = vi.fn().mockResolvedValue(shared);
    vi.stubGlobal('navigator', { mediaDevices: { getUserMedia: vi.fn().mockResolvedValue(mic), getDisplayMedia } });
    return { microphone: await startMicrophone({ onUtterance: vi.fn() }), mic, getDisplayMedia };
  };

  it('mixes the shared audio into the same tap and drops the picture', async () => {
    const video = track('video');
    const audio = track('audio');
    const { microphone, getDisplayMedia } = await start(stream(video, audio));

    expect(await microphone.addCallAudio(vi.fn())).toBe(true);

    expect(getDisplayMedia).toHaveBeenCalledWith(expect.objectContaining({ audio: true, systemAudio: 'include' }));
    expect(video.stop).toHaveBeenCalled();
    expect(sources).toHaveLength(2);
    expect(sources[1]!.connect).toHaveBeenCalled();

    microphone.stop();
    expect(audio.stop).toHaveBeenCalled();
    expect(sources[1]!.disconnect).toHaveBeenCalled();
  });

  it('reports a share without audio and releases it', async () => {
    const video = track('video');
    const { microphone } = await start(stream(video));

    expect(await microphone.addCallAudio(vi.fn())).toBe(false);
    expect(video.stop).toHaveBeenCalled();
    expect(sources).toHaveLength(1);
  });

  it('tells the caller when sharing is stopped from the browser', async () => {
    const audio = track('audio');
    const { microphone } = await start(stream(track('video'), audio));
    const onEnded = vi.fn();
    await microphone.addCallAudio(onEnded);

    const ended = audio.addEventListener.mock.calls.find(([type]) => type === 'ended')![1] as () => void;
    ended();

    expect(onEnded).toHaveBeenCalledOnce();
    expect(sources[1]!.disconnect).toHaveBeenCalled();
  });
});
