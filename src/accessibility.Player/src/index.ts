import { AccessLearnPlayer } from "./accesslearn-player";
import type { AccessLearnPlayerElement, PlayerConfig } from "./types";

export const version = "1.0.0";

function isSafeApiBase(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === "https:" ||
      (url.protocol === "http:" && ["localhost", "127.0.0.1", "::1"].includes(url.hostname));
  } catch {
    return false;
  }
}

if (!customElements.get("accesslearn-player")) {
  customElements.define("accesslearn-player", AccessLearnPlayer);
}

export function mount(target: string | Element, config: PlayerConfig): AccessLearnPlayerElement {
  const host = typeof target === "string" ? document.querySelector(target) : target;
  if (!host) throw new Error("MOUNT_TARGET_NOT_FOUND");
  if (!config.lessonId?.trim() || !config.apiBase?.trim() || !isSafeApiBase(config.apiBase)) {
    throw new Error("INVALID_CONFIG");
  }
  if (host instanceof AccessLearnPlayer) {
    host.configure(config);
    return host;
  }
  if (host.querySelector(":scope > accesslearn-player")) throw new Error("DUPLICATE_MOUNT");
  const player = document.createElement("accesslearn-player") as AccessLearnPlayer;
  player.configure(config);
  host.append(player);
  return player;
}

export const AccessLearn = Object.freeze({ mount, version });

declare global {
  interface Window {
    AccessLearn: typeof AccessLearn;
  }
}

window.AccessLearn = AccessLearn;

const script = document.currentScript as HTMLScriptElement | null;
const autoMount = (): void => {
  const target = script?.dataset.accesslearnMount;
  const lessonId = script?.dataset.lessonId;
  const apiBase = script?.dataset.apiBase;
  if (!target && !lessonId && !apiBase) return;
  if (!target || !lessonId || !apiBase) {
    console.error("AccessLearn declarative mount requires data-accesslearn-mount, data-lesson-id, and data-api-base.");
    return;
  }
  try {
    mount(target, {
      lessonId,
      apiBase,
      locale: script?.dataset.locale === "am" ? "am" : "en"
    });
  } catch (error) {
    console.error("AccessLearn player mount failed.", error);
  }
};

if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", autoMount, { once: true });
else autoMount();

export type { AccessLearnPlayerElement, PlayerConfig } from "./types";
