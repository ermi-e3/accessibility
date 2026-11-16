const DB_NAME = "accesslearn-offline-v2";
const STORE = "packages";
const params = new URLSearchParams(location.search);
const locale = params.get("locale") === "am" ? "am" : "en";
const text = {
  en: {
    title: "Offline lesson library", intro: "Download lightweight audio and text while connected, then learn without a network.",
    downloadTitle: "Download a published lesson", lessonId: "Lesson ID", download: "Download", library: "Downloaded lessons",
    empty: "No offline lessons have been downloaded.", open: "Open", remove: "Remove", update: "Update", ready: "Ready offline",
    downloading: "Downloading", complete: "Download complete", quota: "The package does not fit in available browser storage.",
    failed: "The lesson could not be downloaded. Completed files were kept for retry.", removed: "Offline lesson removed.", updateAvailable: "Update available"
  },
  am: {
    title: "የኦፍላይን ትምህርት ቤተ-መጻሕፍት", intro: "ተገናኝተው ቀላል ድምፅና ጽሑፍ ያውርዱ፤ ከዚያ ያለ ኢንተርኔት ይማሩ።",
    downloadTitle: "የታተመ ትምህርት ያውርዱ", lessonId: "የትምህርት መለያ", download: "አውርድ", library: "የወረዱ ትምህርቶች",
    empty: "ምንም የኦፍላይን ትምህርት አልወረደም።", open: "ክፈት", remove: "አስወግድ", update: "አዘምን", ready: "ለኦፍላይን ዝግጁ",
    downloading: "በማውረድ ላይ", complete: "ማውረዱ ተጠናቀቀ", quota: "ጥቅሉ ባለው የአሳሽ ማከማቻ ውስጥ አይገባም።",
    failed: "ትምህርቱን ማውረድ አልተቻለም። የተጠናቀቁ ፋይሎች ለድጋሚ ሙከራ ተቀምጠዋል።", removed: "የኦፍላይን ትምህርቱ ተወግዷል።", updateAvailable: "አዲስ ስሪት አለ"
  }
}[locale];
document.documentElement.lang = locale;
for (const element of document.querySelectorAll("[data-i18n]")) element.textContent = text[element.dataset.i18n] || element.textContent;

const library = document.querySelector("[data-library]");
const empty = document.querySelector("[data-empty]");
const status = document.querySelector("[data-status]");
const progress = document.querySelector("[data-progress]");
const lessonView = document.querySelector("[data-lesson-view]");
const lessonInput = document.querySelector("#lesson-id");
lessonInput.value = params.get("lesson") || "";

if ("serviceWorker" in navigator) navigator.serviceWorker.register("/offline/service-worker.js", { scope: "/offline/" });
if (navigator.storage?.persist) navigator.storage.persist().catch(() => false);

function database() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE, { keyPath: "packageId" });
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function records() {
  const db = await database();
  return new Promise((resolve, reject) => {
    const request = db.transaction(STORE).objectStore(STORE).getAll();
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

async function put(record) {
  const db = await database();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE, "readwrite");
    transaction.objectStore(STORE).put(record);
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}

async function removeRecord(packageId) {
  const db = await database();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction(STORE, "readwrite");
    transaction.objectStore(STORE).delete(packageId);
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error);
    transaction.onabort = () => reject(transaction.error);
  });
}

function stableUrl(publicationId, path) {
  return new URL(`/offline/content/${publicationId}/${path}`, location.origin).toString();
}

async function sha256(buffer) {
  const digest = await crypto.subtle.digest("SHA-256", buffer);
  return [...new Uint8Array(digest)].map((value) => value.toString(16).padStart(2, "0")).join("");
}

function safePath(path) {
  return typeof path === "string" && path.length > 0 && path.length <= 512 &&
    !path.startsWith("/") && !path.includes("\\") && !path.split("/").some((part) => part === "" || part === "." || part === "..");
}

function safeUrl(value) {
  try {
    const url = new URL(value, location.origin);
    return url.protocol === "https:" || url.protocol === "http:" && ["localhost", "127.0.0.1", "::1"].includes(url.hostname);
  } catch { return false; }
}

function validateManifest(manifest, expectedLessonId) {
  if (!manifest || manifest.schemaVersion !== "1.0" || manifest.lessonId !== expectedLessonId ||
      !manifest.packageId || !manifest.publicationId || !Number.isInteger(manifest.publicationRevision) ||
      !Array.isArray(manifest.assets) || manifest.assets.length === 0 || !safeUrl(manifest.zipUrl)) {
    throw new Error("INVALID_MANIFEST");
  }
  const paths = new Set();
  let total = 0;
  for (let index = 0; index < manifest.assets.length; index++) {
    const asset = manifest.assets[index];
    if (asset.sequence !== index || !safePath(asset.path) || paths.has(asset.path) || !safeUrl(asset.url) ||
        !Number.isInteger(asset.byteLength) || asset.byteLength <= 0 || !/^[0-9a-f]{64}$/.test(asset.sha256) ||
        typeof asset.contentType !== "string" || typeof asset.role !== "string") {
      throw new Error("INVALID_MANIFEST");
    }
    paths.add(asset.path); total += asset.byteLength;
  }
  if (total !== manifest.totalBytes || !manifest.assets.some((asset) => asset.role === "offlineAudio") ||
      !paths.has("index.html") || !paths.has("lesson.json") || !paths.has("styles.css")) {
    throw new Error("INVALID_MANIFEST");
  }
}

async function verifiedResponse(response, asset) {
  if (!response?.ok) return null;
  const bytes = await response.arrayBuffer();
  if (bytes.byteLength !== asset.byteLength || await sha256(bytes) !== asset.sha256) return null;
  const headers = { "Content-Type": asset.contentType, "Content-Length": String(asset.byteLength) };
  if (asset.role === "documentHtml") {
    headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; base-uri 'none'; form-action 'none'";
  }
  return new Response(bytes, { headers });
}

function storedAssets(assets) {
  return assets.map(({ url, ...asset }) => asset);
}

async function download(lessonId, packageId = null) {
  status.classList.remove("error");
  status.textContent = text.downloading;
  const manifestUrl = packageId
    ? `/api/v1/public/offline-packages/${encodeURIComponent(packageId)}/manifest`
    : `/api/v1/public/lessons/${encodeURIComponent(lessonId)}/offline-manifest`;
  const response = await fetch(manifestUrl);
  if (!response.ok) throw new Error(`MANIFEST_${response.status}`);
  let manifest = await response.json();
  validateManifest(manifest, lessonId);
  const existingRecords = await records();
  const newerReady = existingRecords.find((record) =>
    record.lessonId === lessonId && record.status === "ready" &&
    record.revision > manifest.publicationRevision);
  if (newerReady) throw new Error("STALE_PACKAGE");
  const existingReady = existingRecords.find((record) => record.packageId === manifest.packageId && record.status === "ready");
  if (existingReady) {
    const cache = await caches.open(`accesslearn-package-${manifest.publicationId}`);
    const complete = (await Promise.all(manifest.assets.map(async (asset) =>
      Boolean(await cache.match(stableUrl(manifest.publicationId, asset.path)))))).every(Boolean);
    if (complete) {
      status.textContent = text.ready;
      await renderLibrary();
      return;
    }
    await caches.delete(`accesslearn-package-${manifest.publicationId}`);
    await removeRecord(existingReady.packageId);
  }
  const temporaryCacheName = `accesslearn-package-temp-${manifest.packageId}`;
  const finalCacheName = `accesslearn-package-${manifest.publicationId}`;
  const temporary = await caches.open(temporaryCacheName);
  let cachedBytes = 0;
  for (const asset of manifest.assets) {
    const request = stableUrl(manifest.publicationId, asset.path);
    const cached = await temporary.match(request);
    if (cached && await verifiedResponse(cached.clone(), asset)) cachedBytes += asset.byteLength;
    else if (cached) await temporary.delete(request);
  }
  const existingFinalCache = await caches.open(finalCacheName);
  let finalBytes = 0;
  for (const asset of manifest.assets) {
    const cached = await existingFinalCache.match(stableUrl(manifest.publicationId, asset.path));
    if (cached && await verifiedResponse(cached.clone(), asset)) finalBytes += asset.byteLength;
  }
  const estimate = await navigator.storage?.estimate?.();
  const peakAdditionalBytes = (manifest.totalBytes - cachedBytes) +
    (manifest.totalBytes - finalBytes) + 1_048_576;
  if (estimate?.quota && estimate.quota - (estimate.usage || 0) < peakAdditionalBytes) throw new Error("QUOTA");
  let completedBytes = 0;
  const completed = [];
  progress.hidden = false;
  progress.max = manifest.totalBytes || 1;
  for (let index = 0; index < manifest.assets.length; index++) {
    let asset = manifest.assets[index];
    const request = new Request(stableUrl(manifest.publicationId, asset.path));
    let cached = await temporary.match(request);
    if (cached && !(await verifiedResponse(cached.clone(), asset))) {
      await temporary.delete(request); cached = null;
    }
    if (!cached) {
      let assetResponse = await fetch(asset.url);
      if (assetResponse.status === 401 || assetResponse.status === 403) {
        const refreshed = await fetch(`/api/v1/public/offline-packages/${encodeURIComponent(manifest.packageId)}/manifest`);
        if (!refreshed.ok) throw new Error(`MANIFEST_${refreshed.status}`);
        const refreshedManifest = await refreshed.json();
        validateManifest(refreshedManifest, lessonId);
        if (refreshedManifest.packageId !== manifest.packageId) throw new Error("PACKAGE_CHANGED");
        manifest = refreshedManifest;
        asset = manifest.assets[index];
        assetResponse = await fetch(asset.url);
      }
      if (!assetResponse.ok) throw new Error(`ASSET_${assetResponse.status}`);
      cached = await verifiedResponse(assetResponse, asset);
      if (!cached) throw new Error("INTEGRITY");
      await temporary.put(request, cached.clone());
    }
    completedBytes += asset.byteLength;
    completed.push(asset.path);
    progress.value = completedBytes;
    await put({ lessonId, packageId: manifest.packageId, publicationId: manifest.publicationId, revision: manifest.publicationRevision,
      title: manifest.title, status: "downloading", completed, totalBytes: manifest.totalBytes, assets: storedAssets(manifest.assets) });
  }
  await put({ lessonId, packageId: manifest.packageId, publicationId: manifest.publicationId, revision: manifest.publicationRevision,
    title: manifest.title, status: "activating", completed, totalBytes: manifest.totalBytes, assets: storedAssets(manifest.assets) });
  const finalCache = await caches.open(finalCacheName);
  for (const request of await temporary.keys()) {
    const cached = await temporary.match(request);
    if (cached) await finalCache.put(request, cached);
  }
  for (const asset of manifest.assets) {
    const cached = await finalCache.match(stableUrl(manifest.publicationId, asset.path));
    if (!cached || !(await verifiedResponse(cached.clone(), asset))) throw new Error("FINAL_INTEGRITY");
  }
  await put({ lessonId, packageId: manifest.packageId, publicationId: manifest.publicationId, revision: manifest.publicationRevision,
    title: manifest.title, status: "ready", completed, totalBytes: manifest.totalBytes,
    assets: storedAssets(manifest.assets), zipUrl: `/api/v1/public/offline-packages/${encodeURIComponent(manifest.packageId)}/package.zip` });
  await caches.delete(temporaryCacheName);
  for (const obsolete of (await records()).filter((record) =>
    record.lessonId === lessonId && record.packageId !== manifest.packageId &&
    record.revision <= manifest.publicationRevision)) {
    await caches.delete(`accesslearn-package-${obsolete.publicationId}`);
    await caches.delete(`accesslearn-package-temp-${obsolete.packageId}`);
    await removeRecord(obsolete.packageId);
  }
  progress.hidden = true;
  status.textContent = text.complete;
  await renderLibrary();
}

async function openLesson(record) {
  const cache = await caches.open(`accesslearn-package-${record.publicationId}`);
  for (const asset of record.assets.filter((item) => item.isRequired)) {
    if (!(await cache.match(stableUrl(record.publicationId, asset.path)))) {
      record.status = "downloading";
      await put(record);
      await renderLibrary();
      throw new Error("OFFLINE_ASSET_EVICTED");
    }
  }
  const metadata = await cache.match(stableUrl(record.publicationId, "lesson.json"));
  if (!metadata) throw new Error("OFFLINE_METADATA");
  const lesson = await metadata.json();
  lessonView.replaceChildren();
  const heading = document.createElement("h2"); heading.textContent = lesson.title; lessonView.append(heading);
  const audioAsset = record.assets.find((asset) => asset.role === "offlineAudio");
  if (audioAsset) {
    const audio = document.createElement("audio"); audio.controls = true; audio.preload = "metadata";
    audio.src = stableUrl(record.publicationId, audioAsset.path);
    const captions = record.assets.filter((asset) => asset.role === "caption");
    for (const caption of captions) {
      const track = document.createElement("track"); track.kind = "captions"; track.srclang = caption.language || "und";
      track.label = caption.language || "Captions"; track.src = stableUrl(record.publicationId, caption.path); audio.append(track);
    }
    lessonView.append(audio);
    if (captions.length > 0) {
      const label = document.createElement("label"); label.textContent = locale === "am" ? "ጽሑፍ መግለጫ" : "Captions";
      const selector = document.createElement("select");
      const off = document.createElement("option"); off.value = ""; off.textContent = locale === "am" ? "ጠፍቷል" : "Off"; selector.append(off);
      for (const caption of captions) {
        const option = document.createElement("option"); option.value = caption.language || "und";
        option.textContent = caption.language || "Captions"; selector.append(option);
      }
      const cue = document.createElement("p"); cue.style.fontSize = "1.25rem"; cue.style.background = "#111";
      cue.style.color = "#fff"; cue.style.padding = ".5rem"; cue.hidden = true;
      selector.onchange = () => {
        for (const track of Array.from(audio.textTracks)) {
          track.mode = selector.value && track.language === selector.value ? "hidden" : "disabled";
          track.oncuechange = () => {
            const values = track.activeCues ? Array.from(track.activeCues).map((item) =>
              typeof item.getCueAsHTML === "function" ? item.getCueAsHTML().textContent || "" : item.text || "") : [];
            cue.textContent = values.join("\n"); cue.hidden = values.length === 0;
          };
        }
      };
      label.append(selector); lessonView.append(label, cue);
      const links = document.createElement("p");
      for (const caption of captions) {
        const link = document.createElement("a"); link.href = stableUrl(record.publicationId, caption.path);
        link.textContent = `${caption.language || "Captions"} VTT`; link.className = "button"; links.append(link, " ");
      }
      lessonView.append(links);
    }
  }
  for (const transcript of record.assets.filter((asset) => asset.role === "transcript")) {
    const response = await cache.match(stableUrl(record.publicationId, transcript.path));
    if (!response) continue;
    const section = document.createElement("section"); section.lang = transcript.language || "";
    const title = document.createElement("h3"); title.textContent = transcript.language || "Transcript";
    const pre = document.createElement("pre"); pre.style.whiteSpace = "pre-wrap"; pre.textContent = await response.text();
    section.append(title, pre); lessonView.append(section);
  }
  for (const documentAsset of record.assets.filter((asset) => asset.role === "documentHtml")) {
    const button = document.createElement("button"); button.textContent = documentAsset.displayName || documentAsset.path;
    button.onclick = () => {
      let frame = lessonView.querySelector("iframe[data-document]");
      if (!frame) {
        frame = document.createElement("iframe"); frame.dataset.document = "true"; frame.setAttribute("sandbox", "");
        frame.style.width = "100%"; frame.style.minHeight = "30rem";
        frame.title = documentAsset.displayName || documentAsset.path; lessonView.append(frame);
      }
      frame.src = stableUrl(record.publicationId, documentAsset.path);
    };
    lessonView.append(button);
  }
  lessonView.hidden = false;
  lessonView.scrollIntoView({ block: "start" });
}

async function removeLesson(record) {
  await caches.delete(`accesslearn-package-${record.publicationId}`);
  await caches.delete(`accesslearn-package-temp-${record.packageId}`);
  await removeRecord(record.packageId);
  lessonView.hidden = true;
  status.textContent = text.removed;
  await renderLibrary();
}

async function renderLibrary() {
  const packages = await records();
  library.replaceChildren(); empty.hidden = packages.length > 0;
  for (const record of packages) {
    const item = document.createElement("li"); item.className = "card";
    const title = document.createElement("h3"); title.textContent = record.title;
    const metadata = document.createElement("p"); metadata.className = "metadata";
    const stateText = record.status === "ready" ? text.ready : text.downloading;
    metadata.textContent = `${stateText} · revision ${record.revision} · ${(record.totalBytes / 1024 / 1024).toFixed(1)} MB${record.updateAvailable ? ` · ${text.updateAvailable}` : ""}`;
    const actions = document.createElement("div"); actions.className = "actions";
    const open = document.createElement("button"); open.textContent = text.open; open.hidden = record.status !== "ready";
    open.onclick = () => openLesson(record).catch(showError);
    const update = document.createElement("button"); update.textContent = record.status === "ready" ? text.update : text.download;
    update.onclick = () => download(
      record.lessonId,
      record.status === "ready" ? null : record.packageId
    ).catch(showError);
    const remove = document.createElement("button"); remove.textContent = text.remove; remove.onclick = () => removeLesson(record).catch(showError);
    const zip = document.createElement("a"); zip.className = "button"; zip.textContent = "ZIP"; zip.href = record.zipUrl || "#";
    zip.hidden = record.status !== "ready";
    actions.append(open, update, remove, zip); item.append(title, metadata, actions); library.append(item);
  }
}

function showError(error) {
  progress.hidden = true;
  status.classList.add("error");
  status.textContent = error?.message === "QUOTA" ? text.quota : text.failed;
  console.error(error);
}

async function checkUpdates() {
  if (!navigator.onLine) return;
  for (const record of (await records()).filter((item) => item.status === "ready")) {
    try {
      const response = await fetch(`/api/v1/public/lessons/${encodeURIComponent(record.lessonId)}/offline-manifest`);
      if (!response.ok) continue;
      const current = await response.json();
      record.updateAvailable = current.publicationId !== record.publicationId;
      await put(record);
    } catch {
    }
  }
  await renderLibrary();
}

async function cleanupOrphanCaches() {
  const stored = await records();
  const packageIds = new Set(stored
    .filter((record) => record.status === "downloading" || record.status === "activating")
    .map((record) => String(record.packageId)));
  const publicationIds = new Set(stored
    .filter((record) => record.status === "ready" || record.status === "activating")
    .map((record) => String(record.publicationId)));
  for (const key of await caches.keys()) {
    if (key.startsWith("accesslearn-package-temp-") && !packageIds.has(key.replace("accesslearn-package-temp-", ""))) {
      await caches.delete(key);
    }
    if (key.startsWith("accesslearn-package-") && !key.startsWith("accesslearn-package-temp-") &&
        !publicationIds.has(key.replace("accesslearn-package-", ""))) {
      await caches.delete(key);
    }
  }
}

document.querySelector("[data-download-form]").addEventListener("submit", (event) => {
  event.preventDefault(); download(lessonInput.value.trim()).catch(showError);
});
renderLibrary().catch(showError);
checkUpdates().catch(() => undefined);
cleanupOrphanCaches().catch(() => undefined);
window.addEventListener("online", () => checkUpdates().catch(() => undefined));
