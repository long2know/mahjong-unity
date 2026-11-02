export function setDrawerAccessibility(drawer: HTMLElement, toggle: HTMLElement | null, open: boolean): void {
  drawer.inert = !open;
  drawer.setAttribute('aria-hidden', String(!open));
  toggle?.setAttribute('aria-expanded', String(open));
  if (!open && drawer.contains(document.activeElement)) toggle?.focus();
}

/** Handle only the open drawer, and let nested widgets consume Escape first. */
export function handleDrawerKey(event: KeyboardEvent, drawer: HTMLElement, close: () => void): void {
  if (event.defaultPrevented || drawer.getAttribute('aria-hidden') !== 'false') return;
  if (event.key === 'Escape') {
    event.preventDefault();
    close();
    return;
  }
  if (event.key !== 'Tab') return;
  const controls = Array.from(drawer.querySelectorAll<HTMLElement>('a[href], button, input, select, textarea, [tabindex]'))
    .filter(element => element.tabIndex >= 0 && !element.matches(':disabled')
      && !element.closest('[hidden], [inert]') && element.getClientRects().length > 0);
  const first = controls[0], last = controls[controls.length - 1];
  if (!first) {
    event.preventDefault();
    drawer.tabIndex = -1;
    drawer.focus();
  } else if (event.shiftKey ? document.activeElement === first || !drawer.contains(document.activeElement)
    : document.activeElement === last || !drawer.contains(document.activeElement)) {
    event.preventDefault();
    (event.shiftKey ? last : first).focus();
  }
}
