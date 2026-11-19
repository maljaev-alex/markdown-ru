import { formattingPositionMap, modelMap, previewMap } from './navigation-map.js';
import { createScrollMap } from './scroll-map.js';

export function createNavigation({ editor, source, surface, nativePreview, current, flush, post }) {
  let applying = false, disposed = false, timer, pending = {}, last, mapCache, previewCache, requested;
  let frame, scrollCache, lastHorizontalScroller, lastWheel = -Infinity, paintedNode, paintedOffset, previewCaret = null, sending = false;
  const scrollPositions = new WeakMap();
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
    const start = domPosition(Math.min(anchor, focus), 1);
    const range = document.createRange();
    range.setStart(start.node, start.offset);
    if (anchor === focus) range.collapse(true);
    else {
      const end = domPosition(Math.max(anchor, focus), -1);
      range.setEnd(end.node, end.offset);
    }
    return range;
  }
  function hideCaret() {
    caret.hidden = true;
    paintedNode = null;
    document.body.classList.remove('am-draws-caret');
  }
  function clearHighlight() { CSS.highlights?.delete('am-synced-selection'); hideCaret(); }
  function documentScroller() {
    return document.scrollingElement || (document.compatMode === 'BackCompat' ? document.body : document.documentElement);
  }
  function scroller() { return current().preview ? documentScroller() : surface; }
  function viewport(element = scroller()) {
    if (element === documentScroller()) {
      // In the shipped loader's quirks mode, html.clientHeight includes the entire document.
      const client = document.compatMode === 'BackCompat' ? document.body : document.documentElement;
      return { left: 0, top: 0, right: client.clientWidth, bottom: client.clientHeight };
    }
    const rect = element.getBoundingClientRect(), left = rect.left + element.clientLeft, top = rect.top + element.clientTop;
    return { left, top, right: left + element.clientWidth, bottom: top + element.clientHeight };
  }
  function scrollParents(range) {
    const outer = scroller(), parents = [];
    let element = range.startContainer.nodeType === Node.ELEMENT_NODE ? range.startContainer : range.startContainer.parentElement;
    for (; element && element !== outer; element = element.parentElement) {
      if (element.scrollWidth <= element.clientWidth && element.scrollHeight <= element.clientHeight) continue;
      const style = getComputedStyle(element);
      if (/auto|scroll|overlay/.test(style.overflowX + ' ' + style.overflowY)) parents.push(element);
    }
    parents.push(outer);
    return parents;
  }
  function caretRect(range) {
    const rect = range.getClientRects()[0];
    if (rect?.height) return rect;
    const node = range.startContainer;
    const box = (node.nodeType === Node.TEXT_NODE ? node.parentElement : node).getBoundingClientRect();
    return { top: box.top, bottom: box.bottom, left: box.left, right: box.left, height: box.height };
  }
  function visibleCaret(range) {
    const rect = caretRect(range);
    let top = rect.top, bottom = rect.bottom;
    for (const element of scrollParents(range)) {
      const box = viewport(element);
      if (rect.left < box.left || rect.left >= box.right) return null;
      top = Math.max(top, box.top); bottom = Math.min(bottom, box.bottom);
    }
    return bottom > top ? { left: rect.left, top, height: bottom - top } : null;
  }
  function paintCaret(range, local, editing) {
    CSS.highlights?.delete('am-synced-selection');
    const box = visibleCaret(range);
    if (!box) { hideCaret(); return; }
    // Scintilla uses device pixels. WebView2 also includes its zoom factor in devicePixelRatio.
    const scale = window.devicePixelRatio;
    Object.assign(caret.style, { left: Math.round(box.left * scale) / scale + 'px', top: box.top + 'px',
      width: 2 / scale + 'px', height: box.height + 'px' });
    if (!caret.hidden && (paintedNode !== range.startContainer || paintedOffset !== range.startOffset)) {
      const blink = caret.getAnimations().find(animation => animation.animationName === 'am-marker-blink');
      if (blink) blink.currentTime = 0;
    }
    paintedNode = range.startContainer; paintedOffset = range.startOffset;
    caret.classList.toggle('am-editing-caret', editing && local);
    caret.hidden = false;
    document.body.classList.toggle('am-draws-caret', local);
  }
  function paint() {
    const { state, preview, loading } = current();
    if (disposed || loading || !state) { clearHighlight(); return; }
    const editingFocus = !preview && editor.editing.view.document.isFocused && document.hasFocus();
    const dom = window.getSelection();
    const root = preview ? nativePreview : editor.ui.getEditableElement();
    const local = preview ? document.hasFocus() && previewTarget(document.activeElement)
      : editingFocus && root.contains(document.activeElement) && document.activeElement.isContentEditable;
    if (local && dom?.rangeCount && dom.isCollapsed && root.contains(dom.focusNode) &&
        (preview || editor.model.document.selection.isCollapsed)) {
      // Use the browser's live insertion point (also during typing and IME).
      paintCaret(dom.getRangeAt(0), true, !preview);
      return;
    }
    if (!last || !enabled() || state.snapshot.syncSelection === false) { clearHighlight(); return; }
    const range = rangeFor(last.anchor, last.focus);
    if (last.anchor !== last.focus) {
      hideCaret();
      if (window.Highlight) CSS.highlights.set('am-synced-selection', new Highlight(range));
    } else if (!editingFocus) paintCaret(range, false, !preview);
    else clearHighlight();
  }
  function revealDelta(start, end, min, max) {
    const margin = Math.min(8, (max - min) / 4);
    if (start < min + margin) return start - min - margin;
    if (end > max - margin) return end - max + margin;
    return 0;
  }
  function scrollTo(offset, reveal, anchor = offset, horizontal = reveal, vertical = true) {
    const range = rangeFor(offset, offset);
    const otherEnd = anchor === offset ? null : rangeFor(anchor, anchor);
    for (const element of scrollParents(range)) {
      const rect = caretRect(range), box = viewport(element);
      let { left, right, top, bottom } = rect;
      if (otherEnd && element.contains(otherEnd.startContainer)) {
        const other = caretRect(otherEnd);
        const x1 = Math.min(left, other.left), x2 = Math.max(right, other.right);
        const y1 = Math.min(top, other.top), y2 = Math.max(bottom, other.bottom);
        if (x2 - x1 <= box.right - box.left - 16) { left = x1; right = x2; }
        if (y2 - y1 <= box.bottom - box.top - 16) { top = y1; bottom = y2; }
      }
      const dx = horizontal ? revealDelta(left, right, box.left, box.right) : 0;
      const alignTop = !reveal && element === scroller();
      const dy = vertical ? (alignTop ? top - box.top - 8 : revealDelta(top, bottom, box.top, box.bottom)) : 0;
      if (dx || dy) element.scrollTo({ left: element.scrollLeft + dx, top: element.scrollTop + dy, behavior: 'instant' });
      if (dx) lastHorizontalScroller = element;
      scrollPositions.set(element, { x: element.scrollLeft, y: element.scrollTop });
    }
  }
  function readSelection() {
    let anchor, focus;
    if (current().preview) {
      const selection = window.getSelection();
      if (!selection?.rangeCount || !nativePreview.contains(selection.anchorNode) || !nativePreview.contains(selection.focusNode)) return null;
      const positions = map();
      const backward = !selection.isCollapsed && selection.getRangeAt(0).startContainer === selection.focusNode && selection.getRangeAt(0).startOffset === selection.focusOffset;
      anchor = positions.toSource(selection.anchorNode, selection.anchorOffset, backward ? -1 : 1);
      focus = positions.toSource(selection.focusNode, selection.focusOffset, backward ? 1 : -1);
    } else {
      const selection = editor.model.document.selection;
      if (!selection.anchor || !selection.focus) return null;
      const positions = map();
      anchor = positions.toSource(selection.anchor, selection.isBackward ? -1 : 1);
      focus = positions.toSource(selection.focus, selection.isBackward ? 1 : -1);
    }
    if (anchor == null || focus == null) return null;
    if ((current().preview ? window.getSelection().isCollapsed : editor.model.document.selection.isCollapsed)) focus = anchor;
    return { anchor, focus };
  }
  function previewTarget(node) {
    return nativePreview.contains(node) || node === document.body || node === document.documentElement;
  }
  function previewSelectionCleared() {
    const selection = window.getSelection();
    return !selection?.rangeCount || (selection.isCollapsed && previewTarget(selection.anchorNode));
  }
  function sourceAt(node, offset) {
    if (current().preview) return map().toSource(node, offset);
    const view = editor.editing.view.domConverter.domPositionToView(node, offset);
    return view ? map().toSource(editor.editing.mapper.toModelPosition(view)) : null;
  }
  function topOffset(scrollTarget) {
    const root = current().preview ? nativePreview : editor.ui.getEditableElement();
    const nested = scrollTarget && root.contains(scrollTarget) ? scrollTarget : null;
    const scope = nested || root, outer = viewport(), inner = nested ? viewport(nested) : root.getBoundingClientRect();
    const left = Math.max(outer.left, inner.left), right = Math.min(outer.right, inner.right);
    const top = Math.max(outer.top, inner.top), bottom = Math.min(outer.bottom, inner.bottom);
    let fallback;
    for (const y of [top + 10, top + 24, top + 40, Math.min(top + 60, (top + bottom) / 2)]) {
      if (y >= bottom) continue;
      let visibleCell;
      for (const x of [left + 1, left + 48, (left + right) / 2, right - 12]) {
        if (x < left || x >= right) continue;
        const hit = document.elementFromPoint(x, y);
        if (!hit || !scope.contains(hit)) continue;
        // Chromium can return a boundary before the whole table when asked for
        // a caret in its gutter. Only accept a caret on the actual visual line.
        const cell = hit.closest('td,th');
        const block = cell || hit.closest('p,li,pre,h1,h2,h3,h4,h5,h6');
        const range = document.caretRangeFromPoint(x, y);
        if (range && (block || scope).contains(range.startContainer)) {
          const rect = range.getClientRects()[0];
          if (rect?.height && rect.left >= left - 1 && rect.left < right && rect.top <= y + 2 && rect.bottom >= y - 2) {
            const offset = sourceAt(range.startContainer, range.startOffset);
            if (offset != null) return offset;
          }
        }
        if (!visibleCell && cell && scope.contains(cell)) visibleCell = cell;
        if (!fallback && block && scope.contains(block)) fallback = block;
      }
      // Padding or an empty cell can occupy the entire sampling line. Its own
      // content still identifies the row even when there is no glyph at that y.
      if (visibleCell) { fallback = visibleCell; break; }
    }
    if (!fallback) return null;
    const text = document.createTreeWalker(fallback, NodeFilter.SHOW_TEXT).nextNode();
    return sourceAt(text || fallback, 0);
  }
  function verticalMap() {
    const outer = scroller(), root = current().preview ? nativePreview : editor.ui.getEditableElement();
    const positions = map(), height = outer.scrollHeight, width = outer.clientWidth;
    if (scrollCache?.positions !== positions || scrollCache.height !== height || scrollCache.width !== width) {
      const origin = viewport(outer).top - outer.scrollTop;
      scrollCache = { positions, height, width, map: createScrollMap(positions.scrollBlocks, root, origin, current().state.text.length) };
    }
    return scrollCache.map;
  }
  function verticalPosition(target) {
    const outer = scroller();
    if (target && target !== outer) return { top: topOffset(target) ?? last?.top ?? 0 };
    return verticalMap().at(outer.scrollTop + Math.min(10, outer.scrollTop));
  }
  function restoreVertical(state) {
    if (state.nextTop == null) { scrollTo(state.top, false); return; }
    const positions = verticalMap(), start = positions.yAt(state.top), end = positions.yAt(state.nextTop);
    const y = start + (end - start) * (state.topFraction || 0), outer = scroller();
    outer.scrollTo({ top: y - Math.min(10, y / 2), behavior: 'instant' });
    scrollPositions.set(outer, { x: outer.scrollLeft, y: outer.scrollTop });
  }
  function capture(selection = true, scroll = false, verticalTarget, horizontalTarget) {
    if (!enabled()) return null;
    const { state, preview } = current();
    let selected = selection ? readSelection() : null;
    // A click in a read-only WebView can clear the browser range completely.
    // Scintilla still needs an explicit, collapsed selection in that case.
    if (!selected && selection && preview && previewCaret != null && previewSelectionCleared())
      selected = { anchor: previewCaret, focus: previewCaret };
    const track = state.snapshot.syncScroll !== false;
    return { session: state.snapshot.session, revision: state.snapshot.revision,
      anchor: selected?.anchor ?? last?.anchor ?? 0, focus: selected?.focus ?? last?.focus ?? 0,
      ...(scroll && track ? verticalPosition(verticalTarget) : { top: 0 }),
      left: horizontalTarget && track ? topOffset(horizontalTarget) : null,
      selection: !!selected && state.snapshot.syncSelection !== false, scroll: scroll && track, reveal: !!selected && !scroll };
  }
  function cancelTimer() {
    clearTimeout(timer); cancelAnimationFrame(frame); timer = frame = undefined;
  }
  function schedule(selection, scroll) {
    if (applying || !enabled()) return;
    pending.selection ||= selection; pending.scroll ||= scroll;
    if (timer == null && !sending) {
      frame = requestAnimationFrame(send);
      timer = setTimeout(send, 45); // Also deliver when WebView2 suspends animation frames.
    }
  }
  function rememberPosition(state) {
    last = { ...state, smoothScroll: false, selection: state.selection || !!last?.selection, scroll: state.scroll || !!last?.scroll,
      anchor: state.selection ? state.anchor : last?.anchor ?? state.anchor,
      focus: state.selection ? state.focus : last?.focus ?? state.focus,
      top: state.scroll ? state.top : last?.top ?? state.top,
      nextTop: state.scroll ? state.nextTop : last?.nextTop,
      topFraction: state.scroll ? state.topFraction : last?.topFraction };
  }
  async function send() {
    cancelTimer();
    if (sending || !enabled()) return;
    const state = current().state;
    sending = true;
    try {
      const ready = await flush(true);
      if (current().state !== state) return;
      if (!ready || !enabled()) { pending = {}; return; }
      const next = capture(!!pending.selection, !!pending.scroll, pending.verticalTarget, pending.horizontalTarget);
      if (next) next.smoothScroll = next.scroll && !next.selection && !!pending.smoothScroll;
      pending = {};
      if (next && (next.selection || next.scroll || next.left != null)) {
        next.reveal = next.selection;
        rememberPosition(next);
        if (next.selection) {
          if (current().preview) previewCaret = next.focus;
          if (next.anchor === next.focus) paint(); else clearHighlight();
        }
        post({ action: 'navigation', state: next });
      }
    } finally {
      sending = false;
      if (pending.selection || pending.scroll || pending.horizontalTarget) schedule(false, false);
    }
  }
  function receive(state) {
    if (!enabled()) return;
    const active = current().state;
    if (state.session !== active.snapshot.session || state.revision !== active.snapshot.revision || active.inFlight || active.text !== active.base) return;
    applying = true;
    if (state.selection) pending.selection = false;
    if (state.scroll) pending.scroll = false;
    if (state.left != null) pending.horizontalTarget = null;
    if (!pending.selection && !pending.scroll && !pending.horizontalTarget) cancelTimer();
    try {
      rememberPosition(state);
      if (state.selection && active.snapshot.syncSelection !== false) {
        if (current().preview) {
          const local = readSelection();
          if (!local || local.anchor !== state.anchor || local.focus !== state.focus) {
            previewCaret = null;
            window.getSelection()?.removeAllRanges();
          }
        }
        else {
          const positions = map(), backward = state.anchor > state.focus;
          const anchor = positions.toPosition(state.anchor, backward ? -1 : 1);
          const focus = state.focus === state.anchor ? anchor : positions.toPosition(state.focus, backward ? 1 : -1);
          // CKEditor ignores a direction-only change when the range is equal.
          // Set the anchor first, then extend the focus, within one model batch.
          editor.model.change(writer => { writer.setSelection(anchor); writer.setSelectionFocus(focus); });
        }
      }
      if (state.scroll && active.snapshot.syncScroll !== false) restoreVertical(state);
      if (state.left != null && active.snapshot.syncScroll !== false) scrollTo(state.left, false, state.left, true, false);
      if (state.selection && state.reveal && active.snapshot.syncSelection !== false) scrollTo(state.focus, true, state.anchor);
      paint();
    } finally { applying = false; }
  }
  function refresh(modeChanged = false) {
    previewCache = null; mapCache = null; scrollCache = null;
    if (!enabled()) { last = null; previewCaret = null; paint(); return; }
    const { state } = current(), key = [state.snapshot.session, state.snapshot.revision, state.snapshot.syncSelection, state.snapshot.syncScroll, state.snapshot.preserveLineBreaks].join(':');
    if (last?.session !== state.snapshot.session) { last = null; clearHighlight(); }
    if (requested !== key || modeChanged) {
      previewCaret = null; lastHorizontalScroller = null;
      const outer = scroller();
      scrollPositions.set(outer, { x: outer.scrollLeft, y: outer.scrollTop });
    }
    if (requested !== key && !state.inFlight && state.text === state.base) { cancelTimer(); pending = {}; }
    if (modeChanged && last?.session === state.snapshot.session) receive({ ...last, selection: true, scroll: true });
    if (requested !== key || modeChanged) {
      requested = key;
      post({ action: 'navigation', state: { session: state.snapshot.session, revision: state.snapshot.revision, request: true } });
    }
  }
  function beforeModeChange() {
    cancelTimer();
    const outer = scroller();
    const panned = lastHorizontalScroller?.isConnected && lastHorizontalScroller.scrollLeft ? lastHorizontalScroller : null;
    const next = capture(true, true, pending.verticalTarget, pending.horizontalTarget || panned || (outer.scrollLeft ? outer : null));
    pending = {};
    if (!next) return last;
    if (!next.selection && last?.selection) { next.anchor = last.anchor; next.focus = last.focus; next.selection = true; }
    if (next.selection) next.reveal = !!visibleCaret(rangeFor(next.focus, next.focus));
    last = next;
    if (next.selection || next.scroll) post({ action: 'navigation', state: next });
    return next;
  }
  function remap(position, before, after) {
    if (!position || before === after) return position;
    const project = formattingPositionMap(before, after);
    const backwards = position.anchor > position.focus;
    const anchor = project(position.anchor, backwards ? -1 : 1);
    return { ...position, anchor, focus: position.focus === position.anchor ? anchor : project(position.focus, backwards ? 1 : -1),
      top: project(position.top, 1), nextTop: position.nextTop == null ? null : project(position.nextTop, 1),
      left: position.left == null ? null : project(position.left, 1) };
  }
  function afterModeChange(position) {
    last = position;
    if (last) {
      last = { ...last, revision: current().state.snapshot.revision };
      if (last.selection || last.scroll) post({ action: 'navigation', state: last });
    }
    refresh(true);
  }
  const selectionChanged = () => { if (!current().preview) schedule(true, false); };
  editor.model.document.selection.on('change:range', selectionChanged);
  on(document, 'selectionchange', () => {
    paint();
    if (!current().preview || !enabled()) return;
    const selected = readSelection();
    if (selected) previewCaret = selected.focus;
    if (selected || (previewCaret != null && previewSelectionCleared())) schedule(true, false);
  });
  function rememberScrollStart(target) {
    for (let node = target; node instanceof Element; node = node.parentElement) {
      if (!scrollPositions.has(node)) scrollPositions.set(node, { x: node.scrollLeft, y: node.scrollTop });
    }
  }
  on(document, 'wheel', event => {
    rememberScrollStart(event.target);
    if (event.deltaY) lastWheel = performance.now();
  }, { capture: true, passive: true });
  on(document, 'pointerdown', event => {
    lastWheel = -Infinity;
    rememberScrollStart(event.target);
    if (current().preview && previewTarget(event.target)) clearHighlight();
    else {
      previewCaret = null;
      if (surface.contains(event.target)) { last = null; clearHighlight(); }
    }
  }, true);
  on(document, 'pointerup', event => {
    if (current().preview && previewTarget(event.target) && event.button === 0 && enabled()) {
      const bounds = viewport();
      if (event.clientX >= bounds.right || event.clientY >= bounds.bottom) return;
      const hit = document.caretRangeFromPoint(event.clientX, event.clientY);
      const offset = hit && nativePreview.contains(hit.startContainer) ? map().toSource(hit.startContainer, hit.startOffset) : null;
      previewCaret = offset ?? previewCaret ?? last?.focus ?? null;
      schedule(true, false);
    } else if (surface.contains(event.target)) schedule(true, false);
  });
  const viewDocument = editor.editing.view.document;
  viewDocument.on('change:isFocused', paint);
  on(window, 'focus', paint);
  on(window, 'blur', () => { previewCaret = null; paint(); });
  on(document, 'keyup', event => {
    if (nativePreview.contains(event.target) || surface.contains(event.target)) schedule(true, false);
  });
  on(document, 'scroll', event => {
    const target = event.target === document ? documentScroller() : event.target;
    if (target !== scroller() && !(current().preview ? nativePreview : surface).contains(target)) return;
    paint();
    const previous = scrollPositions.get(target) || { x: 0, y: 0 };
    const x = target.scrollLeft, y = target.scrollTop, time = performance.now();
    scrollPositions.set(target, { x, y, time });
    if (!enabled() || current().state.snapshot.syncScroll === false || (x === previous.x && y === previous.y)) return;
    if (x !== previous.x) pending.horizontalTarget = lastHorizontalScroller = target;
    if (y !== previous.y) {
      pending.verticalTarget = target;
      const dy = Math.abs(y - previous.y);
      pending.smoothScroll = time - lastWheel < 160 && time - (previous.time ?? -Infinity) > 28 &&
        dy > 24 && dy < (target === documentScroller() ? viewport(target).bottom : target.clientHeight) / 2;
    }
    schedule(false, y !== previous.y);
  }, { capture: true, passive: true });
  on(window, 'resize', () => { scrollCache = null; paint(); });
  on(document, 'load', () => { scrollCache = null; }, true);
  return { receive, refresh, capture, send, beforeModeChange, afterModeChange, remap,
    dispose() { disposed = true; cancelTimer(); listeners.forEach(remove => remove()); editor.model.document.selection.off('change:range', selectionChanged); viewDocument.off('change:isFocused', paint); clearHighlight(); caret.remove(); } };
}
