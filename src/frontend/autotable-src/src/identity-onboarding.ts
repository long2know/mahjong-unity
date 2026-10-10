// Phase K Wave 22 — Hicks (Frontend), bundle-audit §3.7.
//
// Lazy-loaded onboarding card.  Extracted from `identity.ts` so the
// eager lobby cold path no longer pays for the ~6-7 KB minified
// install-and-wire path (Continue button → hub bind → optimistic
// profile + UpdateProfile invoke).  The card is shown only on the
// very first visit (cookie-less + LS_KEY_ONBOARDED absent); on every
// subsequent visit `shouldShowOnboarding()` returns false and this
// module is never imported.
//
// Public entry: `installOnboardingCard()`.  Idempotent — the lobby
// (lobby.ts:initLobby) dynamic-imports this module + calls the
// installer immediately + then again every time the identity event
// fires; the installer guards against double-wiring via its own
// `onboardingInstalled` flag.

import { getHubConnection, invokeHub } from './hub';
import {
  AVATAR_COLOR_PRESETS,
  DISPLAY_NAME_MAX,
  DISPLAY_NAME_MIN,
  getProfile,
  initProfileHubBindings,
  setAvatarColor as setProfileAvatarColor,
  setDisplayName as setProfileDisplayName,
  validateAvatarColor,
  validateDisplayName,
} from './profile';
import { showEl, hideEl } from './dom-utils';
import {
  getIdentity,
  onIdentity,
  shouldShowOnboarding,
  applyOnboardingProfile,
  markOnboardingCompleteExported,
} from './identity';

let onboardingInstalled = false;

/**
 * Mount the onboarding card.  Idempotent — bails out if the markup
 * isn't present or if it's already wired.  When `shouldShowOnboarding`
 * returns false the card stays hidden.
 *
 * Bishop's `UpdateProfile` SignalR RPC is the canonical writer for
 * displayName + avatarColor — we route the Continue button through
 * there so the cookie-bound identity stays in sync with the
 * connection-id profile that drives the rest of the UI.
 */
export function installOnboardingCard(): void {
  if (onboardingInstalled) return;
  const card = document.getElementById('onboarding-card');
  if (card === null) return;
  onboardingInstalled = true;

  const nameInput = document.getElementById(
    'onboarding-display-name-input') as HTMLInputElement | null;
  const nameError = document.getElementById('onboarding-display-name-error');
  const presetsHost = document.getElementById('onboarding-avatar-presets');
  const customColor = document.getElementById(
    'onboarding-avatar-color-custom') as HTMLInputElement | null;
  const previewAvatar = document.getElementById('onboarding-preview-avatar');
  const continueBtn = document.getElementById(
    'onboarding-continue') as HTMLButtonElement | null;
  const skipBtn = document.getElementById(
    'onboarding-skip') as HTMLButtonElement | null;

  const initial = getIdentity();
  let selectedColor: string =
    initial !== null ? initial.avatarColor : AVATAR_COLOR_PRESETS[5];

  const refreshPreview = (): void => {
    if (previewAvatar !== null) {
      (previewAvatar as HTMLElement).style.backgroundColor = selectedColor;
      const name = nameInput?.value.trim() ?? '';
      previewAvatar.textContent = onboardingInitial(name);
    }
    if (presetsHost !== null) {
      for (const btn of presetsHost.querySelectorAll<HTMLButtonElement>(
        '.onboarding-avatar-preset')) {
        const matches = btn.getAttribute('data-color')?.toLowerCase()
          === selectedColor.toLowerCase();
        btn.classList.toggle('onboarding-avatar-preset-selected', matches);
        btn.setAttribute('aria-checked', matches ? 'true' : 'false');
      }
    }
    if (customColor !== null && document.activeElement !== customColor) {
      customColor.value = selectedColor;
    }
  };

  if (presetsHost !== null) {
    presetsHost.replaceChildren();
    AVATAR_COLOR_PRESETS.forEach((hex, idx) => {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'onboarding-avatar-preset';
      btn.style.backgroundColor = hex;
      btn.setAttribute('data-color', hex);
      btn.setAttribute('data-testid', `onboarding-avatar-color-preset-${idx}`);
      btn.setAttribute('role', 'radio');
      btn.setAttribute('aria-checked', 'false');
      btn.setAttribute('aria-label', `Preset colour ${idx + 1}`);
      btn.title = hex;
      btn.addEventListener('click', () => {
        selectedColor = hex;
        refreshPreview();
      });
      presetsHost.appendChild(btn);
    });
  }

  if (customColor !== null) {
    customColor.addEventListener('input', () => {
      if (!validateAvatarColor(customColor.value)) return;
      selectedColor = customColor.value.toLowerCase();
      refreshPreview();
    });
  }

  if (nameInput !== null) {
    nameInput.setAttribute('minlength', String(DISPLAY_NAME_MIN));
    nameInput.setAttribute('maxlength', String(DISPLAY_NAME_MAX));
    nameInput.addEventListener('input', () => {
      const { error } = validateDisplayName(nameInput.value);
      if (nameError !== null) nameError.textContent = error ?? '';
      nameInput.classList.toggle('onboarding-input-invalid', error !== null);
      refreshPreview();
    });
  }

  if (continueBtn !== null) {
    continueBtn.addEventListener('click', () => {
      const rawName = nameInput?.value ?? '';
      const { value, error } = validateDisplayName(rawName);
      if (error !== null || value === null) {
        if (nameError !== null) nameError.textContent = error ?? 'Enter a name.';
        nameInput?.focus();
        return;
      }
      const expanded = expandHexColor(selectedColor).toUpperCase();
      applyOnboardingProfile(value, selectedColor);
      void applyProfileFromOnboarding(value, selectedColor, expanded);
      markOnboardingCompleteExported();
      hideOnboardingCard();
    });
  }

  if (skipBtn !== null) {
    skipBtn.addEventListener('click', () => {
      markOnboardingCompleteExported();
      hideOnboardingCard();
    });
  }

  // Populate the input with whatever default name the backend gave us
  // so Continue with no edits still ends up with a sensible name.
  if (initial !== null && nameInput !== null && nameInput.value === '') {
    nameInput.value = initial.displayName;
  }
  refreshPreview();

  onIdentity((id) => {
    if (nameInput !== null && document.activeElement !== nameInput
        && nameInput.value === '') {
      nameInput.value = id.displayName;
    }
    refreshPreview();
  });

  // Visibility: shown only when shouldShowOnboarding() returns true.
  if (shouldShowOnboarding()) {
    showOnboardingCard();
  } else {
    hideOnboardingCard();
  }
}

function showOnboardingCard(): void {
  const card = document.getElementById('onboarding-card');
  if (card === null) return;
  showEl(card);
  card.setAttribute('aria-hidden', 'false');
}

function hideOnboardingCard(): void {
  const card = document.getElementById('onboarding-card');
  if (card === null) return;
  hideEl(card);
  card.setAttribute('aria-hidden', 'true');
}

function onboardingInitial(name: string): string {
  const trimmed = name.trim();
  if (trimmed === '') return '?';
  const parts = trimmed.split(/\s+/);
  if (parts.length === 1) return parts[0].charAt(0).toUpperCase();
  return (parts[0].charAt(0) + parts[parts.length - 1].charAt(0)).toUpperCase();
}

function expandHexColor(hex: string): string {
  if (/^#[0-9a-fA-F]{3}$/.test(hex)) {
    const r = hex.charAt(1);
    const g = hex.charAt(2);
    const b = hex.charAt(3);
    return `#${r}${r}${g}${g}${b}${b}`;
  }
  return hex;
}

/**
 * Push the onboarding-form values into the Wave-5 profile cache so
 * the lobby's profile chip + the move-log nick map re-render with
 * the chosen name/colour.  Steps:
 *
 *   1.  Install the `ProfileLoaded` listener (profile.ts only wires
 *       this once the hub is up).
 *   2.  Force a hub connection — the server's `OnConnectedAsync`
 *       fires a `ProfileLoaded` event with the *default* name, which
 *       seeds profile.ts's local cache so `setDisplayName` /
 *       `setAvatarColor` aren't no-ops.
 *   3.  Poll briefly for the seed event to land (it travels over
 *       the same SignalR WebSocket as the connect, so we usually
 *       see it within a single tick).
 *   4.  Apply the onboarding values through the profile module's
 *       public setters; their optimistic local-update path fires
 *       `onProfile` listeners immediately, and the debounced
 *       `UpdateProfile` invoke commits to the server.
 *   5.  As a defensive belt-and-braces measure (e.g. if SignalR is
 *       unreachable in tests), invoke `UpdateProfile` directly so
 *       the server still gets the new name even when profile.ts
 *       falls through.
 */
async function applyProfileFromOnboarding(
  displayName: string,
  selectedColor: string,
  expandedColor: string,
): Promise<void> {
  try {
    initProfileHubBindings();
    await getHubConnection();
  } catch {
    // Hub unreachable — fall through to the direct invoke below;
    // the local identity cache is still updated by the caller.
  }
  const deadline = Date.now() + 2000;
  while (getProfile() === null && Date.now() < deadline) {
    await new Promise<void>((resolve) => setTimeout(resolve, 50));
  }
  const nameResult = setProfileDisplayName(displayName);
  const colorResult = setProfileAvatarColor(selectedColor);
  if (nameResult.error !== null || colorResult.error !== null) {
    // profile.ts hadn't loaded — push the values to the hub
    // ourselves so the server-side record still picks them up.
    try {
      await invokeHub('UpdateProfile', displayName, expandedColor);
    } catch {
      /* swallow — the local identity cache is the source of truth
         until the hub is reachable */
    }
  }
}
