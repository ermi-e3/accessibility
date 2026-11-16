import { message, normalizeLocale, type MessageKey, type PlayerLocale } from "./i18n";
import { playerStyles } from "./styles";
import type {
  AccessLearnPlayerElement,
  CaptionTrack,
  PlayerConfig,
  PlayerManifest,
  PlayerState,
  TranscriptResponse,
  TranscriptSegment
} from "./types";

const PREFERENCES_KEY = "accesslearn-player:v1:preferences";
const SEEK_SECONDS = 10;
const SIGN_DRIFT_SECONDS = 0.35;

interface PlayerPreferences {
  captionScale: number;
  captionColor: string;
  captionBackground: string;
  captionOpacity: number;
  signPosition: "top-left" | "top-right" | "bottom-left" | "bottom-right" | "custom";
  signSize: "small" | "medium" | "large";
  signLeft: number | null;
  signTop: number | null;
  captionLanguage: string | null;
  transcriptOpen: boolean;
}

const defaultPreferences: PlayerPreferences = {
  captionScale: 1,
  captionColor: "#ffffff",
  captionBackground: "#000000",
  captionOpacity: 0.82,
  signPosition: "top-right",
  signSize: "medium",
  signLeft: null,
  signTop: null,
  captionLanguage: null,
  transcriptOpen: false
};

export class AccessLearnPlayer extends HTMLElement implements AccessLearnPlayerElement {
  static readonly observedAttributes = ["lesson-id", "api-base", "locale"];

  readonly #root: ShadowRoot;
  #events = new AbortController();
  #loadController: AbortController | null = null;
  #manifest: PlayerManifest | null = null;
  #locale: PlayerLocale = "en";
  #ready = false;
  #transcriptOpen = false;
  #signEnabled = false;
  #audioDescriptionEnabled = false;
  #captionLanguage: string | null = null;
  #transcriptCache = new Map<string, TranscriptResponse>();
  #preferences = this.#loadPreferences();
  #restorePrimaryMuted = false;
  #refreshTimer: number | null = null;
  #refreshing = false;
  #resourceRefreshAttempts = 0;
  #configuring = false;
  #activeSegmentSequence: number | null = null;

  constructor() {
    super();
    this.#root = this.attachShadow({ mode: "open" });
  }

  connectedCallback(): void {
    if (!this.hasAttribute("tabindex")) this.tabIndex = 0;
    this.setAttribute("role", "group");
    this.setAttribute("translate", "yes");
    void this.#initialize();
  }

  disconnectedCallback(): void {
    this.#stopMedia();
    this.#events.abort();
    this.#loadController?.abort();
    this.#clearRefreshTimer();
  }

  attributeChangedCallback(name: string, previous: string | null, next: string | null): void {
    if (previous === next || !this.isConnected || this.#configuring) return;
    if (name === "locale" && this.#ready) {
      void this.#initialize();
      return;
    }
    if (name === "lesson-id" || name === "api-base") void this.#initialize();
  }

  configure(config: PlayerConfig): void {
    this.#configuring = true;
    this.setAttribute("lesson-id", config.lessonId);
    this.setAttribute("api-base", config.apiBase);
    if (config.locale) this.setAttribute("locale", config.locale);
    else this.removeAttribute("locale");
    this.#configuring = false;
    if (this.isConnected) void this.#initialize();
  }

  async play(): Promise<void> {
    const media = this.#media();
    if (!media) return;
    try {
      await media.play();
    } catch (error) {
      this.#showStatus(this.#text("mediaFailed"), true);
      this.#emit("accesslearn:error", { code: "PLAYBACK_REJECTED", recoverable: true });
      throw error;
    }
  }

  pause(): void {
    this.#media()?.pause();
  }

  seekTo(seconds: number): void {
    const media = this.#media();
    if (!media || !Number.isFinite(seconds)) return;
    media.currentTime = Math.max(0, Math.min(media.duration || seconds, seconds));
    this.#syncSecondary(true);
  }

  setCaptionLanguage(language: string | null): void {
    if (!this.#manifest) return;
    const available = language === null || this.#manifest.captions.some((track) => track.language === language);
    if (!available) throw new Error("RESOURCE_UNAVAILABLE");
    this.#captionLanguage = language;
    const media = this.#media();
    if (media) {
      for (const track of Array.from(media.textTracks)) {
        track.mode = language !== null && track.language === language ? "hidden" : "disabled";
      }
    }
    const selector = this.#element<HTMLSelectElement>("[data-caption-select]");
    if (selector) selector.value = language ?? "";
    const captionOverlay = this.#element<HTMLElement>("[data-caption-text]");
    if (captionOverlay) captionOverlay.lang = language ?? this.#manifest.sourceLanguage;
    this.#preferences.captionLanguage = language;
    this.#persistPreferences();
    this.#renderActiveCue();
    if (this.#transcriptOpen) void this.#loadTranscript(language ?? this.#defaultTranscriptLanguage());
    this.#emit("accesslearn:captionchange", { language });
  }

  async setTranscriptOpen(open: boolean): Promise<void> {
    this.#transcriptOpen = open;
    this.#preferences.transcriptOpen = open;
    this.#persistPreferences();
    const transcript = this.#element<HTMLElement>("[data-transcript]");
    const restoreFocus = !open && Boolean(transcript?.contains(this.#root.activeElement));
    const layout = this.#element<HTMLElement>("[data-layout]");
    const button = this.#element<HTMLButtonElement>("[data-transcript-button]");
    if (transcript) transcript.hidden = !open;
    layout?.classList.toggle("has-transcript", open);
    button?.setAttribute("aria-pressed", String(open));
    if (open) await this.#loadTranscript(this.#captionLanguage ?? this.#defaultTranscriptLanguage());
    else if (restoreFocus) button?.focus();
  }

  async setSignLanguage(enabled: boolean): Promise<void> {
    if (!this.#manifest?.signLanguage && enabled) throw new Error("RESOURCE_UNAVAILABLE");
    if (enabled === this.#signEnabled) return;
    this.#signEnabled = enabled;
    const window = this.#element<HTMLElement>("[data-sign-window]");
    const button = this.#element<HTMLButtonElement>("[data-sign-button]");
    if (window) window.hidden = !enabled;
    button?.setAttribute("aria-pressed", String(enabled));
    if (enabled) {
      const signMedia = this.#signMedia();
      if (signMedia && !signMedia.src && this.#manifest?.signLanguage) {
        signMedia.src = this.#manifest.signLanguage.url;
      }
      this.#syncSecondary(true);
      if (!this.#media()?.paused) {
        try {
          await signMedia?.play();
        } catch {
          await this.#handleSecondaryFailure("sign");
          return;
        }
      }
    } else {
      this.#signMedia()?.pause();
    }
    this.#emit("accesslearn:signchange", { enabled });
  }

  async setAudioDescription(enabled: boolean, muted = false): Promise<void> {
    if (!this.#manifest?.audioDescription && enabled) throw new Error("RESOURCE_UNAVAILABLE");
    if (enabled === this.#audioDescriptionEnabled) return;
    const media = this.#media();
    const audio = this.#descriptionMedia();
    if (!media || !audio) return;
    this.#audioDescriptionEnabled = enabled;
    const button = this.#element<HTMLButtonElement>("[data-description-button]");
    button?.setAttribute("aria-pressed", String(enabled));
    if (enabled) {
      if (!audio.src && this.#manifest?.audioDescription) {
        audio.src = this.#manifest.audioDescription.url;
      }
      this.#restorePrimaryMuted = media.muted;
      media.muted = true;
      audio.volume = media.volume;
      audio.muted = muted;
      this.#syncSecondary(true);
      if (!media.paused) {
        try {
          await audio.play();
        } catch {
          await this.#handleSecondaryFailure("description");
          return;
        }
      }
    } else {
      audio.pause();
      media.muted = this.#restorePrimaryMuted;
    }
    this.#refreshMuteControl();
    this.#emit("accesslearn:audiodescriptionchange", { enabled });
  }

  getState(): PlayerState {
    const media = this.#media();
    return {
      lessonId: this.#manifest?.lessonId ?? null,
      ready: this.#ready,
      playing: Boolean(media && !media.paused),
      currentTime: media?.currentTime ?? 0,
      duration: Number.isFinite(media?.duration) ? media?.duration ?? 0 : 0,
      captionLanguage: this.#captionLanguage,
      transcriptOpen: this.#transcriptOpen,
      signLanguageEnabled: this.#signEnabled,
      audioDescriptionEnabled: this.#audioDescriptionEnabled
    };
  }

  destroy(): void {
    this.#stopMedia();
    this.#events.abort();
    this.#loadController?.abort();
    this.#clearRefreshTimer();
    this.#root.replaceChildren();
    this.#ready = false;
    this.remove();
  }

  async #initialize(isRefresh = false): Promise<void> {
    this.#stopMedia();
    this.#events.abort();
    this.#events = new AbortController();
    this.#loadController?.abort();
    const loadController = new AbortController();
    this.#loadController = loadController;
    this.#clearRefreshTimer();
    if (!isRefresh) {
      this.#resourceRefreshAttempts = 0;
      this.#signEnabled = false;
      this.#audioDescriptionEnabled = false;
      this.#transcriptOpen = this.#preferences.transcriptOpen;
      this.#restorePrimaryMuted = false;
      this.#activeSegmentSequence = null;
    }
    this.#locale = normalizeLocale(this.getAttribute("locale"));
    this.lang = this.#locale;
    this.dir = "ltr";
    this.setAttribute("aria-label", this.#text("player"));
    this.#ready = false;
    this.#manifest = null;
    this.#transcriptCache.clear();
    this.#renderLoading();

    const lessonId = this.getAttribute("lesson-id")?.trim();
    const apiBase = this.getAttribute("api-base")?.trim();
    if (!lessonId || !apiBase) {
      this.#renderFatal("INVALID_CONFIG");
      return;
    }

    try {
      const manifestUrl = new URL(
        `api/v1/public/lessons/${encodeURIComponent(lessonId)}/manifest`,
        this.#withTrailingSlash(apiBase)
      );
      if (!this.#isSafeUrl(manifestUrl.toString())) throw new Error("UNSAFE_API_URL");
      const response = await fetch(manifestUrl, {
        headers: { "Accept-Language": this.#locale },
        signal: loadController.signal
      });
      if (!response.ok) throw new Error(`MANIFEST_${response.status}`);
      const manifest = (await response.json()) as PlayerManifest;
      this.#validateManifest(manifest, lessonId);
      this.#manifest = manifest;
      this.#renderPlayer();
      this.#configureManifest();
      this.#ready = true;
      this.#scheduleManifestRefresh();
      this.#showStatus(this.#text("ready"));
      this.#emit("accesslearn:ready", {
        lessonId: manifest.lessonId,
        revision: manifest.revision,
        languages: manifest.captions.map((track) => track.language),
        features: {
          signLanguage: Boolean(manifest.signLanguage),
          audioDescription: Boolean(manifest.audioDescription),
          transcript: manifest.transcripts.length > 0
        }
      });
    } catch (error) {
      if (loadController.signal.aborted) return;
      this.#renderFatal(error instanceof Error ? error.message : "MANIFEST_FAILED");
    }
  }

  #renderLoading(): void {
    this.#root.innerHTML = `
      <style>${playerStyles}</style>
      <section class="player loading" aria-busy="true" lang="${this.#locale}" dir="ltr">
        <p role="status">${this.#text("loading")}</p>
      </section>`;
  }

  #renderFatal(code: string): void {
    this.#root.innerHTML = `
      <style>${playerStyles}</style>
      <section class="player loading" lang="${this.#locale}" dir="ltr">
        <p class="status error" role="alert">${this.#text("loadFailed")}</p>
        <button type="button" data-retry>${this.#text("retry")}</button>
      </section>`;
    this.#element<HTMLButtonElement>("[data-retry]")?.addEventListener(
      "click",
      () => void this.#initialize(),
      { signal: this.#events.signal }
    );
    this.#emit("accesslearn:error", { code, recoverable: true });
  }

  #renderPlayer(): void {
    this.#root.innerHTML = `
      <style>${playerStyles}</style>
      <section class="player" data-player lang="${this.#locale}" dir="ltr">
        <h2 class="visually-hidden" data-title></h2>
        <div class="layout" data-layout>
          <div class="media-column">
            <div class="stage" data-stage>
              <video class="primary-media" data-media playsinline preload="metadata" crossorigin="anonymous"></video>
              <div class="caption-overlay" aria-hidden="true"><span data-caption-text></span></div>
              <div class="sign-window" data-sign-window data-position="top-right" data-size="medium" tabindex="0"
                   role="group" aria-label="${this.#text("signLanguage")}" aria-describedby="al-sign-help" hidden>
                <p class="visually-hidden" id="al-sign-help">${this.#text("signMovementHelp")}</p>
                <video class="sign-video" data-sign-media muted playsinline preload="metadata" crossorigin="anonymous"></video>
                <div class="sign-toolbar">
                  <button type="button" data-sign-minimize></button>
                </div>
              </div>
              <audio data-description-media preload="metadata" crossorigin="anonymous"></audio>
            </div>
            <div class="controls" role="group" aria-label="${this.#text("player")}">
              <button type="button" data-play>${this.#text("play")}</button>
              <label class="visually-hidden" for="al-seek">${this.#text("seek")}</label>
              <input class="seek" id="al-seek" data-seek type="range" min="0" max="0" step="0.1" value="0" aria-label="${this.#text("seek")}">
              <output class="time" data-time>00:00 / 00:00</output>
              <button type="button" data-mute>${this.#text("mute")}</button>
              <label class="visually-hidden" for="al-volume">${this.#text("volume")}</label>
              <input class="volume" id="al-volume" data-volume type="range" min="0" max="1" step="0.05" value="1" aria-label="${this.#text("volume")}">
              <label class="visually-hidden" for="al-speed">${this.#text("speed")}</label>
              <select id="al-speed" data-speed aria-label="${this.#text("speed")}">
                <option value="0.75">0.75×</option><option value="1" selected>1×</option>
                <option value="1.25">1.25×</option><option value="1.5">1.5×</option><option value="2">2×</option>
              </select>
              <label class="visually-hidden" for="al-captions">${this.#text("captions")}</label>
              <select id="al-captions" data-caption-select aria-label="${this.#text("captions")}"></select>
              <button type="button" data-caption-settings>${this.#text("captionSettings")}</button>
              <button type="button" data-sign-button aria-pressed="false">${this.#text("signLanguage")}</button>
              <button type="button" data-description-button aria-pressed="false">${this.#text("audioDescription")}</button>
              <button type="button" data-transcript-button aria-pressed="false">${this.#text("transcript")}</button>
              <a class="control" data-offline-link>${this.#text("downloadOffline")}</a>
              <button type="button" data-fullscreen aria-pressed="false">${this.#text("fullscreen")}</button>
              <button type="button" data-help>${this.#text("keyboardHelp")}</button>
            </div>
            <p class="status" data-status role="status" aria-live="polite"></p>
            <ul class="warnings" data-warnings hidden></ul>
          </div>
          <aside class="transcript" data-transcript hidden aria-label="${this.#text("transcript")}">
            <header><h3>${this.#text("transcript")}</h3><button type="button" data-transcript-close>${this.#text("close")}</button></header>
            <label class="visually-hidden" for="al-transcript-search">${this.#text("transcriptSearch")}</label>
            <input id="al-transcript-search" data-transcript-search type="search" placeholder="${this.#text("transcriptSearch")}">
            <p data-transcript-empty hidden>${this.#text("transcriptEmpty")}</p>
            <ol class="segments" data-segments></ol>
          </aside>
        </div>
        <dialog class="dialog" data-settings-dialog aria-labelledby="al-settings-title">
          <h3 id="al-settings-title">${this.#text("captionSettings")}</h3>
          <div class="settings-grid">
            <label>${this.#text("captionSize")}<select data-caption-size><option value="0.75">75%</option><option value="1">100%</option><option value="1.25">125%</option><option value="1.5">150%</option><option value="2">200%</option></select></label>
            <label>${this.#text("captionTextColor")}<input data-caption-color type="color"></label>
            <label>${this.#text("captionBackground")}<input data-caption-background type="color"></label>
            <label>${this.#text("captionOpacity")}<input data-caption-opacity type="range" min="0" max="1" step="0.05"></label>
            <label data-sign-setting>${this.#text("position")}<select data-sign-position><option value="top-left">${this.#text("cornerTopLeft")}</option><option value="top-right">${this.#text("cornerTopRight")}</option><option value="bottom-left">${this.#text("cornerBottomLeft")}</option><option value="bottom-right">${this.#text("cornerBottomRight")}</option></select></label>
            <label data-sign-setting>${this.#text("size")}<select data-sign-size><option value="small">${this.#text("small")}</option><option value="medium">${this.#text("medium")}</option><option value="large">${this.#text("large")}</option></select></label>
          </div>
          <div class="dialog-actions"><button type="button" data-settings-close>${this.#text("close")}</button></div>
        </dialog>
        <dialog class="dialog" data-help-dialog aria-labelledby="al-help-title">
          <h3 id="al-help-title">${this.#text("keyboardHelp")}</h3><p>${this.#text("shortcuts")}</p>
          <div class="dialog-actions"><button type="button" data-help-close>${this.#text("close")}</button></div>
        </dialog>
      </section>`;
    this.#bindControls();
    this.#applyPreferences();
  }

  #configureManifest(): void {
    const manifest = this.#manifest;
    const media = this.#media();
    if (!manifest || !media) return;
    const title = this.#element<HTMLElement>("[data-title]")!;
    title.textContent = manifest.title;
    title.lang = manifest.sourceLanguage;
    media.src = manifest.media.url;
    if (manifest.media.posterUrl && !manifest.media.contentType.startsWith("audio/")) media.poster = manifest.media.posterUrl;
    this.#element<HTMLElement>("[data-stage]")?.classList.toggle(
      "audio-only",
      manifest.media.contentType.startsWith("audio/")
    );
    this.#configureTracks(manifest.captions);
    const signButton = this.#element<HTMLButtonElement>("[data-sign-button]");
    if (signButton) signButton.hidden = !manifest.signLanguage;
    for (const setting of this.#root.querySelectorAll<HTMLElement>("[data-sign-setting]")) {
      setting.hidden = !manifest.signLanguage;
    }
    const descriptionButton = this.#element<HTMLButtonElement>("[data-description-button]");
    if (descriptionButton) descriptionButton.hidden = !manifest.audioDescription;
    const transcriptButton = this.#element<HTMLButtonElement>("[data-transcript-button]");
    if (transcriptButton) transcriptButton.hidden = manifest.transcripts.length === 0;
    const offlineLink = this.#element<HTMLAnchorElement>("[data-offline-link]");
    if (offlineLink) {
      const apiBase = this.getAttribute("api-base") ?? location.origin;
      offlineLink.href = new URL(
        `offline/?lesson=${encodeURIComponent(manifest.lessonId)}&locale=${this.#locale}`,
        this.#withTrailingSlash(apiBase)
      ).toString();
    }
    const warnings = this.#element<HTMLUListElement>("[data-warnings]");
    if (warnings && manifest.warnings.length > 0) {
      warnings.hidden = false;
      for (const warning of manifest.warnings) {
        const item = document.createElement("li");
        item.textContent = this.#warningText(warning);
        warnings.append(item);
      }
    }
  }

  #configureTracks(captions: CaptionTrack[]): void {
    const media = this.#media();
    const selector = this.#element<HTMLSelectElement>("[data-caption-select]");
    if (!media || !selector) return;
    selector.replaceChildren();
    const off = document.createElement("option");
    off.value = "";
    off.textContent = this.#text("captionsOff");
    selector.append(off);
    for (const caption of captions) {
      const option = document.createElement("option");
      option.value = caption.language;
      option.textContent = `${caption.label}${caption.isHumanReviewed ? " ✓" : ""}`;
      selector.append(option);
      const track = document.createElement("track");
      track.kind = "captions";
      track.label = caption.label;
      track.srclang = caption.language;
      track.src = caption.url;
      track.default = caption.isDefault;
      track.addEventListener("load", () => {
        track.track.addEventListener("cuechange", () => this.#renderActiveCue());
        if (track.track.language === this.#captionLanguage) track.track.mode = "hidden";
      });
      track.addEventListener("error", () => void this.#handleResourceError("CAPTION_FAILED"));
      media.append(track);
    }
    const preferred = captions.find((track) => track.language === this.#preferences.captionLanguage)
      ?? captions.find((track) => track.isDefault)
      ?? captions[0];
    this.setCaptionLanguage(preferred?.language ?? null);
    if (this.#preferences.transcriptOpen && this.#manifest?.transcripts.length) {
      void this.setTranscriptOpen(true);
    }
  }

  #bindControls(): void {
    const signal = this.#events.signal;
    const media = this.#media()!;
    this.#element<HTMLButtonElement>("[data-play]")?.addEventListener("click", () => void this.#togglePlay(), { signal });
    this.#element<HTMLInputElement>("[data-seek]")?.addEventListener("input", (event) => {
      this.seekTo(Number((event.currentTarget as HTMLInputElement).value));
    }, { signal });
    this.#element<HTMLButtonElement>("[data-mute]")?.addEventListener("click", () => this.#toggleMute(), { signal });
    this.#element<HTMLInputElement>("[data-volume]")?.addEventListener("input", (event) => {
      const volume = Number((event.currentTarget as HTMLInputElement).value);
      media.volume = volume;
      const description = this.#descriptionMedia();
      if (description) description.volume = volume;
    }, { signal });
    this.#element<HTMLSelectElement>("[data-speed]")?.addEventListener("change", (event) => {
      const rate = Number((event.currentTarget as HTMLSelectElement).value);
      media.playbackRate = rate;
      if (this.#signMedia()) this.#signMedia()!.playbackRate = rate;
      if (this.#descriptionMedia()) this.#descriptionMedia()!.playbackRate = rate;
    }, { signal });
    this.#element<HTMLSelectElement>("[data-caption-select]")?.addEventListener("change", (event) => {
      const value = (event.currentTarget as HTMLSelectElement).value;
      this.setCaptionLanguage(value || null);
    }, { signal });
    this.#element<HTMLButtonElement>("[data-caption-settings]")?.addEventListener("click", () => this.#settingsDialog()?.showModal(), { signal });
    this.#element<HTMLButtonElement>("[data-settings-close]")?.addEventListener("click", () => this.#settingsDialog()?.close(), { signal });
    this.#element<HTMLButtonElement>("[data-help]")?.addEventListener("click", () => this.#helpDialog()?.showModal(), { signal });
    this.#element<HTMLButtonElement>("[data-help-close]")?.addEventListener("click", () => this.#helpDialog()?.close(), { signal });
    this.#element<HTMLButtonElement>("[data-sign-button]")?.addEventListener("click", () => void this.setSignLanguage(!this.#signEnabled), { signal });
    this.#element<HTMLButtonElement>("[data-description-button]")?.addEventListener("click", () => void this.setAudioDescription(!this.#audioDescriptionEnabled), { signal });
    this.#element<HTMLButtonElement>("[data-transcript-button]")?.addEventListener("click", () => void this.setTranscriptOpen(!this.#transcriptOpen), { signal });
    this.#element<HTMLButtonElement>("[data-transcript-close]")?.addEventListener("click", () => void this.setTranscriptOpen(false), { signal });
    this.#element<HTMLInputElement>("[data-transcript-search]")?.addEventListener("input", () => this.#renderTranscript(), { signal });
    this.#element<HTMLButtonElement>("[data-fullscreen]")?.addEventListener("click", () => void this.#toggleFullscreen(), { signal });
    this.#element<HTMLButtonElement>("[data-sign-minimize]")?.addEventListener("click", () => this.#toggleSignMinimize(), { signal });
    this.#bindPreferenceControls(signal);
    this.#bindSignMovement(signal);
    this.addEventListener("keydown", (event) => this.#onKeyDown(event), { signal });
    document.addEventListener("fullscreenchange", () => this.#refreshFullscreenControl(), { signal });
    media.addEventListener("loadedmetadata", () => this.#refreshTimeline(), { signal });
    media.addEventListener("durationchange", () => this.#refreshTimeline(), { signal });
    media.addEventListener("timeupdate", () => {
      this.#refreshTimeline();
      this.#renderActiveSegment();
      this.#syncSecondary(false);
    }, { signal });
    media.addEventListener("play", () => {
      this.#refreshPlayControl();
      void this.#resumeSecondary();
      this.#emit("accesslearn:play", {});
    }, { signal });
    media.addEventListener("pause", () => {
      this.#refreshPlayControl();
      this.#signMedia()?.pause();
      this.#descriptionMedia()?.pause();
      this.#emit("accesslearn:pause", {});
    }, { signal });
    media.addEventListener("seeking", () => this.#syncSecondary(true), { signal });
    media.addEventListener("seeked", () => this.#emit("accesslearn:seeked", { currentTime: media.currentTime }), { signal });
    media.addEventListener("volumechange", () => this.#refreshMuteControl(), { signal });
    media.addEventListener("error", () => void this.#handleResourceError("MEDIA_FAILED"), { signal });
    this.#signMedia()?.addEventListener("error", () => void this.#handleSecondaryFailure("sign"), { signal });
    this.#signMedia()?.addEventListener("ended", () => {
      if (!media.ended) void this.#handleSecondaryFailure("sign", media.duration - media.currentTime > 0.75);
    }, { signal });
    this.#descriptionMedia()?.addEventListener(
      "error",
      () => void this.#handleSecondaryFailure("description"),
      { signal }
    );
    this.#descriptionMedia()?.addEventListener("ended", () => {
      if (!media.ended) void this.#handleSecondaryFailure("description", media.duration - media.currentTime > 0.75);
    }, { signal });
  }

  #bindPreferenceControls(signal: AbortSignal): void {
    const size = this.#element<HTMLSelectElement>("[data-caption-size]")!;
    const color = this.#element<HTMLInputElement>("[data-caption-color]")!;
    const background = this.#element<HTMLInputElement>("[data-caption-background]")!;
    const opacity = this.#element<HTMLInputElement>("[data-caption-opacity]")!;
    const signPosition = this.#element<HTMLSelectElement>("[data-sign-position]")!;
    const signSize = this.#element<HTMLSelectElement>("[data-sign-size]")!;
    size.addEventListener("change", () => { this.#preferences.captionScale = Number(size.value); this.#savePreferences(); }, { signal });
    color.addEventListener("input", () => { this.#preferences.captionColor = color.value; this.#savePreferences(); }, { signal });
    background.addEventListener("input", () => { this.#preferences.captionBackground = background.value; this.#savePreferences(); }, { signal });
    opacity.addEventListener("input", () => { this.#preferences.captionOpacity = Number(opacity.value); this.#savePreferences(); }, { signal });
    signPosition.addEventListener("change", () => {
      this.#preferences.signPosition = signPosition.value as PlayerPreferences["signPosition"];
      this.#savePreferences();
    }, { signal });
    signSize.addEventListener("change", () => {
      this.#preferences.signSize = signSize.value as PlayerPreferences["signSize"];
      this.#savePreferences();
    }, { signal });
  }

  #bindSignMovement(signal: AbortSignal): void {
    const window = this.#element<HTMLElement>("[data-sign-window]")!;
    window.addEventListener("keydown", (event) => {
      if (!event.key.startsWith("Arrow")) return;
      event.preventDefault();
      event.stopPropagation();
      const step = event.shiftKey ? 1 : 10;
      const stage = this.#element<HTMLElement>("[data-stage]")!.getBoundingClientRect();
      const rect = window.getBoundingClientRect();
      let left = rect.left - stage.left;
      let top = rect.top - stage.top;
      if (event.key === "ArrowLeft") left -= step;
      if (event.key === "ArrowRight") left += step;
      if (event.key === "ArrowUp") top -= step;
      if (event.key === "ArrowDown") top += step;
      this.#setSignCoordinates(left, top);
    }, { signal });
    window.addEventListener("pointerdown", (event) => {
      if ((event.target as Element).closest("button")) return;
      window.setPointerCapture(event.pointerId);
      const stage = this.#element<HTMLElement>("[data-stage]")!.getBoundingClientRect();
      const rect = window.getBoundingClientRect();
      const offsetX = event.clientX - rect.left;
      const offsetY = event.clientY - rect.top;
      const move = (moveEvent: PointerEvent) => this.#setSignCoordinates(
        moveEvent.clientX - stage.left - offsetX,
        moveEvent.clientY - stage.top - offsetY
      );
      const end = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", end);
        window.removeEventListener("pointercancel", end);
      };
      window.addEventListener("pointermove", move);
      window.addEventListener("pointerup", end);
      window.addEventListener("pointercancel", end);
    }, { signal });
  }

  #setSignCoordinates(left: number, top: number): void {
    const window = this.#element<HTMLElement>("[data-sign-window]")!;
    const stage = this.#element<HTMLElement>("[data-stage]")!.getBoundingClientRect();
    const rect = window.getBoundingClientRect();
    const boundedLeft = Math.max(0, Math.min(stage.width - rect.width, left));
    const boundedTop = Math.max(0, Math.min(stage.height - rect.height, top));
    window.dataset.position = "custom";
    window.style.setProperty("--sign-left", `${boundedLeft}px`);
    window.style.setProperty("--sign-top", `${boundedTop}px`);
    this.#preferences.signPosition = "custom";
    this.#preferences.signLeft = boundedLeft;
    this.#preferences.signTop = boundedTop;
    this.#persistPreferences();
  }

  #onKeyDown(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    if ((event.target as Element).matches("input, select, textarea, button, [contenteditable='true']")) return;
    const key = event.key.toLowerCase();
    if (key === " " || key === "k") { event.preventDefault(); void this.#togglePlay(); }
    else if (key === "arrowleft") { event.preventDefault(); this.seekTo((this.#media()?.currentTime ?? 0) - SEEK_SECONDS); }
    else if (key === "arrowright") { event.preventDefault(); this.seekTo((this.#media()?.currentTime ?? 0) + SEEK_SECONDS); }
    else if (key === "m") { event.preventDefault(); this.#toggleMute(); }
    else if (key === "c") { event.preventDefault(); this.#cycleCaptions(); }
    else if (key === "t") { event.preventDefault(); void this.setTranscriptOpen(!this.#transcriptOpen); }
    else if (key === "s" && this.#manifest?.signLanguage) { event.preventDefault(); void this.setSignLanguage(!this.#signEnabled); }
    else if (key === "d" && this.#manifest?.audioDescription) { event.preventDefault(); void this.setAudioDescription(!this.#audioDescriptionEnabled); }
    else if (key === "f") { event.preventDefault(); void this.#toggleFullscreen(); }
    else if (event.key === "?") { event.preventDefault(); this.#helpDialog()?.showModal(); }
  }

  async #togglePlay(): Promise<void> {
    if (this.#media()?.paused) await this.play();
    else this.pause();
  }

  #toggleMute(): void {
    if (this.#audioDescriptionEnabled) {
      const audio = this.#descriptionMedia();
      if (audio) audio.muted = !audio.muted;
    } else {
      const media = this.#media();
      if (media) media.muted = !media.muted;
    }
    this.#refreshMuteControl();
  }

  #cycleCaptions(): void {
    const tracks = this.#manifest?.captions ?? [];
    if (tracks.length === 0) return;
    if (this.#captionLanguage === null) this.setCaptionLanguage(tracks[0]!.language);
    else this.setCaptionLanguage(null);
  }

  async #toggleFullscreen(): Promise<void> {
    const player = this.#element<HTMLElement>("[data-player]");
    if (!player) return;
    try {
      if (document.fullscreenElement === player) await document.exitFullscreen();
      else await player.requestFullscreen();
    } catch {
      this.#showStatus(this.#text("fullscreenFailed"), true);
      this.#emit("accesslearn:error", { code: "FULLSCREEN_REJECTED", recoverable: true });
    }
  }

  #toggleSignMinimize(): void {
    const sign = this.#element<HTMLElement>("[data-sign-window]")!;
    const video = this.#signMedia()!;
    const minimized = sign.dataset.minimized === "true";
    sign.dataset.minimized = String(!minimized);
    video.hidden = !minimized;
    const button = this.#element<HTMLButtonElement>("[data-sign-minimize]")!;
    button.textContent = minimized ? this.#text("minimize") : this.#text("restore");
  }

  async #resumeSecondary(): Promise<void> {
    this.#syncSecondary(true);
    if (this.#signEnabled) {
      try { await this.#signMedia()?.play(); } catch { await this.#handleSecondaryFailure("sign"); }
    }
    if (this.#audioDescriptionEnabled) {
      try { await this.#descriptionMedia()?.play(); } catch { await this.#handleSecondaryFailure("description"); }
    }
  }

  #scheduleManifestRefresh(): void {
    this.#clearRefreshTimer();
    const expiresAt = Date.parse(this.#manifest?.expiresAt ?? "");
    if (!Number.isFinite(expiresAt)) return;
    const lifetime = Math.max(1_000, expiresAt - Date.now());
    const refreshMargin = Math.min(60_000, Math.max(5_000, lifetime * 0.1));
    const delay = Math.max(1_000, Math.min(2_147_000_000, lifetime - refreshMargin));
    this.#refreshTimer = window.setTimeout(() => void this.#refreshManifest("expiry"), delay);
  }

  #clearRefreshTimer(): void {
    if (this.#refreshTimer !== null) {
      window.clearTimeout(this.#refreshTimer);
      this.#refreshTimer = null;
    }
  }

  async #refreshManifest(reason: "expiry" | "resource"): Promise<boolean> {
    if (this.#refreshing || (reason === "resource" && this.#resourceRefreshAttempts >= 1)) return false;
    this.#refreshing = true;
    if (reason === "resource") this.#resourceRefreshAttempts++;
    const state = this.getState();
    const previousMedia = this.#media();
    const focused = this.#root.activeElement;
    const focusable = Array.from(this.#root.querySelectorAll<HTMLElement>(
      "button, input, select, [tabindex]:not([tabindex='-1'])"
    ));
    const focusIndex = focused instanceof HTMLElement ? focusable.indexOf(focused) : -1;
    const mediaSettings = {
      muted: this.#audioDescriptionEnabled ? this.#restorePrimaryMuted : previousMedia?.muted ?? false,
      volume: previousMedia?.volume ?? 1,
      playbackRate: previousMedia?.playbackRate ?? 1,
      descriptionMuted: this.#descriptionMedia()?.muted ?? false
    };
    try {
      this.#stopMedia();
      this.#signEnabled = false;
      this.#audioDescriptionEnabled = false;
      this.#transcriptOpen = false;
      await this.#initialize(true);
      if (!this.#ready) return false;
      const media = this.#media();
      if (media) {
        media.muted = mediaSettings.muted;
        media.volume = mediaSettings.volume;
        media.playbackRate = mediaSettings.playbackRate;
        const volume = this.#element<HTMLInputElement>("[data-volume]");
        const speed = this.#element<HTMLSelectElement>("[data-speed]");
        if (volume) volume.value = String(mediaSettings.volume);
        if (speed) speed.value = String(mediaSettings.playbackRate);
      }
      if (media && media.readyState < 1) {
        await new Promise<void>((resolve) => {
          const done = (): void => resolve();
          media.addEventListener("loadedmetadata", done, { once: true });
          window.setTimeout(done, 3_000);
        });
      }
      this.seekTo(state.currentTime);
      if (state.captionLanguage && this.#manifest?.captions.some((track) => track.language === state.captionLanguage)) {
        this.setCaptionLanguage(state.captionLanguage);
      }
      if (state.transcriptOpen) await this.setTranscriptOpen(true);
      if (state.signLanguageEnabled && this.#manifest?.signLanguage) await this.setSignLanguage(true);
      if (state.audioDescriptionEnabled && this.#manifest?.audioDescription) {
        await this.setAudioDescription(true, mediaSettings.descriptionMuted);
        this.#refreshMuteControl();
      }
      if (state.playing) {
        try { await this.play(); } catch { this.#showStatus(this.#text("mediaFailed"), true); }
      }
      if (focusIndex >= 0) {
        const refreshedFocusable = this.#root.querySelectorAll<HTMLElement>(
          "button, input, select, [tabindex]:not([tabindex='-1'])"
        );
        refreshedFocusable[focusIndex]?.focus();
      }
      if (reason === "expiry") this.#resourceRefreshAttempts = 0;
      return true;
    } finally {
      this.#refreshing = false;
    }
  }

  async #handleResourceError(code: string): Promise<void> {
    if (await this.#refreshManifest("resource")) return;
    this.#showStatus(code === "MEDIA_FAILED" ? this.#text("mediaFailed") : this.#text("resourceUnavailable"), true);
    this.#emit("accesslearn:error", { code, recoverable: true });
  }

  async #handleSecondaryFailure(
    feature: "sign" | "description",
    refreshResource = true
  ): Promise<void> {
    const wasEnabled = feature === "sign" ? this.#signEnabled : this.#audioDescriptionEnabled;
    const descriptionMuted = this.#descriptionMedia()?.muted ?? false;
    if (feature === "sign") {
      this.#signEnabled = false;
      this.#signMedia()?.pause();
      const signWindow = this.#element<HTMLElement>("[data-sign-window]");
      if (signWindow) signWindow.hidden = true;
      this.#element<HTMLButtonElement>("[data-sign-button]")?.setAttribute("aria-pressed", "false");
    } else {
      this.#audioDescriptionEnabled = false;
      this.#descriptionMedia()?.pause();
      const media = this.#media();
      if (media) media.muted = this.#restorePrimaryMuted;
      this.#element<HTMLButtonElement>("[data-description-button]")?.setAttribute("aria-pressed", "false");
      this.#refreshMuteControl();
    }
    if (!refreshResource) return;
    if (await this.#refreshManifest("resource")) {
      let restored = !wasEnabled;
      try {
        if (feature === "sign" && wasEnabled && this.#manifest?.signLanguage) {
          await this.setSignLanguage(true);
          restored = true;
        }
        if (feature === "description" && wasEnabled && this.#manifest?.audioDescription) {
          await this.setAudioDescription(true, descriptionMuted);
          this.#refreshMuteControl();
          restored = true;
        }
        if (restored) return;
      } catch {
        // Fall through to the final unavailable state.
      }
    }
    this.#showStatus(this.#text("resourceUnavailable"), true);
    this.#emit("accesslearn:error", {
      code: feature === "sign" ? "SIGN_LANGUAGE_FAILED" : "AUDIO_DESCRIPTION_FAILED",
      recoverable: true
    });
  }

  #syncSecondary(force: boolean): void {
    const media = this.#media();
    if (!media || !this.#manifest) return;
    if (this.#signEnabled && this.#manifest.signLanguage) {
      this.#syncTrack(this.#signMedia(), media.currentTime + this.#manifest.signLanguage.syncOffsetMilliseconds / 1000, force);
    }
    if (this.#audioDescriptionEnabled && this.#manifest.audioDescription) {
      this.#syncTrack(this.#descriptionMedia(), media.currentTime + this.#manifest.audioDescription.syncOffsetMilliseconds / 1000, force);
    }
  }

  #syncTrack(track: HTMLMediaElement | null, desired: number, force: boolean): void {
    if (!track || !Number.isFinite(desired)) return;
    const bounded = Math.max(0, Math.min(track.duration || desired, desired));
    if (force || Math.abs(track.currentTime - bounded) > SIGN_DRIFT_SECONDS) track.currentTime = bounded;
    track.playbackRate = this.#media()?.playbackRate ?? 1;
  }

  #refreshTimeline(): void {
    const media = this.#media();
    const seek = this.#element<HTMLInputElement>("[data-seek]");
    const output = this.#element<HTMLOutputElement>("[data-time]");
    if (!media || !seek || !output) return;
    const duration = Number.isFinite(media.duration) ? media.duration : (this.#manifest?.media.durationMilliseconds ?? 0) / 1000;
    seek.max = String(duration);
    seek.value = String(media.currentTime);
    seek.setAttribute("aria-valuetext", `${this.#formatTime(media.currentTime)} / ${this.#formatTime(duration)}`);
    output.textContent = `${this.#formatTime(media.currentTime)} / ${this.#formatTime(duration)}`;
  }

  #refreshPlayControl(): void {
    const media = this.#media();
    const button = this.#element<HTMLButtonElement>("[data-play]");
    if (!media || !button) return;
    button.textContent = media.paused ? this.#text("play") : this.#text("pause");
    button.setAttribute("aria-label", button.textContent);
  }

  #refreshMuteControl(): void {
    const muted = this.#audioDescriptionEnabled
      ? this.#descriptionMedia()?.muted
      : this.#media()?.muted;
    const button = this.#element<HTMLButtonElement>("[data-mute]");
    if (!button) return;
    button.textContent = muted ? this.#text("unmute") : this.#text("mute");
    button.setAttribute("aria-pressed", String(Boolean(muted)));
  }

  #refreshFullscreenControl(): void {
    const button = this.#element<HTMLButtonElement>("[data-fullscreen]");
    const player = this.#element<HTMLElement>("[data-player]");
    if (!button || !player) return;
    const active = document.fullscreenElement === player;
    button.textContent = active ? this.#text("exitFullscreen") : this.#text("fullscreen");
    button.setAttribute("aria-pressed", String(active));
  }

  #renderActiveCue(): void {
    const overlay = this.#element<HTMLElement>("[data-caption-text]");
    const media = this.#media();
    if (!overlay || !media || this.#captionLanguage === null) {
      if (overlay) overlay.textContent = "";
      return;
    }
    const track = Array.from(media.textTracks).find((item) => item.language === this.#captionLanguage);
    const cues = track?.activeCues ? Array.from(track.activeCues) : [];
    overlay.textContent = cues.map((cue) => (cue as VTTCue).text).join("\n");
  }

  async #loadTranscript(language: string | null): Promise<void> {
    if (!language || !this.#manifest) return;
    if (!this.#transcriptCache.has(language)) {
      const link = this.#manifest.transcripts.find((item) => item.language === language);
      if (!link) return;
      try {
        const response = await fetch(link.url, { signal: this.#loadController?.signal });
        if (!response.ok) throw new Error(`TRANSCRIPT_${response.status}`);
        this.#transcriptCache.set(language, (await response.json()) as TranscriptResponse);
      } catch {
        this.#showStatus(this.#text("resourceUnavailable"), true);
        return;
      }
    }
    const transcriptRegion = this.#element<HTMLElement>("[data-transcript]");
    if (transcriptRegion) transcriptRegion.lang = language;
    this.#renderTranscript();
  }

  #renderTranscript(): void {
    const language = this.#captionLanguage ?? this.#defaultTranscriptLanguage();
    const transcript = language ? this.#transcriptCache.get(language) : undefined;
    const list = this.#element<HTMLOListElement>("[data-segments]");
    const empty = this.#element<HTMLElement>("[data-transcript-empty]");
    const search = this.#element<HTMLInputElement>("[data-transcript-search]")?.value.trim().toLocaleLowerCase(this.#locale) ?? "";
    if (!list || !empty) return;
    list.replaceChildren();
    const filtered = (transcript?.segments ?? []).filter((segment) =>
      !search || `${segment.speakerLabel ?? ""} ${segment.text}`.toLocaleLowerCase(this.#locale).includes(search)
    );
    empty.hidden = filtered.length > 0;
    for (const segment of filtered) {
      const item = document.createElement("li");
      const button = document.createElement("button");
      button.type = "button";
      button.className = "segment";
      button.dataset.segmentStart = String(segment.startMilliseconds / 1000);
      button.addEventListener("click", () => this.seekTo(segment.startMilliseconds / 1000));
      const time = document.createElement("span");
      time.className = "segment-time";
      time.textContent = this.#formatTime(segment.startMilliseconds / 1000);
      const text = document.createElement("span");
      text.textContent = segment.speakerLabel ? `${segment.speakerLabel}: ${segment.text}` : segment.text;
      button.append(time, text);
      item.append(button);
      list.append(item);
    }
    this.#renderActiveSegment();
  }

  #renderActiveSegment(): void {
    const current = this.#media()?.currentTime ?? 0;
    const language = this.#captionLanguage ?? this.#defaultTranscriptLanguage();
    const transcript = language ? this.#transcriptCache.get(language) : undefined;
    const active = transcript?.segments.find((segment) =>
      current * 1000 >= segment.startMilliseconds && current * 1000 < segment.endMilliseconds
    );
    for (const button of this.#root.querySelectorAll<HTMLButtonElement>("[data-segment-start]")) {
      const isActive = Boolean(active && Number(button.dataset.segmentStart) === active.startMilliseconds / 1000);
      button.setAttribute("aria-current", String(isActive));
      if (isActive && active && this.#transcriptOpen && this.#activeSegmentSequence !== active.sequence) {
        button.scrollIntoView({
          block: "nearest",
          behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth"
        });
      }
    }
    this.#activeSegmentSequence = active?.sequence ?? null;
  }

  #applyPreferences(): void {
    this.style.setProperty("--al-caption-size", String(this.#preferences.captionScale));
    this.style.setProperty("--al-caption-color", this.#preferences.captionColor);
    this.style.setProperty("--al-caption-background", this.#hexToRgb(this.#preferences.captionBackground));
    this.style.setProperty("--al-caption-opacity", String(this.#preferences.captionOpacity));
    const size = this.#element<HTMLSelectElement>("[data-caption-size]");
    const color = this.#element<HTMLInputElement>("[data-caption-color]");
    const background = this.#element<HTMLInputElement>("[data-caption-background]");
    const opacity = this.#element<HTMLInputElement>("[data-caption-opacity]");
    const signPosition = this.#element<HTMLSelectElement>("[data-sign-position]");
    const signSize = this.#element<HTMLSelectElement>("[data-sign-size]");
    if (size) size.value = String(this.#preferences.captionScale);
    if (color) color.value = this.#preferences.captionColor;
    if (background) background.value = this.#preferences.captionBackground;
    if (opacity) opacity.value = String(this.#preferences.captionOpacity);
    if (signPosition) signPosition.value = this.#preferences.signPosition === "custom" ? "top-right" : this.#preferences.signPosition;
    if (signSize) signSize.value = this.#preferences.signSize;
    const sign = this.#element<HTMLElement>("[data-sign-window]");
    if (sign) {
      sign.dataset.position = this.#preferences.signPosition;
      sign.dataset.size = this.#preferences.signSize;
      if (this.#preferences.signPosition === "custom" &&
          this.#preferences.signLeft !== null && this.#preferences.signTop !== null) {
        sign.style.setProperty("--sign-left", `${this.#preferences.signLeft}px`);
        sign.style.setProperty("--sign-top", `${this.#preferences.signTop}px`);
      }
    }
    const minimize = this.#element<HTMLButtonElement>("[data-sign-minimize]");
    if (minimize) minimize.textContent = this.#text("minimize");
  }

  #savePreferences(): void {
    this.#applyPreferences();
    this.#persistPreferences();
  }

  #persistPreferences(): void {
    try { localStorage.setItem(PREFERENCES_KEY, JSON.stringify(this.#preferences)); } catch { /* Storage is optional. */ }
  }

  #loadPreferences(): PlayerPreferences {
    try {
      const stored = JSON.parse(localStorage.getItem(PREFERENCES_KEY) ?? "null") as Partial<PlayerPreferences> | null;
      return stored ? { ...defaultPreferences, ...stored } : { ...defaultPreferences };
    } catch {
      return { ...defaultPreferences };
    }
  }

  #validateManifest(manifest: PlayerManifest, expectedLessonId: string): void {
    if (!manifest || !manifest.schemaVersion.startsWith("1.") || manifest.lessonId !== expectedLessonId ||
        !manifest.publicationId || !Number.isInteger(manifest.revision) || manifest.revision < 1 ||
        !manifest.media?.url || !/^(video|audio)\//.test(manifest.media.contentType) ||
        !this.#isSafeUrl(manifest.media.url) ||
        manifest.media.durationMilliseconds <= 0 || !Array.isArray(manifest.captions) ||
        !Array.isArray(manifest.transcripts) || !Array.isArray(manifest.warnings) ||
        !Number.isFinite(Date.parse(manifest.expiresAt)) || Date.parse(manifest.expiresAt) <= Date.now() ||
        manifest.captions.some((track) => !track.language || !track.label || !this.#isSafeUrl(track.url)) ||
        manifest.transcripts.some((track) => !track.language || !track.label || !this.#isSafeUrl(track.url)) ||
        Boolean(manifest.signLanguage &&
          (!manifest.signLanguage.language || !this.#isSafeUrl(manifest.signLanguage.url) ||
           !Number.isFinite(manifest.signLanguage.syncOffsetMilliseconds) ||
           Math.abs(manifest.signLanguage.syncOffsetMilliseconds) > 2_000)) ||
        Boolean(manifest.audioDescription &&
          (!this.#isSafeUrl(manifest.audioDescription.url) || manifest.audioDescription.mode !== "full-mix" ||
           !Number.isFinite(manifest.audioDescription.syncOffsetMilliseconds) ||
           Math.abs(manifest.audioDescription.syncOffsetMilliseconds) > 2_000))) {
      throw new Error("UNSUPPORTED_MANIFEST");
    }
  }

  #isSafeUrl(value: string): boolean {
    try {
      const apiBase = this.getAttribute("api-base");
      const url = apiBase ? new URL(value, apiBase) : new URL(value);
      return url.protocol === "https:" ||
        (url.protocol === "http:" && ["localhost", "127.0.0.1", "::1"].includes(url.hostname));
    } catch {
      return false;
    }
  }

  #defaultTranscriptLanguage(): string | null {
    return this.#manifest?.transcripts.find((item) => item.language === this.#manifest?.sourceLanguage)?.language
      ?? this.#manifest?.transcripts[0]?.language
      ?? null;
  }

  #stopMedia(): void {
    this.#media()?.pause();
    this.#signMedia()?.pause();
    this.#descriptionMedia()?.pause();
  }

  #showStatus(value: string, error = false): void {
    const status = this.#element<HTMLElement>("[data-status]");
    if (!status) return;
    status.textContent = value;
    status.classList.toggle("error", error);
  }

  #emit(name: string, detail: Record<string, unknown>): void {
    this.dispatchEvent(new CustomEvent(name, { detail, bubbles: true, composed: true }));
  }

  #text(key: MessageKey): string { return message(this.#locale, key); }
  #warningText(code: string): string {
    const key = code as MessageKey;
    return ["missingEnglishCaptions", "missingAmharicCaptions", "missingSignLanguage", "missingAudioDescription"]
      .includes(code)
      ? this.#text(key)
      : code;
  }
  #media(): HTMLVideoElement | null { return this.#element<HTMLVideoElement>("[data-media]"); }
  #signMedia(): HTMLVideoElement | null { return this.#element<HTMLVideoElement>("[data-sign-media]"); }
  #descriptionMedia(): HTMLAudioElement | null { return this.#element<HTMLAudioElement>("[data-description-media]"); }
  #settingsDialog(): HTMLDialogElement | null { return this.#element<HTMLDialogElement>("[data-settings-dialog]"); }
  #helpDialog(): HTMLDialogElement | null { return this.#element<HTMLDialogElement>("[data-help-dialog]"); }
  #element<T extends Element>(selector: string): T | null { return this.#root.querySelector<T>(selector); }
  #withTrailingSlash(value: string): string { return value.endsWith("/") ? value : `${value}/`; }
  #formatTime(seconds: number): string {
    const safe = Number.isFinite(seconds) && seconds > 0 ? Math.floor(seconds) : 0;
    const hours = Math.floor(safe / 3600);
    const minutes = Math.floor((safe % 3600) / 60);
    const remaining = safe % 60;
    return hours > 0
      ? `${hours}:${String(minutes).padStart(2, "0")}:${String(remaining).padStart(2, "0")}`
      : `${String(minutes).padStart(2, "0")}:${String(remaining).padStart(2, "0")}`;
  }
  #hexToRgb(value: string): string {
    const hex = value.replace("#", "");
    const number = Number.parseInt(hex, 16);
    return `${(number >> 16) & 255}, ${(number >> 8) & 255}, ${number & 255}`;
  }
}

declare global {
  interface HTMLElementTagNameMap {
    "accesslearn-player": AccessLearnPlayer;
  }
}
