export interface PlayerManifest {
  schemaVersion: string;
  lessonId: string;
  publicationId: string;
  revision: number;
  title: string;
  sourceLanguage: string;
  expiresAt: string;
  media: PlayerMedia;
  captions: CaptionTrack[];
  transcripts: TranscriptLink[];
  signLanguage: SignLanguageTrack | null;
  audioDescription: AudioDescriptionTrack | null;
  warnings: string[];
}

export interface PlayerMedia {
  url: string;
  contentType: string;
  durationMilliseconds: number;
  posterUrl: string | null;
}

export interface CaptionTrack {
  language: string;
  label: string;
  url: string;
  isDefault: boolean;
  isHumanReviewed: boolean;
}

export interface TranscriptLink {
  language: string;
  label: string;
  url: string;
  isHumanReviewed: boolean;
}

export interface SignLanguageTrack {
  language: string;
  url: string;
  syncOffsetMilliseconds: number;
}

export interface AudioDescriptionTrack {
  url: string;
  syncOffsetMilliseconds: number;
  mode: "full-mix";
}

export interface TranscriptResponse {
  lessonId: string;
  publicationId: string;
  language: string;
  revision: number;
  segments: TranscriptSegment[];
}

export interface TranscriptSegment {
  sequence: number;
  startMilliseconds: number;
  endMilliseconds: number;
  text: string;
  speakerLabel: string | null;
}

export interface PlayerConfig {
  lessonId: string;
  apiBase: string;
  locale?: "en" | "am";
}

export interface PlayerState {
  lessonId: string | null;
  ready: boolean;
  playing: boolean;
  currentTime: number;
  duration: number;
  captionLanguage: string | null;
  transcriptOpen: boolean;
  signLanguageEnabled: boolean;
  audioDescriptionEnabled: boolean;
}

export interface PlayerProgressEventDetail {
  lessonId: string;
  publicationId: string;
  revision: number;
  currentTime: number;
  duration: number;
  progress: number;
  percent: number;
}

export interface PlayerCompleteEventDetail extends PlayerProgressEventDetail {
  completedAt: string;
}

export interface AccessLearnPlayerElement extends HTMLElement {
  configure(config: PlayerConfig): void;
  play(): Promise<void>;
  pause(): void;
  seekTo(seconds: number): void;
  setCaptionLanguage(language: string | null): void;
  setTranscriptOpen(open: boolean): Promise<void>;
  setSignLanguage(enabled: boolean): Promise<void>;
  setAudioDescription(enabled: boolean, muted?: boolean): Promise<void>;
  getState(): PlayerState;
  destroy(): void;
}

declare global {
  interface HTMLElementEventMap {
    "db-player:progress": CustomEvent<PlayerProgressEventDetail>;
    "db-player:complete": CustomEvent<PlayerCompleteEventDetail>;
  }
}
