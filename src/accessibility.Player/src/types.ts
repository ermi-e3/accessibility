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
  offlinePackage?: OfflinePackageLink | null;
  documents?: DocumentLink[];
  warnings: string[];
}

export interface DocumentLink {
  outputId: string;
  title: string;
  language: string;
  kind: "html" | "pdf";
  label: "semanticHtmlChecksPassed" | "veraPdfUa1ProfilePassed";
  url: string;
  contentType: "text/html" | "text/html; charset=utf-8" | "application/pdf";
  byteLength: number;
  sha256: string;
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
  reviewStatus?: string;
  demoOnly?: boolean;
}

export interface SignLanguageTrack {
  language: string;
  label?: string;
  url: string;
  syncOffsetMilliseconds: number;
  isHumanReviewed?: boolean;
  creationMethod?: "human-recorded";
  reviewStatus?: string;
  demoOnly?: boolean;
}

export interface OfflinePackageLink {
  profile: string;
  label: string;
  url: string;
  contentType: "application/zip";
  byteLength: number;
  sha256: string;
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
  isHumanReviewed?: boolean;
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
  transcriptLanguage: string | null;
  transcriptOpen: boolean;
  signLanguageEnabled: boolean;
  signWindowMinimized: boolean;
  signWindowMaximized: boolean;
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
  setTranscriptLanguage(language: string): Promise<void>;
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
