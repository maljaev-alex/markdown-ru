window.createLegacyView = async function (args) {
  let options = {
    document: "",
    css: "",
    lineMark: false
  }
  options = { ...options, ...args };

  if (options.editorManaged) {
    const previousSetup = window.markdownSetup;
    window.markdownSetup = async (parser, context) => {
      if (previousSetup) await previousSetup(parser, context);
      // Document HTML must not run inside the page that owns the editing bridge.
      parser.set({ html: false });
      window.AnotherMarkdownEditor.configurePreviewParser(parser);
    };
  }

  if (options.readOnlyPreview) {
    // The legacy preview is read-only; changes go through the versioned editor bridge.
    const preventEdit = event => {
      if (event.type !== "click" || event.target.closest('input, textarea, [contenteditable="true"]')) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    };
    if (!options.editorManaged) ["click", "paste", "drop", "beforeinput"].forEach(name => document.addEventListener(name, preventEdit, true));
    const disableInputs = () => document.querySelectorAll('#content input, #content textarea, #content [contenteditable="true"]').forEach(el => {
      el.disabled = true;
      if (el.hasAttribute('contenteditable')) el.setAttribute('contenteditable', 'false');
    });
    new MutationObserver(disableInputs).observe(document.getElementById("content"), { childList: true, subtree: true });
  }

  const plugins = [
    [/\.(md|mdc)$/i, 'http://assets.example/markdown/markdown.min.js', options.css, true],
    [/\.pano360\.(json)$/i, 'http://assets.example/pano360/editor.js', null]
  ]

  for (let plugin of plugins) {
    if (options.document.match(plugin[0])) {
      if (plugin[2]) {
        const link = document.createElement("link");
        link.href = plugin[2];
        link.rel = 'stylesheet';
        document.head.appendChild(link);
      }
      const script = document.createElement("script");
      script.src = plugin[1];
      script.defer = true;
      const scriptLoad = new Promise((resolve) => {
        script.onload = () => resolve();
        document.head.appendChild(script);
      });
      await scriptLoad;

      const viewPlugin = window.viewPlugin;
      let imagePreview = null;
      if (plugin[3]) {
        const style = document.createElement("link");
        style.href = "http://assets.example/image-preview.css";
        style.rel = "stylesheet";
        document.head.appendChild(style);
        await new Promise((resolve, reject) => {
          const script = document.createElement("script");
          script.src = "http://assets.example/image-preview.js";
          script.onload = resolve;
          script.onerror = () => reject(new Error("Image preview could not be loaded"));
          document.head.appendChild(script);
        });
        imagePreview = window.createImagePreview(document.getElementById("content"));
      }

      const documentChanged = async (modified = true) => {
        const args = options = {
          ...options,
          modified
        };
        await viewPlugin.setDocument(document.getElementById("content"), args);
        window.amSetShowLinkAddresses(options.showLinkAddresses);
        if (imagePreview) imagePreview.refresh();
      }

      await documentChanged(false);
      return {
        scrollToLine: viewPlugin.scrollToLine,
        documentChanged,
        setOptions: async next => { options = { ...options, ...next }; await documentChanged(); },
        dispose: () => { if (imagePreview) imagePreview.dispose(); }
      }
    }
  }
  console.log(`unsupported file extension: ${options.document}`);
  return {
    scrollToLine: () => { },
    documentChanged: () => { },
    dispose: () => { }
  }
}

// WebView2 applies its native status-bar setting only on the next top-level
// navigation. This indicator applies the preference without reloading the editor.
window.amSetShowLinkAddresses = (() => {
  let enabled = false, hovered = null, focused = null, indicator;
  const linkAt = target => target instanceof Element ? target.closest('a[href]') : null;
  const sourceHref = link => {
    const editor = window.amEditor?.editor;
    if (editor && editor.ui.getEditableElement()?.contains(link)) {
      // Preserve the original Markdown address, including its spelling and encoding.
      // Read the model attribute without changing the selection or URL sanitization.
      const viewLink = editor.editing.view.domConverter.mapDomToView(link);
      if (viewLink) {
        const range = editor.editing.mapper.toModelRange(editor.editing.view.createRangeIn(viewLink));
        for (const item of range.getItems()) {
          const href = item.getAttribute('linkHref');
          if (href) return href;
        }
      }
    }
    return link.getAttribute('href') || link.href;
  };
  const decodePath = path => path.replace(/(?:%[0-9a-f]{2})+/gi, encoded => {
    try { return decodeURIComponent(encoded); } catch { return encoded; }
  });
  // A basename with :line looks like a URL scheme; resolve it as a relative path.
  const resolveHref = (href, baseURI) => new URL(/^[^\\/:?#]+\.[^\\/:?#]+:\d+(?::\d+)?$/.test(href) ? './' + href : href, baseURI);
  const addressOf = link => {
    const href = sourceHref(link);
    if (/^[a-z]:[\\/]/i.test(href)) return decodePath(href);
    let url;
    try { url = resolveHref(href, link.baseURI); } catch { return href; }
    const suffix = url.search + decodePath(url.hash);
    if (url.protocol === 'http:' && url.hostname === 'local.example') {
      const disk = /^\/disk([a-z])\//i.exec(url.pathname);
      if (disk) return disk[1] + ':/' + decodePath(url.pathname.slice(disk[0].length)) + suffix;
    }
    if (url.protocol === 'file:') {
      const path = decodePath(url.pathname);
      return (url.hostname ? '//' + url.hostname + path : path.replace(/^\/(?=[a-z]:\/)/i, '')) + suffix;
    }
    return url.href;
  };
  const openLocal = (href, baseURI) => {
    if (!href || href.startsWith('#') || !window.chrome?.webview?.postMessage) return false;
    let url;
    try { url = resolveHref(href, baseURI); } catch { return false; }
    if (url.protocol !== 'file:' && !(url.protocol === 'http:' && url.hostname === 'local.example')) return false;
    // Chromium blocks file: navigation before NavigationStarting. Send the
    // target to the native file opener instead of navigating the WebView.
    window.chrome.webview.postMessage({ type: 'am.openLink', href: url.href });
    return true;
  };
  const refresh = () => {
    const link = hovered || focused;
    const visible = enabled && link?.isConnected && link.getClientRects().length && !!link.href;
    indicator.hidden = !visible;
    indicator.textContent = visible ? addressOf(link) : '';
  };
  return value => {
    if (!indicator) {
      indicator = document.createElement('div'); indicator.id = 'am-link-address';
      indicator.hidden = true; indicator.setAttribute('aria-hidden', 'true');
      Object.assign(indicator.style, { position: 'fixed', left: '0', bottom: '0', zIndex: '2147483647',
        maxWidth: '100vw', boxSizing: 'border-box', padding: '3px 6px', font: '12px/1.4 "Segoe UI", sans-serif',
        color: '#000', background: '#6a9444',
        border: '1px solid #6a9444', whiteSpace: 'nowrap', overflow: 'hidden',
        textOverflow: 'ellipsis', pointerEvents: 'none', userSelect: 'none' });
      document.body.append(indicator);
      document.addEventListener('pointerover', event => { hovered = linkAt(event.target); refresh(); });
      document.addEventListener('pointerout', event => { hovered = linkAt(event.relatedTarget); refresh(); });
      document.addEventListener('focusin', event => { focused = linkAt(event.target); refresh(); });
      document.addEventListener('focusout', event => { focused = linkAt(event.relatedTarget); refresh(); });
      window.addEventListener('blur', () => { hovered = focused = null; refresh(); });
      // File opening is independent of the address visibility preference.
      document.addEventListener('click', event => {
        const link = linkAt(event.target);
        if (!link || event.button !== 0) return;
        const editable = window.amEditor?.editor.ui.getEditableElement();
        if (editable?.contains(link) && !event.ctrlKey && !event.metaKey) return;
        if (openLocal(sourceHref(link), link.baseURI)) { event.preventDefault(); event.stopImmediatePropagation(); }
      }, true);
      document.addEventListener('keydown', event => {
        const editor = window.amEditor?.editor;
        if (event.key !== 'Enter' || !event.altKey || !editor?.ui.getEditableElement()?.contains(event.target)) return;
        if (openLocal(editor.commands.get('link')?.value, document.baseURI)) { event.preventDefault(); event.stopImmediatePropagation(); }
      }, true);
    }
    enabled = !!value; refresh();
  };
})();

window.createView = async function (args) {
  window.amSetShowLinkAddresses(args.showLinkAddresses);
  if (!args.editorSnapshot) return window.createLegacyView(args);
  const style = document.createElement('link');
  style.rel = 'stylesheet'; style.href = 'http://assets.example/editor/editor.css';
  document.head.appendChild(style);
  try { await new Promise((resolve, reject) => {
    const script = document.createElement('script'); script.src = 'http://assets.example/editor/editor.js';
    script.onload = resolve; script.onerror = () => reject(new Error('Visual editor could not be loaded'));
    document.head.appendChild(script);
  });
    return await window.AnotherMarkdownEditor.create(args);
  } catch (error) {
    const notice = document.createElement('p');
    notice.textContent = '\u0412\u0438\u0437\u0443\u0430\u043b\u044c\u043d\u044b\u0439 \u0440\u0435\u0434\u0430\u043a\u0442\u043e\u0440 \u043d\u0435 \u0437\u0430\u0433\u0440\u0443\u0437\u0438\u043b\u0441\u044f. \u041f\u0435\u0440\u0435\u0443\u0441\u0442\u0430\u043d\u043e\u0432\u0438 \u043f\u043e\u043b\u043d\u044b\u0439 \u043f\u0430\u043a\u0435\u0442 \u043f\u043b\u0430\u0433\u0438\u043d\u0430; \u0438\u0441\u0445\u043e\u0434\u043d\u0438\u043a \u0434\u043e\u0441\u0442\u0443\u043f\u0435\u043d \u0432 Notepad++.';
    document.body.prepend(notice); document.getElementById('content').hidden = false;
    console.error(error);
    return window.createLegacyView({ ...args, editorManaged: true, readOnlyPreview: true });
  }
};
