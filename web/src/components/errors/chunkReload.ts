const KEY = 'finsight.chunk-reload-at';
/** A second failure this soon after an automatic reload shows the update screen instead of reloading again. */
export const AUTO_RELOAD_WINDOW_MS = 60_000;

/**
 * Whether to reload by itself after a page's code failed to load, recording the attempt. Allowed once per window
 * so a deploy that is still rolling out (or a dev server that is down) can't cause a reload loop. Without
 * sessionStorage it never reloads automatically.
 */
export function claimAutoReload(now = Date.now()): boolean {
  try {
    const last = Number(sessionStorage.getItem(KEY) ?? 0);
    if (Number.isFinite(last) && now - last < AUTO_RELOAD_WINDOW_MS) return false;
    sessionStorage.setItem(KEY, String(now));
    return true;
  } catch {
    return false;
  }
}

export function reloadPage() {
  window.location.reload();
}
