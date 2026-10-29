import type * as SpeechSdk from "microsoft-cognitiveservices-speech-sdk";
import { api, type SpeechToken } from "./api";

// Azure tokens last 10 minutes; fetch a new one well before that.
const TOKEN_MAX_AGE_MS = 8 * 60 * 1000;
// Silence that ends a reading.
const END_SILENCE_MS = "1200";

/**
 * Read-aloud practice on Azure Speech: plays the target in the tutor's voice, and records one reading of it
 * assessed against the target (scripted pronunciation assessment with miscue detection). The score itself is
 * computed by the API from the raw result.
 */
export class ReadAloud {
  private token: { value: SpeechToken; fetchedAt: number } | null = null;
  private recognizer: SpeechSdk.SpeechRecognizer | null = null;
  private audio: HTMLAudioElement | null = null;

  private constructor(
    private readonly sdk: typeof SpeechSdk,
    private readonly accent: string | null,
  ) {}

  /** @param accent A locale from the accent list; null for the default accent. */
  static async create(accent: string | null): Promise<ReadAloud> {
    const sdk = await import("microsoft-cognitiveservices-speech-sdk");
    const readAloud = new ReadAloud(sdk, accent);
    await readAloud.config();
    return readAloud;
  }

  private async config(): Promise<SpeechSdk.SpeechConfig> {
    if (!this.token || Date.now() - this.token.fetchedAt > TOKEN_MAX_AGE_MS) {
      this.token = { value: await api.speechToken({ accent: this.accent }), fetchedAt: Date.now() };
    }
    const { sdk } = this;
    const token = this.token.value;
    const config = sdk.SpeechConfig.fromAuthorizationToken(token.token, token.region);
    config.speechRecognitionLanguage = token.locale;
    config.speechSynthesisVoiceName = token.voice;
    config.outputFormat = sdk.OutputFormat.Detailed;
    config.speechSynthesisOutputFormat = sdk.SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3;
    config.setProperty(sdk.PropertyId.Speech_SegmentationSilenceTimeoutMs, END_SILENCE_MS);
    return config;
  }

  /** Plays the text in the accent's voice. */
  async listen(text: string): Promise<void> {
    this.stopListening();
    const { sdk } = this;
    const synthesizer = new sdk.SpeechSynthesizer(await this.config(), null as unknown as SpeechSdk.AudioConfig);
    try {
      const data = await new Promise<ArrayBuffer>((resolve, reject) =>
        synthesizer.speakTextAsync(
          text,
          (result) =>
            result.reason === sdk.ResultReason.SynthesizingAudioCompleted
              ? resolve(result.audioData)
              : reject(new Error(result.errorDetails || "no audio returned")),
          (error) => reject(new Error(error)),
        ),
      );
      const url = URL.createObjectURL(new Blob([data], { type: "audio/mpeg" }));
      const audio = new Audio(url);
      this.audio = audio;
      await new Promise<void>((resolve) => {
        audio.onended = audio.onerror = audio.onpause = () => resolve();
        audio.play().catch(() => resolve());
      });
      URL.revokeObjectURL(url);
    } finally {
      synthesizer.close();
    }
  }

  stopListening() {
    this.audio?.pause();
    this.audio = null;
  }

  /**
   * Records one reading from the microphone, until a pause, and returns Azure's detailed results for it.
   * Resolves with an empty list when nothing was heard.
   */
  async record(referenceText: string): Promise<unknown[]> {
    this.stopListening();
    const { sdk } = this;
    const recognizer = new sdk.SpeechRecognizer(await this.config(), sdk.AudioConfig.fromDefaultMicrophoneInput());
    this.recognizer = recognizer;
    const assessment = new sdk.PronunciationAssessmentConfig(
      referenceText,
      sdk.PronunciationAssessmentGradingSystem.HundredMark,
      sdk.PronunciationAssessmentGranularity.Phoneme,
      true,
    );
    assessment.applyTo(recognizer);

    try {
      const result = await new Promise<SpeechSdk.SpeechRecognitionResult>((resolve, reject) =>
        recognizer.recognizeOnceAsync(resolve, (error) => reject(new Error(error))),
      );
      if (result.reason === sdk.ResultReason.Canceled) {
        const details = sdk.CancellationDetails.fromResult(result);
        if (details.reason === sdk.CancellationReason.Error) throw new Error(`Speech recognition stopped: ${details.errorDetails}`);
        return [];
      }
      if (result.reason !== sdk.ResultReason.RecognizedSpeech) return [];
      return [JSON.parse(result.properties.getProperty(sdk.PropertyId.SpeechServiceResponse_JsonResult))];
    } finally {
      this.recognizer = null;
      recognizer.close();
    }
  }

  dispose() {
    this.stopListening();
    this.recognizer?.close();
    this.recognizer = null;
  }
}
