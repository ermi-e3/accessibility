(() => {
  'use strict';

  const verifier = document.querySelector('[data-certificate-verifier]');
  const certificate = document.querySelector('[data-certificate-record]');
  const status = document.querySelector('[data-verification-status]');
  const input = document.querySelector('[data-credential-input]');
  const verificationLink = document.querySelector('[data-verification-link]');
  const copyButton = document.querySelector('[data-copy-verification]');
  const copyStatus = document.querySelector('[data-copy-status]');
  if (!verifier || !certificate || !status || !input || !verificationLink) return;

  const expectedId = verifier.dataset.credentialId;
  const suppliedId = new URLSearchParams(window.location.search).get('id') || '';
  input.value = suppliedId;

  const shareUrl = new URL(window.location.href);
  shareUrl.search = '';
  shareUrl.searchParams.set('id', expectedId);
  verificationLink.href = shareUrl.href;
  verificationLink.textContent = shareUrl.href;

  if (!suppliedId) {
    certificate.hidden = true;
    status.dataset.state = 'invalid';
    status.textContent = 'Enter a credential ID to check the public certificate registry.';
  } else if (suppliedId === expectedId) {
    certificate.hidden = false;
    status.dataset.state = 'verified';
    status.textContent = 'Verified: this credential matches the public AccessLearn registry record.';
  } else {
    certificate.hidden = true;
    status.dataset.state = 'invalid';
    status.textContent = 'Not verified: no public certificate matches that credential ID.';
  }

  if (!copyButton || !copyStatus || !navigator.clipboard) {
    if (copyButton) copyButton.hidden = true;
    return;
  }

  copyButton.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(shareUrl.href);
      copyStatus.textContent = 'Verification link copied.';
    } catch {
      copyStatus.textContent = 'Copy failed. Select the verification link instead.';
    }
  });
})();
