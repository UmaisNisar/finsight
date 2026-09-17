import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
import { afterEach } from 'vitest';

/*
  jsdom lacks several browser APIs the UI relies on. These shims implement just the observable behaviour the
  components use (open state and the events they listen for), so tests exercise real component logic.
*/

// <dialog>: showModal/close toggle the open attribute; close fires a "close" event, as in browsers.
if (typeof HTMLDialogElement !== 'undefined' && !HTMLDialogElement.prototype.showModal) {
  HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) {
    this.setAttribute('open', '');
  };
  HTMLDialogElement.prototype.show = HTMLDialogElement.prototype.showModal;
  HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
    if (!this.hasAttribute('open')) return;
    this.removeAttribute('open');
    this.dispatchEvent(new Event('close'));
  };
}

// Popover API: fire beforetoggle and toggle synchronously (browsers queue toggle; components handle both).
if (!HTMLElement.prototype.showPopover) {
  const fire = (el: HTMLElement, newState: 'open' | 'closed') => {
    el.dispatchEvent(Object.assign(new Event('beforetoggle'), { newState }));
    el.dispatchEvent(Object.assign(new Event('toggle'), { newState }));
  };
  HTMLElement.prototype.showPopover = function (this: HTMLElement) {
    fire(this, 'open');
  };
  HTMLElement.prototype.hidePopover = function (this: HTMLElement) {
    fire(this, 'closed');
  };
}

if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

if (!window.matchMedia) {
  window.matchMedia = (query: string) =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => false,
    }) as MediaQueryList;
}

Element.prototype.scrollIntoView ??= () => undefined;

afterEach(() => {
  cleanup();
  localStorage.clear();
  sessionStorage.clear();
});
