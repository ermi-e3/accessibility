(() => {
  'use strict';

  const query = new URLSearchParams(window.location.search);

  function readProgress(key) {
    try {
      return JSON.parse(localStorage.getItem(key) || 'null');
    } catch {
      return null;
    }
  }

  function writeProgress(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch {
      // Progress remains visible for this visit when storage is unavailable.
    }
  }

  function isEventDetail(value) {
    return value &&
      typeof value.lessonId === 'string' &&
      typeof value.publicationId === 'string' &&
      Number.isFinite(value.percent);
  }

  function configureIntegration(root) {
    const player = root.querySelector('accesslearn-player');
    const instructions = root.querySelector('[data-player-instructions]');
    const progress = root.querySelector('[data-lesson-progress]');
    const progressValue = root.querySelector('[data-lesson-progress-value]');
    const status = root.querySelector('[data-lesson-progress-status]');
    if (!player || !progress || !progressValue || !status) return;

    const lessonId = query.get('lesson') || root.dataset.lessonId || '';
    const locale = query.get('locale') === 'am' ? 'am' : 'en';
    const configuredApiBase = root.dataset.apiBase;
    const configuredSdkSource = root.dataset.sdkSrc;
    const apiBase = configuredApiBase === 'same-origin' && ['http:', 'https:'].includes(window.location.protocol)
      ? window.location.origin
      : configuredApiBase;
    const sdkSource = configuredSdkSource ? new URL(configuredSdkSource, document.baseURI).href : '';

    function renderPercent(percent, message) {
      const safePercent = Math.max(0, Math.min(100, Math.round(percent)));
      progress.value = safePercent;
      progressValue.value = `${safePercent}%`;
      progressValue.textContent = `${safePercent}%`;
      status.textContent = message;
    }

    root.addEventListener('db-player:progress', (event) => {
      const detail = event.detail;
      if (!isEventDetail(detail)) return;
      const storageKey = `accesslearn:lesson-progress:${detail.lessonId}`;
      const previous = readProgress(storageKey);
      writeProgress(storageKey, {
        ...detail,
        completed: Boolean(previous && previous.completed),
        updatedAt: new Date().toISOString()
      });
      renderPercent(detail.percent, `Lesson progress: ${Math.round(detail.percent)}%.`);
    });

    root.addEventListener('db-player:complete', (event) => {
      const detail = event.detail;
      if (!isEventDetail(detail)) return;
      writeProgress(`accesslearn:lesson-progress:${detail.lessonId}`, {
        ...detail,
        completed: true,
        updatedAt: detail.completedAt || new Date().toISOString()
      });
      root.dataset.lessonComplete = 'true';
      renderPercent(100, 'Lesson complete. Career opportunities are available below.');
    });

    root.addEventListener('accesslearn:error', () => {
      status.textContent = 'The player could not update lesson progress. Try loading the lesson again.';
    });

    if (!lessonId) {
      player.hidden = true;
      renderPercent(0, 'Player not loaded. Add ?lesson=<published-lesson-id> to this page URL.');
      return;
    }

    if (!apiBase || !sdkSource) {
      player.hidden = true;
      renderPercent(0, 'Player host configuration is incomplete.');
      return;
    }

    const saved = readProgress(`accesslearn:lesson-progress:${lessonId}`);
    if (saved && Number.isFinite(saved.percent)) {
      renderPercent(saved.percent, saved.completed
        ? 'Lesson complete. Career opportunities are available below.'
        : `Saved lesson progress: ${Math.round(saved.percent)}%.`);
    }

    player.hidden = false;
    if (instructions) instructions.hidden = true;

    if (customElements.get('accesslearn-player') && window.AccessLearn?.mount) {
      try {
        window.AccessLearn.mount(player, { lessonId, apiBase, locale });
      } catch {
        status.textContent = 'The AccessLearn player could not be mounted.';
      }
      return;
    }

    player.setAttribute('lesson-id', lessonId);
    player.setAttribute('api-base', apiBase);
    player.setAttribute('locale', locale);
    player.dataset.lessonId = lessonId;
    player.dataset.apiBase = apiBase;
    player.dataset.locale = locale;
    const existing = [...document.querySelectorAll('script[data-accesslearn-sdk]')]
      .some((candidate) => candidate.dataset.accesslearnSdk === sdkSource);
    if (existing) return;

    const script = document.createElement('script');
    script.src = sdkSource;
    script.defer = true;
    script.dataset.accesslearnSdk = sdkSource;
    script.addEventListener('error', () => {
      player.hidden = true;
      status.textContent = 'The AccessLearn player script could not be loaded.';
    });
    document.head.append(script);
  }

  document.querySelectorAll('[data-accesslearn-integration]').forEach(configureIntegration);
})();
