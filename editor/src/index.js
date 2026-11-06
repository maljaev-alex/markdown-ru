import {
  DecoupledEditor, Essentials, Paragraph, Heading, Bold, Italic, Strikethrough, Code,
  Link, AutoLink, List, TodoList, BlockQuote, CodeBlock, HorizontalLine, Table, TableToolbar,
  Image, ImageToolbar, ImageTextAlternative, ImageUpload, ImageInsertViaUrl, LinkImage,
  Autoformat, Markdown, PasteFromOffice
} from 'ckeditor5';
import translations from 'ckeditor5/translations/ru.js';
import 'ckeditor5/ckeditor5.css';
import './style.css';
import strings from './ru.json';
import { OpaqueMarkdown } from './opaque.js';
import { GfmTableAlignment } from './table.js';
import { SourceDocument, mergeSource, splitSource, hasHtml } from './source.js';
import { configureLineBreaks } from './line-breaks.js';
import { createNavigation } from './navigation.js';
export { configurePreviewParser } from './navigation-map.js';
export { mergeSource, splitSource, hasHtml };

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}
function button(label, action) {
  const node = element('button', 'am-button', label); node.type = 'button';
  node.addEventListener('click', action); return node;
}

export async function create(options) {
  const shell = element('section', 'am-editor-shell');
  const actions = element('div', 'am-editor-actions');
  const toolbar = element('div', 'am-editor-toolbar');
  const surface = element('div', 'am-editor-surface');
  const status = element('span', 'am-editor-status'); status.setAttribute('role', 'status');
  const nativePreview = document.getElementById('content'); nativePreview.hidden = false;
  actions.hidden = !!options.nativeEditorToolbar;
  toolbar.hidden = surface.hidden = true;
  shell.classList.add('am-preview-mode');
  nativePreview.before(shell); shell.append(actions, toolbar, surface);
  const sessions = new Map(), requests = new Map();
  let active, loading = false, timer, requestNumber = 0, legacy, preview = true, disposed = false, pendingSnapshot;
  let previewQueue = Promise.resolve(), lastUi;
  const host = window.chrome?.webview;
  const post = payload => host?.postMessage({ type: 'am.editor', ...payload });
  const request = (action, state, extra = {}) => new Promise((resolve, reject) => {
    if (!host) { reject(new Error(strings.noHost)); return; }
    const requestId = String(++requestNumber);
    const timeout = setTimeout(() => { requests.delete(requestId); reject(new Error(strings.timeout)); },
      action === 'save' || action === 'saveAs' ? 600000 : 30000);
    requests.set(requestId, reply => { clearTimeout(timeout); resolve(reply); });
    post({ action, requestId, session: state.snapshot.session, baseText: state.base, content: state.text, ...extra });
  });
  const onMessage = event => {
    if (event.data?.type !== 'am.editor.reply') return;
    const reply = event.data.reply, complete = requests.get(reply.requestId);
    requests.delete(reply.requestId); complete?.(reply);
  };
  host?.addEventListener('message', onMessage);
  const editor = await DecoupledEditor.create({
    licenseKey: 'GPL', language: 'ru', translations: [translations],
    plugins: [Essentials, Paragraph, Heading, Bold, Italic, Strikethrough, Code, Link, AutoLink,
      List, TodoList, BlockQuote, CodeBlock, HorizontalLine, Table, TableToolbar, Image, ImageToolbar,
      ImageTextAlternative, ImageUpload, ImageInsertViaUrl, LinkImage, Autoformat, Markdown, PasteFromOffice, OpaqueMarkdown, GfmTableAlignment],
    toolbar: { items: ['heading', '|', 'bold', 'italic', 'strikethrough', 'code', '|', 'link',
      'bulletedList', 'numberedList', 'todoList', '|', 'insertImageViaUrl', 'uploadImage',
      'blockQuote', 'insertTable', 'codeBlock', 'horizontalLine', '|', 'undo', 'redo'], shouldNotGroupWhenFull: true },
    // Treat absolute Windows paths as local files, not unknown URL schemes.
    link: { allowedProtocols: ['https?', 'ftps?', 'mailto', 'file', '[a-z](?=:[\\\\/])', '[a-z0-9_.+-]+\\.[a-z0-9]+(?=:\\d+(?::\\d+)?$)'] },
    table: { contentToolbar: ['tableColumn', 'tableRow'] },
    image: { toolbar: ['imageTextAlternative', 'linkImage'] },
    heading: { options: [{ model: 'paragraph', title: 'Paragraph', class: 'ck-heading_paragraph' },
      ...[1, 2, 3, 4, 5, 6].map(n => ({ model: 'heading' + n, view: 'h' + n, title: 'Heading ' + n, class: 'ck-heading_heading' + n }))] },
    codeBlock: { languages: [{ language: 'plaintext', label: strings.plain },
      ...['javascript', 'typescript', 'csharp', 'python', 'powershell', 'sql', 'bsl', 'json', 'html', 'css', 'bash', 'mermaid'].map(language => ({ language, label: language }))] },
    placeholder: strings.empty
  });
  toolbar.append(editor.ui.view.toolbar.element); surface.append(editor.ui.getEditableElement());
  const lineBreaks = configureLineBreaks(editor);
  const source = new SourceDocument(editor);
  const navigation = createNavigation({ editor, source, surface, nativePreview,
    current: () => ({ state: active, preview, loading }), flush, post });
  function publishUi() {
    if (!active) return;
    const state = { session: active.snapshot.session, status: status.textContent, error: status.classList.contains('am-error'),
      editing: !preview, readOnly: !!active.snapshot.readOnly, translated: !!active.snapshot.translated,
      hasConflict: !!active.conflict, canRetry: !!active.error };
    const json = JSON.stringify(state);
    if (json !== lastUi) { lastUi = json; post({ action: 'ui', state }); }
  }
  function setStatus(text, error = false) { status.textContent = text; status.classList.toggle('am-error', error); publishUi(); }
  function draft(state) { post({ action: 'draft', session: state.snapshot.session, baseText: state.base, content: state.text }); }
  function capture() {
    if (!active || loading) return true;
    if (editor.plugins.get('FileRepository').loaders.length) { setStatus(strings.imageWait); return false; }
    try { active.text = source.serialize(); draft(active); return true; }
    catch (error) { setStatus(error.message === 'html-not-supported' ? strings.html : error.message, true); return false; }
  }
  function load(text) {
    loading = true;
    try { lineBreaks.set(active?.snapshot.preserveLineBreaks); source.load(text); }
    finally { loading = false; }
  }
  function refreshStatus() {
    if (!active) return;
    window.amSetShowLinkAddresses?.(active.snapshot.showLinkAddresses);
    if (lineBreaks.preserve !== !!active.snapshot.preserveLineBreaks) load(active.text);
    saveButton.textContent = active.snapshot.translated ? strings.saveAs : strings.save;
    saveButton.hidden = !active.snapshot.translated;
    saveButton.disabled = active.snapshot.readOnly || !!active.conflict; conflictButton.hidden = !active.conflict;
    modeButton.disabled = preview && !!active.snapshot.readOnly;
    document.documentElement.classList.toggle('am-theme-dark', !!active.snapshot.dark);
    if (active.snapshot.readOnly) editor.enableReadOnlyMode('source'); else editor.disableReadOnlyMode('source');
    if (active.conflict) setStatus(strings.conflict, true);
    else if (active.snapshot.translated) setStatus(strings.translation);
    else if (active.snapshot.readOnly) setStatus(strings.readonly);
    else setStatus(active.text === active.base ? strings.ready + (active.snapshot.modified ? ' \u00b7 ' + strings.unsaved : '') : strings.pending);
  }
  function rebase(state, snapshot) {
    if (snapshot.session !== state.snapshot.session || snapshot.revision < state.snapshot.revision) return;
    const merged = mergeSource(state.base, state.text, snapshot.text); state.snapshot = snapshot; state.error = null;
    if (merged.text === null) state.conflict = { ...merged, source: snapshot.text };
    else {
      state.base = snapshot.text; state.conflict = null;
      if (state.text !== merged.text) { state.text = merged.text; if (state === active) load(state.text); }
    }
    draft(state);
  }
  async function receiveSnapshot(snapshot) {
    if (disposed || !snapshot?.session) return;
    if (!capture()) { pendingSnapshot = snapshot; return; }
    if (active?.snapshot.session !== snapshot.session) {
      preview = true; updateMode();
      conflictDialog?.close();
      let state = sessions.get(snapshot.session);
      if (!state) { state = { snapshot, base: snapshot.draftBase ?? snapshot.text, text: snapshot.draftText ?? snapshot.text }; sessions.set(snapshot.session, state); }
      active = state; rebase(state, snapshot); load(state.text);
    } else if (!active.inFlight) rebase(active, snapshot);
    else if (!active.remote || snapshot.revision >= active.remote.revision) active.remote = snapshot;
    const base = document.querySelector('base'); if (base && snapshot.baseUrl) base.href = snapshot.baseUrl;
    refreshStatus(); if (preview) await renderPreview();
    navigation.refresh();
    if (!active.conflict && active.text !== active.base) schedule();
  }
  async function flush(settle = false) {
    clearTimeout(timer); if (!capture() || !active) return false;
    const state = active;
    if (state.inFlight) { await state.inFlight; if (state !== active) return false; return flush(settle); }
    if (state.conflict) return false;
    if (state.text === state.base) return true;
    if (state.snapshot.readOnly) return false;
    const sent = state.text;
    state.inFlight = (async () => {
      try {
        const reply = await request('apply', state, { navigation: navigation.capture(true, false) });
        if (reply.status === 'applied') {
          state.error = null; state.base = sent; if (reply.snapshot) state.snapshot = reply.snapshot;
          if (state.remote && state.remote.revision > state.snapshot.revision) rebase(state, state.remote);
          draft(state);
        } else if (reply.status === 'conflict' && reply.snapshot) rebase(state, reply.snapshot);
        else state.error = reply.message || (reply.status === 'stale' ? strings.stale : strings.error);
      } catch (error) { state.error = error.message; }
      finally { state.inFlight = null; state.remote = null; }
    })();
    await state.inFlight;
    if (state === active) {
      refreshStatus();
      if (state.error) { setStatus(state.error, true); retryButton.hidden = false; return false; }
      if (!state.conflict && state.text !== state.base) {
        if (settle) return flush(true);
        schedule();
      }
    }
    return !state.conflict && !state.error && state.text === state.base;
  }
  function schedule() { clearTimeout(timer); timer = setTimeout(flush, 220); }
  editor.model.document.on('change:data', () => {
    if (loading || !active) return;
    if (capture()) {
      active.error = null; retryButton.hidden = true;
      if (pendingSnapshot) { const next = pendingSnapshot; pendingSnapshot = null; receiveSnapshot(next); }
      else { refreshStatus(); schedule(); }
    }
  });
  editor.plugins.get('FileRepository').loaders.on('remove', () => setTimeout(() => {
    if (!capture()) return;
    if (pendingSnapshot) { const next = pendingSnapshot; pendingSnapshot = null; receiveSnapshot(next); }
    else schedule();
  }, 0));
  editor.plugins.get('FileRepository').createUploadAdapter = loader => {
    const state = active; let cancelled = false;
    return { abort() { cancelled = true; }, async upload() {
      const file = await loader.file;
      const data = await new Promise((resolve, reject) => {
        const reader = new FileReader(); reader.onload = () => resolve(reader.result); reader.onerror = reject; reader.readAsDataURL(file);
      });
      if (cancelled || active !== state) throw new Error(strings.stale);
      const reply = await request('image', state, { data, name: file.name });
      if (cancelled || active !== state) throw new Error(strings.stale);
      if (reply.status !== 'image') throw new Error(reply.message || strings.error);
      return { default: reply.url };
    } };
  };
  async function save() {
    try {
      if (!await flush(true)) { if (active?.conflict) showConflicts(); return; }
      const state = active, reply = await request(state.snapshot.translated ? 'saveAs' : 'save', state);
      if (state !== active) return;
      if (reply.snapshot) rebase(state, reply.snapshot);
      if (reply.status === 'conflict') { refreshStatus(); showConflicts(); }
      else setStatus(reply.status === 'saved' ? strings.saved : reply.status === 'cancelled' ? strings.saveCancelled : reply.message || strings.error,
        reply.status !== 'saved' && reply.status !== 'cancelled');
    } catch (error) { setStatus(error.message, true); }
  }
  function renderPreview() {
    const args = { ...options, document: active.snapshot.documentUrl || options.document, editorManaged: true, readOnlyPreview: true,
      lineMark: false, trackFirstLine: false, showLinkAddresses: !!active.snapshot.showLinkAddresses };
    previewQueue = previewQueue.catch(() => {}).then(async () => {
      if (disposed) return;
      if (!legacy) legacy = await window.createLegacyView(args); else await legacy.setOptions(args);
    });
    return previewQueue;
  }
  function updateMode() {
    toolbar.hidden = surface.hidden = preview; nativePreview.hidden = !preview;
    document.documentElement.classList.toggle('am-editing', !preview);
    shell.classList.toggle('am-preview-mode', preview); modeButton.textContent = preview ? strings.edit : strings.preview;
  }
  async function setPreview(value) {
    if (!active || (!value && active.snapshot.readOnly)) return;
    const state = active;
    if (!preview && !await flush(true)) { if (active?.conflict) showConflicts(); return; }
    if (state !== active) return;
    navigation.beforeModeChange();
    preview = value; updateMode(); publishUi();
    if (preview) await renderPreview();
    navigation.refresh(true);
  }
  let conflictDialog;
  function showConflicts() {
    if (!active?.conflict) return;
    conflictDialog?.remove(); const state = active, conflict = state.conflict;
    const dialog = conflictDialog = element('dialog', 'am-conflicts'); dialog.append(element('h2', '', strings.conflict));
    const resolutions = new Map();
    const submit = button(strings.resolve, () => {
      if (active !== state || state.conflict !== conflict) { dialog.close(); showConflicts(); return; }
      const text = conflict.regions.map((region, index) => region.ok ? region.ok.join('') : resolutions.get(index)).join('');
      state.base = conflict.source; state.text = text; state.conflict = null; load(text); draft(state);
      dialog.close(); refreshStatus(); schedule();
    }); submit.disabled = true;
    const update = () => { submit.disabled = resolutions.size !== conflict.regions.filter(r => r.conflict).length; };
    conflict.regions.forEach((region, index) => {
      if (!region.conflict) return;
      const block = element('section', 'am-conflict'), columns = element('div', 'am-conflict-columns');
      const result = element('textarea', 'am-conflict-result'); result.setAttribute('aria-label', strings.result); result.value = region.conflict.a.join('');
      for (const [label, text] of [[strings.visual, region.conflict.a.join('')], [strings.source, region.conflict.b.join('')]]) {
        const column = element('div'); column.append(element('pre', '', text), button(label, () => { result.value = text; resolutions.set(index, text); update(); })); columns.append(column);
      }
      const before = element('details'); before.append(element('summary', '', strings.base), element('pre', '', region.conflict.o.join('')));
      result.addEventListener('input', () => { resolutions.set(index, result.value); update(); });
      block.append(columns, before, element('label', '', strings.result), result); dialog.append(block);
    });
    const footer = element('div', 'am-conflict-actions'); footer.append(submit, button(strings.cancel, () => dialog.close())); dialog.append(footer);
    document.body.append(dialog); dialog.showModal();
  }
  const modeButton = button(strings.edit, () => setPreview(!preview)), saveButton = button(strings.save, save); saveButton.classList.add('am-primary');
  const conflictButton = button(strings.conflictButton, showConflicts); conflictButton.hidden = true;
  async function retry() {
    active.error = null; retryButton.hidden = true;
    const reply = await request('state', active); if (reply.snapshot) await receiveSnapshot(reply.snapshot); await flush();
  }
  const retryButton = button(strings.retry, retry); retryButton.hidden = true;
  actions.append(modeButton, saveButton, conflictButton, retryButton, status);
  const onKey = event => { if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') { event.preventDefault(); event.stopPropagation(); save(); } };
  document.addEventListener('keydown', onKey, true);
  const onUnload = () => capture(); window.addEventListener('beforeunload', onUnload);
  window.amReceiveSnapshot = receiveSnapshot;
  window.amReceiveNavigation = navigation.receive;
  window.amEditorCommand = async (command, session) => {
    if (disposed || !active || active.snapshot.session !== session) return;
    try {
      if (command === 'edit') await setPreview(false);
      else if (command === 'preview') await setPreview(true);
      else if (command === 'save') await save();
      else if (command === 'conflicts') showConflicts();
      else if (command === 'retry') await retry();
    } catch (error) { setStatus(error.message, true); }
  };
  await receiveSnapshot(window.__amSnapshot || options.editorSnapshot);
  window.amEditor = { editor, source, navigation, flush, save, receiveSnapshot, get state() { capture(); return active; } };
  return { documentChanged: () => receiveSnapshot(window.__amSnapshot || options.editorSnapshot),
    scrollToLine: line => { if (preview) legacy?.scrollToLine(line); },
    async dispose() { disposed = true; capture(); navigation.dispose(); clearTimeout(timer); legacy?.dispose(); host?.removeEventListener('message', onMessage); document.removeEventListener('keydown', onKey, true); window.removeEventListener('beforeunload', onUnload); await editor.destroy(); shell.remove(); }
  };
}
