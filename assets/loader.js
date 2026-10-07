window.createView = async function (args) {
  let options = {
    document: "",
    css: "",
    lineMark: false
  }
  options = { ...options, ...args };

  if (options.readOnlyPreview) {
    // Translated text is a view, not an editable version of the source buffer.
    const preventEdit = event => {
      if (event.type !== "click" || event.target.closest('input, textarea, [contenteditable="true"]')) {
        event.preventDefault();
        event.stopImmediatePropagation();
      }
    };
    ["click", "paste", "drop", "beforeinput"].forEach(name => document.addEventListener(name, preventEdit, true));
    const disableInputs = () => document.querySelectorAll('#content input, #content textarea, #content [contenteditable="true"]').forEach(el => {
      el.disabled = true;
      if (el.hasAttribute('contenteditable')) el.setAttribute('contenteditable', 'false');
    });
    new MutationObserver(disableInputs).observe(document.getElementById("content"), { childList: true, subtree: true });
  }

  const plugins = [
    [/\.(md|mdc)$/i, 'http://assets.example/markdown/markdown.min.js', options.css],
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

      const documentChanged = async (modified = true) => {
        const args = options = {
          ...options,
          modified
        };
        await viewPlugin.setDocument(document.getElementById("content"), args);
      }

      await documentChanged(false);
      return {
        scrollToLine: viewPlugin.scrollToLine,
        documentChanged,
        dispose: () => { }
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
