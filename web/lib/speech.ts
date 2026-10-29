import type * as SpeechSdk from "microsoft-cognitiveservices-speech-sdk";
import { api } from "./api";

export interface SpeechEvents {
  /** Partial text while the learner is still speaking. */
  onInterim(text: string): void;
  /** A finished phrase with Azure's detailed result (pronunciation assessment and word timings). */
  onPhrase(text: string, detail: unknown): void;
  onError(message: string): void;
}

// Azure tokens last 10 minutes.
const TOKEN_REFRESH_MS = 8 * 60 * 1000;
// Silence that ends a phrase. The end of a whole turn is decided by the caller on top of this.
const PHRASE_SILENCE_MS = "800";

/**
 * Microphone recognition with pronunciation assessment, plus text-to-speech playback, on Azure Speech.
 * Recognition runs continuously; `setListening(false)` discards what is heard while the tutor talks.
 */
export class SpeechEngine {
  private listening = false;
  /** A phrase began while not listening; drop it when it completes. */
  private tainted = false;
  private generation = 0;
  private playChain: Promise<void> = Promise.resolve();
  private current: { audio: HTMLAudioElement; finish: () => void } | null = null;
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  private constructor(
    private readonly sdk: typeof SpeechSdk,
    private readonly recognizer: SpeechSdk.SpeechRecognizer,
    private readonly synthesizer: SpeechSdk.SpeechSynthesizer,
    private readonly events: SpeechEvents,
  ) {}

  /** @param sessionId The session whose accent sets the recognition locale and the tutor's voice. */
  static async create(events: SpeechEvents, sessionId?: string): Promise<SpeechEngine> {
    const [sdk, token] = await Promise.all([import("microsoft-cognitiveservices-speech-sdk"), api.speechToken(sessionId)]);

    const config = sdk.SpeechConfig.fromAuthorizationToken(token.token, token.region);
    config.speechRecognitionLanguage = token.locale;
    config.outputFormat = sdk.OutputFormat.Detailed;
    config.requestWordLevelTimestamps();
    config.setProperty(sdk.PropertyId.Speech_SegmentationSilenceTimeoutMs, PHRASE_SILENCE_MS);
    config.speechSynthesisVoiceName = token.voice;
    config.speechSynthesisOutputFormat = sdk.SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3;

    const recognizer = new sdk.SpeechRecognizer(config, sdk.AudioConfig.fromDefaultMicrophoneInput());
    // Empty reference text = unscripted assessment of whatever the learner says.
    const assessment = new sdk.PronunciationAssessmentConfig(
      "",
      sdk.PronunciationAssessmentGradingSystem.HundredMark,
      sdk.PronunciationAssessmentGranularity.Phoneme,
      false,
    );
    // Prosody is scored for en-US only; other accents are assessed on accuracy and fluency.
    assessment.enableProsodyAssessment = token.prosody;
    assessment.applyTo(recognizer);

    // A null audio config returns the audio instead of playing it, so playback can be queued and interrupted.
    const synthesizer = new sdk.SpeechSynthesizer(config, null as unknown as SpeechSdk.AudioConfig);

    const engine = new SpeechEngine(sdk, recognizer, synthesizer, events);
    engine.wire();
    await new Promise<void>((resolve, reject) =>
      recognizer.startContinuousRecognitionAsync(resolve, (error) => reject(new Error(error))),
    );
    engine.refreshTimer = setInterval(() => void engine.refreshToken(), TOKEN_REFRESH_MS);
    return engine;
  }

  private wire() {
    const { sdk, recognizer, events } = this;

    recognizer.recognizing = (_, e) => {
      if (!this.listening) {
        this.tainted = true;
        return;
      }
      if (!this.tainted) events.onInterim(e.result.text);
    };

    recognizer.recognized = (_, e) => {
      const dropped = !this.listening || this.tainted;
      this.tainted = false;
      if (dropped || e.result.reason !== sdk.ResultReason.RecognizedSpeech || !e.result.text?.trim()) return;

      let detail: unknown = null;
      try {
        detail = JSON.parse(e.result.properties.getProperty(sdk.PropertyId.SpeechServiceResponse_JsonResult));
      } catch {
        // Keep the text even if the detailed result is unreadable; the turn is then not scored for pronunciation.
      }
      events.onPhrase(e.result.text.trim(), detail);
    };

    recognizer.canceled = (_, e) => {
      if (e.reason === sdk.CancellationReason.Error) {
        events.onError(`Speech recognition stopped: ${e.errorDetails}`);
      }
    };
  }

  private async refreshToken() {
    try {
      const { token } = await api.speechToken();
      this.recognizer.authorizationToken = token;
      this.synthesizer.authorizationToken = token;
    } catch (error) {
      this.events.onError(error instanceof Error ? error.message : "Could not refresh the speech token.");
    }
  }

  setListening(on: boolean) {
    this.listening = on;
  }

  /** Queues text to be spoken after anything already queued. */
  speak(text: string) {
    const generation = this.generation;
    const audio = this.synthesize(text).catch((error: Error) => {
      this.events.onError(`Speech synthesis failed: ${error.message}`);
      return null;
    });
    this.playChain = this.playChain.then(async () => {
      const data = await audio;
      if (data && generation === this.generation) await this.play(data);
    });
  }

  /** Resolves when everything queued so far has finished playing (or was stopped). */
  whenSpeechDone(): Promise<void> {
    return this.playChain;
  }

  stopSpeaking() {
    this.generation++;
    this.current?.audio.pause();
    this.current?.finish();
  }

  private synthesize(text: string): Promise<ArrayBuffer> {
    return new Promise((resolve, reject) =>
      this.synthesizer.speakTextAsync(
        text,
        (result) =>
          result.reason === this.sdk.ResultReason.SynthesizingAudioCompleted
            ? resolve(result.audioData)
            : reject(new Error(result.errorDetails || "no audio returned")),
        (error) => reject(new Error(error)),
      ),
    );
  }

  private play(data: ArrayBuffer): Promise<void> {
    return new Promise((resolve) => {
      const url = URL.createObjectURL(new Blob([data], { type: "audio/mpeg" }));
      const audio = new Audio(url);
      const finish = () => {
        if (this.current?.audio === audio) this.current = null;
        URL.revokeObjectURL(url);
        resolve();
      };
      this.current = { audio, finish };
      audio.onended = finish;
      audio.onerror = finish;
      audio.play().catch(finish);
    });
  }

  dispose() {
    if (this.refreshTimer) clearInterval(this.refreshTimer);
    this.listening = false;
    this.stopSpeaking();
    this.recognizer.stopContinuousRecognitionAsync(
      () => this.recognizer.close(),
      () => this.recognizer.close(),
    );
    this.synthesizer.close();
  }
}

/** Splits streamed text into speakable sentences; call `push` with fragments and `flush` at the end. */
export class SentenceBuffer {
  private buffer = "";

  constructor(private readonly emit: (sentence: string) => void) {}

  push(fragment: string) {
    this.buffer += fragment;
    for (;;) {
      const match = /[.!?]["')\]]*\s+/.exec(this.buffer);
      if (!match) break;
      const end = match.index + match[0].length;
      this.emitTrimmed(this.buffer.slice(0, end));
      this.buffer = this.buffer.slice(end);
    }
  }

  flush() {
    this.emitTrimmed(this.buffer);
    this.buffer = "";
  }

  private emitTrimmed(text: string) {
    const sentence = text.trim();
    if (sentence) this.emit(sentence);
  }
}
