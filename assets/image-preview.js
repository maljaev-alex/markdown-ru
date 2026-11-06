(function () {
  const previews = new WeakMap();
  const magnifier = '<svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="10" cy="10" r="6.5"/><path d="m15 15 6 6M10 7v6M7 10h6"/></svg>';

  window.createImagePreview = function (root, options = {}) {
    if (previews.has(root)) return previews.get(root);

    const buttons = new Map();
    const layer = document.createElement("div");
    layer.className = "am-image-tools";
    document.body.appendChild(layer);
    const dialog = document.createElement("dialog");
    dialog.className = "am-image-dialog";
    if (options.standalone) dialog.classList.add("am-image-window");
    dialog.setAttribute("aria-label", "Просмотр изображения");
    dialog.innerHTML = `
      <header class="am-image-header"><span class="am-image-title"></span><button type="button" data-action="close" title="Закрыть (Esc)" aria-label="Закрыть изображение">×</button></header>
      <nav class="am-image-controls" aria-label="Масштаб изображения">
        <button type="button" data-action="out" title="Уменьшить (−)" aria-label="Уменьшить">−</button>
        <output aria-live="polite"></output>
        <button type="button" data-action="in" title="Увеличить (+)" aria-label="Увеличить">+</button>
        <button type="button" data-action="actual" title="Исходный размер (0)">100%</button>
        <button type="button" data-action="fit">Вписать</button>
      </nav>
      <div class="am-image-stage" tabindex="0" aria-label="Изображение. Колесо — масштаб, перетаскивание — перемещение."><div class="am-image-canvas"><img alt="" draggable="false"></div></div>
      <p class="am-image-error" hidden>Не удалось загрузить изображение.</p>`;
    document.body.appendChild(dialog);
    const stage = dialog.querySelector(".am-image-stage");
    const canvas = dialog.querySelector(".am-image-canvas");
    const image = canvas.querySelector("img");
    const title = dialog.querySelector(".am-image-title");
    const output = dialog.querySelector("output");
    const error = dialog.querySelector(".am-image-error");
    let source = null;
    let returnFocus = null;
    let savedOverflow = "";
    let scale = 1;
    let fitMode = false;
    let drag = null;
    let frame = 0;

    function fitScale() {
      return Math.min(1, stage.clientWidth / image.naturalWidth, stage.clientHeight / image.naturalHeight);
    }

    function setScale(value, x = stage.clientWidth / 2, y = stage.clientHeight / 2) {
      if (!dialog.open || !image.naturalWidth) return;
      const oldX = Math.max(0, (canvas.clientWidth - image.naturalWidth * scale) / 2);
      const oldY = Math.max(0, (canvas.clientHeight - image.naturalHeight * scale) / 2);
      const pointX = (stage.scrollLeft + x - oldX) / scale;
      const pointY = (stage.scrollTop + y - oldY) / scale;
      scale = Math.max(Math.min(0.1, fitScale()), Math.min(8, value));
      const width = image.naturalWidth * scale;
      const height = image.naturalHeight * scale;
      image.style.width = width + "px";
      image.style.height = height + "px";
      canvas.style.width = Math.max(stage.clientWidth, width) + "px";
      canvas.style.height = Math.max(stage.clientHeight, height) + "px";
      stage.scrollLeft = Math.max(0, (canvas.clientWidth - width) / 2) + pointX * scale - x;
      stage.scrollTop = Math.max(0, (canvas.clientHeight - height) / 2) + pointY * scale - y;
      output.textContent = Math.round(scale * 100) + "%";
      dialog.querySelector('[data-action="actual"]').setAttribute("aria-pressed", String(!fitMode && scale === 1));
      dialog.querySelector('[data-action="fit"]').setAttribute("aria-pressed", String(fitMode));
    }

    function close() {
      if (dialog.open) {
        dialog.close();
        if (options.standalone) window.chrome.webview.postMessage({ type: "am.closeImage" });
      }
    }

    function open(original, trigger) {
      if (!original.complete || !original.naturalWidth) return;
      if (!options.standalone) {
        window.chrome.webview.postMessage({ type: "am.openImage", src: original.currentSrc || original.src, title: original.alt || original.title || "Изображение" });
        return;
      }
      if (dialog.open) close();
      source = original;
      returnFocus = trigger || document.activeElement;
      title.textContent = original.alt || original.title || "Изображение";
      image.alt = original.alt || "Изображение";
      error.hidden = true;
      savedOverflow = document.documentElement.style.overflow;
      document.documentElement.style.overflow = "hidden";
      dialog.showModal();
      fitMode = true;
      scale = 1;
      image.onload = () => {
        if (!source || !dialog.open) return;
        setScale(fitScale());
        stage.scrollLeft = 0;
        stage.scrollTop = 0;
      };
      image.onerror = () => { error.hidden = false; };
      image.src = original.currentSrc || original.src;
      stage.focus({ preventScroll: true });
    }

    dialog.addEventListener("close", () => {
      source = null;
      drag = null;
      stage.classList.remove("am-image-dragging");
      document.documentElement.style.overflow = savedOverflow;
      if (returnFocus && returnFocus.isConnected) returnFocus.focus({ preventScroll: true });
    });
    dialog.addEventListener("cancel", event => { event.preventDefault(); close(); });
    dialog.addEventListener("click", event => {
      const action = event.target.closest("button")?.dataset.action;
      if (action === "close") close();
      else if (action) {
        fitMode = action === "fit";
        setScale(fitMode ? fitScale() : action === "actual" ? 1 : scale * (action === "in" ? 1.25 : 0.8));
      }
      else if (event.target === dialog) {
        const rect = dialog.getBoundingClientRect();
        if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) close();
      }
    });
    dialog.addEventListener("keydown", event => {
      if (event.key === "Escape") close();
      else if (["+", "=", "-", "_", "0"].includes(event.key)) {
        fitMode = false;
        setScale(event.key === "0" ? 1 : scale * (["+", "="].includes(event.key) ? 1.25 : 0.8));
      }
      else return;
      event.preventDefault();
      event.stopPropagation();
    });
    stage.addEventListener("wheel", event => {
      event.preventDefault();
      fitMode = false;
      const rect = stage.getBoundingClientRect();
      setScale(scale * (event.deltaY < 0 ? 1.2 : 1 / 1.2), event.clientX - rect.left, event.clientY - rect.top);
    }, { passive: false });
    stage.addEventListener("dblclick", () => { fitMode = scale === 1; setScale(fitMode ? fitScale() : 1); });
    stage.addEventListener("pointerdown", event => {
      if (event.button !== 0) return;
      drag = { x: event.clientX, y: event.clientY, left: stage.scrollLeft, top: stage.scrollTop };
      stage.setPointerCapture(event.pointerId);
      stage.classList.add("am-image-dragging");
      event.preventDefault();
    });
    stage.addEventListener("pointermove", event => {
      if (!drag) return;
      stage.scrollLeft = drag.left + drag.x - event.clientX;
      stage.scrollTop = drag.top + drag.y - event.clientY;
    });
    stage.addEventListener("lostpointercapture", () => { drag = null; stage.classList.remove("am-image-dragging"); });

    function positionButtons() {
      frame = 0;
      for (const [original, button] of buttons) {
        const rect = original.getBoundingClientRect();
        button.hidden = !original.complete || !original.naturalWidth || rect.width < 48 || rect.height < 32
          || rect.bottom < 36 || rect.right < 36 || rect.top > innerHeight || rect.left > innerWidth;
        if (!button.hidden) {
          button.style.left = Math.min(innerWidth - 36, rect.right - 36) + "px";
          button.style.top = Math.max(4, rect.top + 6) + "px";
        }
      }
    }

    function schedulePositions() {
      if (!frame) frame = requestAnimationFrame(positionButtons);
    }

    function refresh() {
      if (source && !root.contains(source)) close();
      for (const [original, button] of buttons) {
        if (!root.contains(original)) { button.remove(); buttons.delete(original); }
      }
      for (const original of root.querySelectorAll("img")) {
        if (buttons.has(original)) continue;
        const button = document.createElement("button");
        button.type = "button";
        button.className = "am-image-zoom";
        button.hidden = true;
        button.title = "Увеличить изображение";
        button.setAttribute("aria-label", "Увеличить изображение" + (original.alt ? ": " + original.alt : ""));
        button.innerHTML = magnifier;
        button.addEventListener("click", event => { event.preventDefault(); open(original, button); });
        layer.appendChild(button);
        buttons.set(original, button);
      }
      schedulePositions();
    }

    function imageClick(event) {
      if (event.button !== 0 || event.ctrlKey || event.shiftKey || event.altKey || event.metaKey) return;
      const original = event.target.closest("img");
      if (!original || original.closest("a, button, [role=button]")) return;
      event.preventDefault();
      event.stopPropagation();
      open(original);
    }

    function resize() {
      schedulePositions();
      if (dialog.open) setScale(fitMode ? fitScale() : scale);
    }

    const observer = new MutationObserver(refresh);
    observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ["src", "srcset", "alt"] });
    const sizeObserver = new ResizeObserver(schedulePositions);
    sizeObserver.observe(root);
    root.classList.add("am-image-preview-enabled");
    root.addEventListener("click", imageClick, true);
    root.addEventListener("load", schedulePositions, true);
    window.addEventListener("scroll", schedulePositions, true);
    window.addEventListener("resize", resize);
    refresh();

    const preview = { refresh, show: open, dispose: () => {
      close();
      observer.disconnect();
      sizeObserver.disconnect();
      if (frame) cancelAnimationFrame(frame);
      root.classList.remove("am-image-preview-enabled");
      root.removeEventListener("click", imageClick, true);
      root.removeEventListener("load", schedulePositions, true);
      window.removeEventListener("scroll", schedulePositions, true);
      window.removeEventListener("resize", resize);
      layer.remove();
      dialog.remove();
      previews.delete(root);
    } };
    previews.set(root, preview);
    return preview;
  };
})();
