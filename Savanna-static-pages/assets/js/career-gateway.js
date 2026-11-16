(() => {
  'use strict';

  const buttons = [...document.querySelectorAll('[data-listen-summary]')];
  const speechSupported = 'speechSynthesis' in window && 'SpeechSynthesisUtterance' in window;
  let activeSpeech = null;

  function setButtonState(button, speaking) {
    const label = button.querySelector('[data-speech-label]');
    button.setAttribute('aria-pressed', String(speaking));
    if (label) label.textContent = speaking ? 'Stop summary' : 'Listen to summary';
  }

  function stopSpeech(message) {
    const previous = activeSpeech;
    activeSpeech = null;
    if (speechSupported) window.speechSynthesis.cancel();
    if (!previous) return;
    setButtonState(previous.button, false);
    if (message) previous.status.textContent = message;
  }

  buttons.forEach((button) => {
    const card = button.closest('[data-job-card]');
    const summary = card && card.querySelector('[data-job-summary]');
    const title = card && card.querySelector('h3');
    const employer = card && card.querySelector('[data-job-employer]');
    const status = card && card.querySelector('[data-speech-status]');
    if (!card || !summary || !title || !employer || !status) return;

    if (!speechSupported) {
      button.disabled = true;
      status.textContent = 'Audio summaries are not supported by this browser. The full text remains available.';
      return;
    }

    button.addEventListener('click', () => {
      if (activeSpeech && activeSpeech.button === button) {
        stopSpeech('Audio summary stopped.');
        return;
      }

      stopSpeech();
      const utterance = new SpeechSynthesisUtterance(
        `${title.textContent}. ${employer.textContent}. ${summary.textContent}`
      );
      utterance.lang = card.lang || document.documentElement.lang || 'en';
      utterance.rate = 0.95;
      const speech = { button, status, utterance };
      activeSpeech = speech;

      utterance.addEventListener('start', () => {
        if (activeSpeech !== speech) return;
        setButtonState(button, true);
        status.textContent = `Playing summary for ${title.textContent}.`;
      });
      utterance.addEventListener('end', () => {
        if (activeSpeech !== speech) return;
        activeSpeech = null;
        setButtonState(button, false);
        status.textContent = 'Audio summary finished.';
      });
      utterance.addEventListener('error', () => {
        if (activeSpeech !== speech) return;
        activeSpeech = null;
        setButtonState(button, false);
        status.textContent = 'The audio summary could not be played. The full text remains available.';
      });

      window.speechSynthesis.speak(utterance);
    });
  });

  window.addEventListener('pagehide', () => stopSpeech(), { once: true });
})();
