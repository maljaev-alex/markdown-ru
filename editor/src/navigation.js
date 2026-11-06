import { modelMap, previewMap } from './navigation-map.js';

export function createNavigation({ editor, source, surface, nativePreview, current, flush, post }) {
  let applying = false, disposed = false, timer, pending = {}, last, mapCache, previewCache, requested;
  let scrollExpected, sending = false;
  const caret = document.createElement('div'); caret.className = 'am-synced-caret'; caret.hidden = true;
  caret.setAttribute('aria-hidden', 'true'); document.body.append(caret);
  const listeners = [];
  const on = (target, name, handler, options) => {
    target.addEventListener(name, handler, options);
    listeners.push(() => target.removeEventListener(name, handler, options));
  };
  function enabled() {
    const value = current();
    return !disposed && value.state && !value.loading && !value.state.conflict && !value.state.snapshot.translated;
  }
  function map() {
    const { state, preview } = current();
    if (preview) {
      if (!previewCache || previewCache.text !== state.text || previewCache.first !== nativePreview.firstChild)
        previewCache = { text: state.text, first: nativePreview.firstChild, map: previewMap(nativePreview, state.text) };
      return previewCache.map;
    }
    if (!mapCache || mapCache.version !== editor.model.document.version)
      mapCache = { version: editor.model.document.version, map: modelMap(editor, source) };
    return mapCache.map;
  }
  function domPosition(offset, affinity) {
    const position = map().toPosition(offset, affinity);
    if (current().preview) return position;
    const view = editor.editing.mapper.toViewPosition(position);
    const dom = editor.editing.view.domConverter.viewPositionToDom(view);
    return { node: dom.parent, offset: dom.offset };
  }
  function rangeFor(anchor, focus) {
    const start = domPosition(Math.min(anchor, focus), 1), end = domPosition(Math.max(anchor, focus), -1);
    const range = document.createRange();
    range.setStart(start.node, start.offset); range.setEnd(end.node, end.offset);
    return range;
  }
  function clearHighlight() { CSS.highlights?.delete('am-synced-selection'); caret.hidden = true; }
  function paint() {
    clearHighlight();
    if (!last || !enabled() || current().state.snapshot.syncSelection === false) return;
    const range = rangeFor(last.anchor, last.focus);
    if (last.anchor !== last.focus) {
      if (window.Highlight) CSS.highlights.set('am-synced-selection', new Highlight(range));
    } else {
      let rect = range.getClientRects()[0];
      if (!rect?.height) rect = (range.startContainer.nodeType === 3 ? range.startContainer.parentElement : range.startContainer).getBoundingClientRect();
      const bounds = current().preview ? { top: 0, bottom: innerHeight } : surface.getBoundingClientRect();
      if (rect && rect.height && rect.bottom > bounds.top && rect.top < bounds.bottom) {
        Object.assign(caret.style, { left: rect.left + 'px', top: Math.max(bounds.top, rect.top) + 'px',
          height: Math.min(rect.bottom, bounds.bottom) - Math.max(bounds.top, rect.top) + 'px' });
        caret.hidden = false;
      }
    }
  }
  function scrollValue() { return current().preview ? window.scrollY : surface.scrollTop; }
  function viewport() { return current().preview ? { top: 0, bottom: innerHeight, left: 0, right: innerWidth } : surface.getBoundingClientRect(); }
  function scrollTo(offset, reveal) {
    const rect = rangeFor(offset, offset).getClientRects()[0];
    if (!rect) return;
    const bounds = viewport();
    let delta = rect.top - bounds.top - 8;
    if (reveal) {
      if (rect.top >= bounds.top + 8 && rect.bottom <= bounds.bottom - 8) return;
      if (rect.bottom > bounds.bottom - 8) delta = rect.bottom - bounds.bottom + 8;
    }
    if (current().preview) window.scrollBy(0, delta); else surface.scrollTop += delta;
    scrollExpected = scrollValue();
  }
  function readSelection() {
    const positions = map();
    let anchor, focus;
    if (current().preview) {
      const selection = window.getSelection();
      if (!selection?.rangeCount || !nativePreview.contains(selection.anchorNode) || !nativePreview.contains(selection.focusNode)) return null;
      const backward = !selection.isCollapsed && selection.getRangeAt(0).startContainer === selection.focusNode && selection.getRangeAt(0).startOffset === selection.focusOffset;
      anchor = positions.toSource(selection.anchorNode, selection.anchorOffset, backward ? -1 : 1);
      focus = positions.toSource(selection.focusNode, selection.focusOffset, backward ? 1 : -1);
    } else {
      const selection = editor.model.document.selection;
      if (!selection.anchor || !selection.focus) return null;
      anchor = positions.toSource(selection.anchor, selection.isBackward ? -1 : 1);
      focus = positions.toSource(selection.focus, selection.isBackward ? 1 : -1);
    }
    if (anchor == null || focus == null) return null;
    if ((current().preview ? window.getSelection().isCollapsed : editor.model.document.selection.isCollapsed)) focus = anchor;
    return { anchor, focus };
  }
  function topOffset() {
    const bounds = viewport(), root = current().preview ? nativePreview : editor.ui.getEditableElement();
    // Hit testing follows wrapped lines too. Find a text position near the first
    // visible baseline; indentation/list markers may occupy the leftmost pixels.
    for (const y of [bounds.top + 10, bounds.top + 24, bounds.top + 40]) {
      for (const x of [bounds.left + 48, bounds.left + 100, (bounds.left + bounds.right) / 2]) {
        const range = document.caretRangeFromPoint(x, y);
        if (!range || !root.contains(range.startContainer)) continue;
        if (current().preview) return map().toSource(range.startContainer, range.startOffset) ?? 0;
        const view = editor.editing.view.domConverter.domPositionToView(range.startContainer, range.startOffset);
        if (view) {
          const position = editor.editing.mapper.toModelPosition(view);
          return map().toSource(position);
        }
      }
    }
    return last?.top ?? 0;
  }
  function capture(selection = true, scroll = false) {
    if (!enabled()) return null;
    const { state } = current(), selected = selection ? readSelection() : null;
    return { session: state.snapshot.session, revision: state.snapshot.revision,
      anchor: selected?.anchor ?? last?.anchor ?? 0, focus: selected?.focus ?? last?.focus ?? 0,
      top: scroll ? topOffset() : 0, selection: !!selected && state.snapshot.syncSelection !== false,
      scroll: scroll && state.snapshot.syncScroll !== false, reveal: !!selected && !scroll };
  }
  function schedule(selection, scroll) {
    if (applying || !enabled()) return;
    pending.selection ||= selection; pending.scroll ||= scroll;
    clearTimeout(timer); timer = setTimeout(send, 45);
  }
  async function send() {
    if (sending || !enabled()) return;
    const state = current().state;
    sending = true;
    try {
      if (!await flush(true) || current().state !== state || !enabled()) { pending = {}; return; }
      const next = capture(pending.selection, pending.scroll); pending = {};
      if (next && (next.selection || next.scroll)) {
        last = next; clearHighlight(); post({ action: 'navigation', state: next });
      }
    } finally { sending = false; }
  }
  function receive(state) {
    if (!enabled()) return;
    const active = current().state;
    if (state.session !== active.snapshot.session || state.revision !== active.snapshot.revision || active.inFlight || active.text !== active.base) return;
    applying = true;
    clearTimeout(timer); pending = {};
    try {
      last = { ...last, ...state,
        anchor: state.selection ? state.anchor : last?.anchor ?? state.anchor,
        focus: state.selection ? state.focus : last?.focus ?? state.focus };
      if (state.selection && active.snapshot.syncSelection !== false) {
        if (current().preview) window.getSelection()?.removeAllRanges();
        else {
          const positions = map(), backward = state.anchor > state.focus;
          const anchor = positions.toPosition(state.anchor, backward ? -1 : 1);
          const focus = state.focus === state.anchor ? anchor : positions.toPosition(state.focus, backward ? 1 : -1);
          // CKEditor ignores a direction-only change when the range is equal.
          // Set the anchor first, then extend the focus, within one model batch.
          editor.model.change(writer => { writer.setSelection(anchor); writer.setSelectionFocus(focus); });
        }
      }
      if (state.scroll && active.snapshot.syncScroll !== false) scrollTo(state.top, false);
      else if (state.selection && state.reveal) scrollTo(state.focus, true);
      paint();
      scrollExpected = scrollValue();
    } finally { applying = false; }
  }
  function refresh(modeChanged = false) {
    previewCache = null; mapCache = null;
    if (!enabled()) { last = null; clearHighlight(); return; }
    const { state } = current(), key = [state.snapshot.session, state.snapshot.revision, state.snapshot.syncSelection, state.snapshot.syncScroll, state.snapshot.preserveLineBreaks].join(':');
    if (last?.session !== state.snapshot.session) { last = null; clearHighlight(); }
    if (requested !== key && !state.inFlight && state.text === state.base) { clearTimeout(timer); pending = {}; }
    if (modeChanged && last?.session === state.snapshot.session) receive({ ...last, selection: true, scroll: true });
    if (requested !== key || modeChanged) {
      requested = key;
      post({ action: 'navigation', state: { session: state.snapshot.session, revision: state.snapshot.revision, request: true } });
    }
  }
  function beforeModeChange() {
    clearTimeout(timer); pending = {};
    const next = capture(true, true);
    if (!next) return;
    if (!next.selection && last) { next.anchor = last.anchor; next.focus = last.focus; }
    last = next;
    if (next.selection || next.scroll) post({ action: 'navigation', state: next });
  }
  const selectionChanged = () => { if (!current().preview) schedule(true, false); };
  editor.model.document.selection.on('change:range', selectionChanged);
  on(document, 'selectionchange', () => { if (current().preview && readSelectionSafe()) schedule(true, false); });
  function readSelectionSafe() { return enabled() && readSelection(); }
  on(document, 'pointerdown', event => {
    if (nativePreview.contains(event.target) || surface.contains(event.target)) { last = null; clearHighlight(); }
  }, true);
  on(document, 'pointerup', event => {
    if (nativePreview.contains(event.target) || surface.contains(event.target)) schedule(true, false);
  });
  on(document, 'keyup', event => {
    if (nativePreview.contains(event.target) || surface.contains(event.target)) schedule(true, false);
  });
  const onScroll = () => {
    if (last) paint();
    if (scrollValue() === scrollExpected) return;
    scrollExpected = undefined; schedule(false, true);
  };
  on(window, 'scroll', () => { if (current().preview) onScroll(); }, { passive: true });
  on(surface, 'scroll', () => { if (!current().preview) onScroll(); }, { passive: true });
  on(window, 'resize', () => { if (last) paint(); });
  return { receive, refresh, capture, send, beforeModeChange,
    dispose() { disposed = true; clearTimeout(timer); listeners.forEach(remove => remove()); editor.model.document.selection.off('change:range', selectionChanged); clearHighlight(); caret.remove(); } };
}
