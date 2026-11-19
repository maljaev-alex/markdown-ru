const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const dependencies = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const root = path.resolve(__dirname, '..'), requireDeps = createRequire(path.join(dependencies, 'package.json'));
const { chromium } = requireDeps('playwright-core');
let checks = 0;
async function testNavigation(browser, doctype) {
  const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
  try {
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.route('http://navigation.test/**', async route => {
      const name = new URL(route.request().url()).pathname;
      if (name === '/editor.js' || name === '/editor.css') await route.fulfill({ body: fs.readFileSync(path.join(root, 'assets/editor', name.slice(1))), contentType: name.endsWith('.css') ? 'text/css' : 'text/javascript' });
      else if (name === '/markdown-it.js') await route.fulfill({ body: fs.readFileSync(path.join(dependencies, 'node_modules/markdown-it/dist/markdown-it.min.js')), contentType: 'text/javascript' });
      else await route.fulfill({ contentType: 'text/html', body: doctype + '<html><head><meta charset="utf-8"><link rel="stylesheet" href="/editor.css"><style>#content{font:17px/1.7 sans-serif;padding:20px 40px}#content td,#content th{padding:8px}#content p{margin:20px 0}</style></head><body><div id="content"></div><script src="/markdown-it.js"></script><script src="/editor.js"></script></body></html>' });
    });
    await page.goto('http://navigation.test/');
    await page.evaluate(require('./editor-mode-fixture.cjs'));
    await page.evaluate(async () => {
      window.fixture = { session: 'initial', path: 'D:/notes/navigation.md', text: '# Initial\n', revision: 1, syncSelection: true, syncScroll: true };
      window.navMessages = []; window.writes = []; window.drafts = 0; window.native = { anchor: 0, focus: 0, top: 0 }; let receiver;
      window.createLegacyView = async () => {
        const parser = markdownit(); MarkdownRuEditor.configurePreviewParser(parser);
        const setOptions = async () => { document.getElementById('content').innerHTML = parser.render(fixture.text); };
        await setOptions(); return { setOptions, dispose() {} };
      };
      window.chrome = { webview: { addEventListener(_, fn) { receiver = fn; }, removeEventListener() {},
        postMessage(message) {
          const mode = editorModeReply(message, fixture);
          if (mode) { setTimeout(() => receiver({ data: { type: 'am.editor.reply', reply: mode } }), 0); return; }
          if (message.action === 'draft') { drafts++; return; }
          if (message.action === 'ui') return;
          if (message.action === 'navigation') {
            if (message.state.request) return;
            const state = message.state;
            navMessages.push(state);
            if (state.selection) { native.anchor = state.anchor; native.focus = state.focus; }
            if (state.scroll) native.top = state.top;
            return;
          }
          if (message.action === 'apply') {
            fixture.text = message.content; fixture.revision++; writes.push(message);
            if (message.navigation) Object.assign(native, message.navigation);
            setTimeout(() => receiver({ data: { type: 'am.editor.reply', reply: { requestId: message.requestId, status: 'applied', snapshot: { ...fixture } } } }), 5);
          }
        }
      } };
      await MarkdownRuEditor.create({ editorSnapshot: fixture });
      window.openDoc = async (text, edit = false) => {
        fixture = { ...fixture, text, session: 's' + (++fixture.revision), revision: fixture.revision + 1, translated: false, readOnly: false, visualEditing: false, syncSelection: true, syncScroll: true };
        window.native = { anchor: 0, focus: 0, top: 0 };
        await amEditor.receiveSnapshot({ ...fixture });
        if (edit) await amEditorCommand('edit', fixture.session);
        navMessages.length = 0; writes.length = 0;
      };
      window.sourceSelect = (anchor, focus = anchor, more = {}) => amReceiveNavigation({ session: fixture.session, revision: fixture.revision, anchor, focus, top: 0, selection: true, scroll: false, reveal: false, ...more });
      window.modelText = () => [...amEditor.editor.model.document.selection.getFirstRange().getItems()].filter(n => n.is('$textProxy')).map(n => n.data).join('');
      window.highlightText = () => [...(CSS.highlights.get('am-synced-selection') || [])].map(r => r.toString()).join('');
      window.modelSelect = (needle, backward = false) => {
        const model = amEditor.editor.model;
        const text = [...model.createRangeIn(model.document.getRoot()).getItems()].find(n => n.is('$textProxy') && n.data.includes(needle));
        if (!text) throw Error('Missing model text: ' + needle);
        const start = text.startOffset + text.data.indexOf(needle);
        model.change(w => w.setSelection(w.createRange(w.createPositionAt(text.parent, start), w.createPositionAt(text.parent, start + needle.length)), { backward }));
      };
      window.previewSelect = (needle, backward = false) => {
        const walker = document.createTreeWalker(document.getElementById('content'), NodeFilter.SHOW_TEXT);
        for (let node; (node = walker.nextNode());) if (node.data.includes(needle)) {
          const start = node.data.indexOf(needle), end = start + needle.length;
          getSelection().setBaseAndExtent(node, backward ? end : start, node, backward ? start : end);
          document.getElementById('content').dispatchEvent(new PointerEvent('pointerup', { bubbles: true })); return;
        }
        throw Error('Missing preview text: ' + needle);
      };
    });
    const check = async (name, test) => { await test(); checks++; console.log('PASS ' + name); };
    const input = '# Heading\r\n\r\nA **bold** word, [link label](https://example.test/link-label), \u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00.\r\n\r\n- First item\r\n- Second __entry__\r\n\r\n| Name | Value |\r\n| --- | --- |\r\n| alpha | beta |\r\n\r\n```js\r\nconst x = 1;\r\n```\r\n';
    for (const edit of [false, true]) {
      const mode = edit ? 'editing' : 'preview';
      await page.evaluate(({ input, edit }) => openDoc(input, edit), { input, edit });
      const text = await page.evaluate(() => fixture.text);
      for (const needle of ['Heading', 'bold', 'link label', '\u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00', 'First item', 'entry', 'alpha', 'beta', 'const x = 1;']) {
        await check(mode + ' source selection: ' + needle, async () => {
          const start = text.indexOf(needle);
          await page.evaluate(({ start, length }) => sourceSelect(start, start + length), { start, length: needle.length });
          assert.equal(await page.evaluate(() => highlightText()), needle, await page.evaluate(() => JSON.stringify({ table: document.querySelector('#content table')?.outerHTML, ranges: [...(CSS.highlights.get('am-synced-selection') || [])].map(r => [r.startContainer.textContent, r.startOffset, r.endContainer.textContent, r.endOffset]) })));
          if (edit) assert.equal(await page.evaluate(() => modelText()), needle);
        });
      }
      await check(mode + ' reverse selection and UTF-16 offsets', async () => {
        const needle = '\u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00', start = text.indexOf(needle);
        await page.evaluate(({ needle, edit }) => edit ? modelSelect(needle, true) : previewSelect(needle, true), { needle, edit });
        await page.waitForTimeout(100);
        const result = await page.evaluate(() => navMessages.at(-1));
        assert.equal(result.anchor, start + needle.length); assert.equal(result.focus, start);
        assert.equal(await page.evaluate(() => writes.length), 0);
      });
      await check(mode + ' caret and stale messages do not steal focus or change text', async () => {
        const result = await page.evaluate(start => {
          document.activeElement?.blur(); const before = document.activeElement;
          sourceSelect(start);
          const selected = amEditor.navigation.capture();
          sourceSelect(0, 5, { session: 'other-tab' }); sourceSelect(0, 5, { revision: fixture.revision - 1 });
          return { stable: before === document.activeElement, hidden: document.querySelector('.am-synced-caret').hidden, selected, text: amEditor.source.serialize() };
        }, text.indexOf('bold') + 2);
        assert.equal(result.stable, true); assert.equal(result.hidden, false); assert.equal(result.text, text);
        if (edit) assert.equal(result.selected.anchor, text.indexOf('bold') + 2);
      });
    }
    await check('mode switch preserves selection in both directions', async () => {
      await page.evaluate(async () => { sourceSelect(fixture.text.indexOf('bold'), fixture.text.indexOf('bold') + 4); await amEditorCommand('preview', fixture.session); });
      assert.equal(await page.evaluate(() => highlightText()), 'bold');
      await page.evaluate(() => amEditorCommand('edit', fixture.session));
      assert.equal(await page.evaluate(() => modelText()), 'bold');
      await page.evaluate(() => sourceSelect(fixture.text.indexOf('bold') + 4, fixture.text.indexOf('bold')));
      assert.equal(await page.evaluate(() => amEditor.editor.model.document.selection.isBackward), true, await page.evaluate(() => JSON.stringify({ text: amEditor.state.text, dirty: amEditor.state.text!==amEditor.state.base, anchor: amEditor.editor.model.document.selection.anchor.path, focus: amEditor.editor.model.document.selection.focus.path })));
    });
    await check('typing sends the updated caret with the same text revision', async () => {
      await page.evaluate(async () => {
        await openDoc('Before **word** after.\n', true); modelSelect('word');
        const editor = amEditor.editor; editor.model.change(w => editor.model.insertContent(w.createText('changed')));
        await amEditor.flush(true);
      });
      const result = await page.evaluate(() => ({ request: writes.at(-1), native }));
      assert.equal(result.request.content, 'Before changed after.\n');
      assert.equal(result.native.anchor, 'Before changed'.length); assert.equal(result.native.focus, result.native.anchor);
    });
    await check('switching mode immediately after a selection retains it without waiting for debounce', async () => {
      await page.evaluate(async () => { await openDoc('Select **word** here.\n'); previewSelect('word', true); await amEditorCommand('edit', fixture.session); });
      assert.equal(await page.evaluate(() => modelText()), 'word');
      assert.equal(await page.evaluate(() => amEditor.editor.model.document.selection.isBackward), true);
      await page.evaluate(async () => { modelSelect('here'); await amEditorCommand('preview', fixture.session); });
      assert.equal(await page.evaluate(() => highlightText()), 'here');
    });
    await check('entities and escapes select their complete source spelling', async () => {
      for (const edit of [false, true]) {
        await page.evaluate(edit => openDoc('A &amp; B and \\* literal.\n', edit), edit);
        const result = await page.evaluate(edit => {
          if (edit) modelSelect('&'); else previewSelect('&');
          return amEditor.navigation.capture();
        }, edit);
        const text = await page.evaluate(() => fixture.text);
        assert.equal(result.anchor, 2); assert.equal(result.focus, text.startsWith('A &amp;') ? 7 : 3);
      }
    });
    await check('repeated words in link destinations are never mistaken for visible text', async () => {
      for (const edit of [false, true]) {
        await page.evaluate(edit => openDoc('[same](same) same again.\n', edit), edit);
        const selection = await page.evaluate(edit => {
          if (edit) modelSelect('same again'); else previewSelect('same again');
          return amEditor.navigation.capture();
        }, edit);
        assert.equal(selection.anchor, 13); assert.equal(selection.focus, 23);
      }
    });
    await check('wrapped source lines keep exact selection offsets with either editor setting', async () => {
      const text = 'First wrapped line\r\nSecond **target** line\r\nLast line.\r\n';
      for (const preserve of [false, true]) {
        await page.evaluate(async ({ text, preserve }) => { fixture.preserveLineBreaks = preserve; await openDoc(text, true); }, { text, preserve });
        const start = text.indexOf('target');
        await page.evaluate(start => sourceSelect(start, start + 6), start);
        assert.equal(await page.evaluate(() => modelText()), 'target');
        const selected = await page.evaluate(() => {
          const model = amEditor.editor.model;
          model.change(w => w.setSelection(model.document.getRoot(), 'end'));
          modelSelect('target', true); return amEditor.navigation.capture();
        });
        assert.equal(selected.anchor, start + 6); assert.equal(selected.focus, start);
        assert.equal(await page.evaluate(() => amEditor.source.serialize()), text);
      }
      await page.evaluate(() => { fixture.preserveLineBreaks = false; });
    });
    await check('external source update remaps the new revision', async () => {
      await page.evaluate(async () => {
        fixture.text = 'An **external** update.\n'; fixture.revision++; await amEditor.receiveSnapshot({ ...fixture }); sourceSelect(5, 13);
      });
      assert.equal(await page.evaluate(() => modelText()), 'external');
    });
    await check('unrecognized link syntax remains selectable as ordinary text', async () => {
      for (const edit of [false, true]) {
        await page.evaluate(edit => openDoc('**Bold** and [label](not a valid URL).\n', edit), edit);
        const result = await page.evaluate(edit => {
          if (edit) modelSelect('not a valid URL'); else previewSelect('not a valid URL');
          return amEditor.navigation.capture();
        }, edit);
        const start = (await page.evaluate(() => fixture.text)).indexOf('not a valid URL');
        assert.equal(result.anchor, start); assert.equal(result.focus, start + 'not a valid URL'.length);
      }
    });
    const long = Array.from({ length: 80 }, (_, i) => '## Section ' + i + '\n\nParagraph number ' + i + ' with enough words to occupy a line.\n').join('\n');
    for (const edit of [false, true]) {
      await check((edit ? 'editing' : 'preview') + ' scrolls both ways without an echo', async () => {
        await page.evaluate(({ text, edit }) => openDoc(text, edit), { text: long, edit });
        await page.waitForTimeout(120);
        await page.evaluate(() => { navMessages.length = 0; sourceSelect(0, 0, { selection: false, scroll: true, top: fixture.text.indexOf('Section 40') }); });
        await page.waitForTimeout(150);
        const before = await page.evaluate(edit => ({ scroll: edit ? document.querySelector('.am-editor-surface').scrollTop : scrollY, count: navMessages.length }), edit);
        assert.ok(before.scroll > 1000); assert.equal(before.count, 0);
        await page.evaluate(edit => { if (edit) document.querySelector('.am-editor-surface').scrollTop += 350; else scrollBy(0, 350); }, edit);
        await page.waitForTimeout(150);
        const message = await page.evaluate(() => navMessages.at(-1));
        assert.equal(message.scroll, true); assert.equal(message.selection, false); assert.ok(message.top > long.indexOf('Section 40'));
      });
    }
    const tallTable = ['| Key | Description |', '| --- | --- |',
      ...Array.from({ length: 75 }, (_, i) => `| Record-${i} | Some **formatted** text for row ${i}. |`)].join('\n') + '\n';
    for (const edit of [false, true]) {
      await check((edit ? 'editing' : 'preview') + ' follows visible table rows throughout a scroll gesture', async () => {
        await page.evaluate(async ({ text, edit }) => {
          await openDoc(text, edit);
          const table = document.querySelector(edit ? '.ck-content table' : '#content table');
          table.style.cssText += ';width:70%;margin-inline:auto;table-layout:fixed';
          for (const row of table.rows) row.style.height = '68px';
          const surface = edit ? document.querySelector('.am-editor-surface') : document.scrollingElement;
          const top = edit ? surface.getBoundingClientRect().top + surface.clientTop : 0;
          window.visibleSourceRow = () => {
            const row = [...table.rows].find(row => {
              const rect = row.getBoundingClientRect();
              return rect.top <= top + 10 && rect.bottom > top + 10;
            });
            return row.cells[0].textContent.trim();
          };
          window.reportedSourceRow = offset => fixture.text.slice(0, offset).split('\n').length - 1;
          window.expectedSourceRow = () => fixture.text.split('\n').findIndex(line => line.includes('|') && line.split('|')[1].trim() === visibleSourceRow());
          surface.scrollTop += table.rows[12].getBoundingClientRect().top - top + 5;
        }, { text: tallTable, edit });
        await page.mouse.move(500, 400);
        for (const delta of [150, 210, -110]) {
          await page.evaluate(() => { navMessages.length = 0; });
          await page.mouse.wheel(0, delta);
          await page.waitForFunction(() => navMessages.some(state => state.scroll), null, { timeout: 2000 });
          const row = await page.evaluate(() => [reportedSourceRow(native.top), expectedSourceRow()]);
          assert.equal(row[0], row[1], 'scroll anchor must belong to the visible data row');
        }
        const updates = await page.evaluate(async edit => {
          navMessages.length = 0; drafts = 0;
          const surface = edit ? document.querySelector('.am-editor-surface') : document.scrollingElement;
          for (let i = 0; i < 24; i++) {
            await new Promise(requestAnimationFrame);
            surface.scrollTop += 19;
          }
          return new Set(navMessages.filter(s => s.scroll).map(s => reportedSourceRow(s.top))).size;
        }, edit);
        assert.ok(updates >= 2, 'the source should follow while the gesture is still in progress');
        await page.waitForFunction(() => reportedSourceRow(native.top) === expectedSourceRow(), null, { timeout: 2000 });
        assert.equal(await page.evaluate(() => navMessages.some(s => s.left != null)), false);
        const samples = await page.evaluate(edit => {
          const table = document.querySelector(edit ? '.ck-content table' : '#content table');
          const surface = edit ? document.querySelector('.am-editor-surface') : document.scrollingElement;
          const row = table.rows[20], origin = edit ? surface.getBoundingClientRect().top + surface.clientTop : 0;
          row.style.height = '220px';
          const height = row.getBoundingClientRect().height;
          return [.08, .25, .5, .76, .94, .76, .25].map(fraction => {
            surface.scrollTop += row.getBoundingClientRect().top - origin - 10 + height * fraction;
            const state = amEditor.navigation.capture(false, true);
            return [reportedSourceRow(state.top), reportedSourceRow(state.nextTop), state.topFraction, fraction];
          });
        }, edit);
        for (const [start, end, actual, expected] of samples) {
          assert.equal(start, 21); assert.equal(end, 22); assert.ok(Math.abs(actual - expected) < .02);
        }
        const restored = await page.evaluate(() => {
          const saved = amEditor.navigation.capture(false, true);
          sourceSelect(0, 0, { selection: false, scroll: true, top: 0 });
          amReceiveNavigation(saved);
          return [saved.topFraction, amEditor.navigation.capture(false, true).topFraction];
        });
        assert.ok(Math.abs(restored[0] - restored[1]) < .02, 'restoring a viewport keeps its position within the row');
        assert.deepEqual(await page.evaluate(() => [writes.length, drafts]), [0, 0]);
      });
    }
    await check('the preview and focused CKEditor repeat the source blink interval', async () => {
      await page.evaluate(async () => {
        await openDoc('Read here.\n');
        fixture.caretBlinkPeriod = 370;
        await amEditor.receiveSnapshot({ ...fixture });
        sourceSelect(1);
      });
      const sample = async (selector, name, property) => page.locator(selector).evaluate((element, { name, property }) => {
        const animation = element.getAnimations().find(a => a.animationName === name);
        if (!animation) throw Error('Missing animation ' + name);
        const timing = animation.effect.getTiming();
        animation.pause();
        const phases = [0, 371, 741, 1111].map(t => {
          animation.currentTime = t;
          return getComputedStyle(element)[property];
        });
        animation.play();
        return { duration: timing.duration, continuous: timing.iterations === Infinity, phases };
      }, { name, property });
      const preview = await sample('.am-synced-caret', 'am-marker-blink', 'opacity');
      assert.deepEqual(preview, { duration: 740, continuous: true, phases: ['1', '0', '1', '0'] });
      await page.evaluate(() => amEditorCommand('edit', fixture.session));
      await page.locator('.ck-content p').click();
      await page.waitForFunction(() => document.body.classList.contains('am-draws-caret'));
      assert.deepEqual(await sample('.am-synced-caret', 'am-marker-blink', 'opacity'), preview);
      const active = await page.evaluate(() => ({
        native: getComputedStyle(document.activeElement).caretColor,
        width: document.querySelector('.am-synced-caret').getBoundingClientRect().width * devicePixelRatio
      }));
      assert.equal(active.native, 'rgba(0, 0, 0, 0)'); assert.ok(Math.abs(active.width - 2) < 0.05);
      assert.deepEqual(await page.evaluate(() => [writes.length, amEditor.state.text]), [0, 'Read here.\n']);
      await page.evaluate(() => { delete fixture.caretBlinkPeriod; });
    });
    await check('the CKEditor insertion point stays visible through a focus loss and return', async () => {
      await page.evaluate(() => openDoc('Read here.\n', true));
      await page.locator('.ck-content p').click();
      await page.waitForFunction(() => navMessages.some(s => s.selection) && amEditor.editor.editing.view.document.isFocused);
      const offset = await page.evaluate(() => amEditor.navigation.capture().focus);
      const passiveVisible = () => {
        const caret = document.querySelector('.am-synced-caret');
        return !caret.hidden && caret.getBoundingClientRect().height > 0;
      };
      await page.waitForFunction(passiveVisible);
      await page.evaluate(() => document.activeElement.blur());
      await page.waitForFunction(passiveVisible);
      assert.equal(await page.evaluate(() => document.body.classList.contains('am-draws-caret')), false);
      await page.evaluate(() => amEditorCommand('restoreFocus', fixture.session));
      await page.waitForFunction(() => amEditor.editor.editing.view.document.isFocused && document.body.classList.contains('am-draws-caret'));
      assert.equal(await page.evaluate(() => amEditor.navigation.capture().focus), offset);
      assert.equal(await page.evaluate(() => writes.length), 0);
    });
    await check('a local editor caret stays thick when source synchronization is disabled', async () => {
      await page.evaluate(async () => {
        await openDoc('Read **bold** text.\n', true);
        fixture.syncSelection = false; await amEditor.receiveSnapshot({ ...fixture }); navMessages.length = 0;
      });
      await page.locator('.ck-content strong').click();
      await page.waitForFunction(() => document.body.classList.contains('am-draws-caret') && !document.querySelector('.am-synced-caret').hidden);
      assert.equal(await page.evaluate(() => navMessages.some(s => s.selection)), false);
      await page.evaluate(() => modelSelect('bold'));
      await page.waitForFunction(() => document.querySelector('.am-synced-caret').hidden);
      assert.equal(await page.evaluate(() => modelText()), 'bold');
    });
    await check('holding the down arrow mirrors intermediate and final CKEditor carets', async () => {
      const text = Array.from({ length: 60 }, (_, i) => 'Line ' + i + ' with some text.').join('\n\n') + '\n';
      await page.evaluate(text => openDoc(text, true), text);
      await page.locator('.ck-content p').first().click();
      await page.waitForFunction(() => navMessages.some(s => s.selection));
      await page.evaluate(() => { navMessages.length = 0; drafts = 0; });
      for (let i = 0; i < 22; i++) {
        await page.keyboard.down('ArrowDown');
        await page.waitForTimeout(10);
      }
      const during = await page.evaluate(() => new Set(navMessages.filter(s => s.selection).map(s => s.focus)).size);
      await page.keyboard.up('ArrowDown');
      assert.ok(during >= 2, 'the source caret should move before keyup');
      await page.waitForFunction(() => native.focus === amEditor.navigation.capture().focus);
      assert.ok(await page.evaluate(() => native.focus > fixture.text.indexOf('Line 15 ')));
      assert.deepEqual(await page.evaluate(() => [writes.length, drafts]), [0, 0]);
    });
    await check('read-only preview still synchronizes; translation stays isolated', async () => {
      await page.evaluate(async () => { await openDoc('Read only.\n'); fixture.readOnly = true; await amEditor.receiveSnapshot({ ...fixture }); sourceSelect(0, 4); });
      assert.equal(await page.evaluate(() => highlightText()), 'Read');
      await page.evaluate(async () => { fixture.translated = true; fixture.session = 'translation'; await amEditor.receiveSnapshot({ ...fixture }); sourceSelect(0, 4); navMessages.length = 0; previewSelect('Read'); });
      await page.waitForTimeout(100);
      assert.equal(await page.evaluate(() => navMessages.length), 0); assert.equal(await page.evaluate(() => highlightText()), '');
    });
    await check('clearing a multi-item preview selection collapses the source at its focus', async () => {
      const list = '- First item\n- Second item\n- Third item\n- Fourth item\n- Last item\n';
      await page.evaluate(text => openDoc(text), list);
      const first = list.indexOf('First'), end = list.indexOf('Last') + 4;
      for (const backwards of [false, true]) {
        await page.evaluate(backwards => {
          const items = document.querySelectorAll('#content li'), first = items[0].firstChild, last = items[4].firstChild;
          if (backwards) getSelection().setBaseAndExtent(last, 4, first, 0);
          else getSelection().setBaseAndExtent(first, 0, last, 4);
        }, backwards);
        const anchor = backwards ? end : first, focus = backwards ? first : end;
        await page.waitForFunction(([a, f]) => native.anchor === a && native.focus === f, [anchor, focus]);
        await page.evaluate(() => getSelection().removeAllRanges());
        await page.waitForFunction(f => native.anchor === f && native.focus === f, focus, { timeout: 1500 });
      }
      assert.equal(await page.evaluate(() => writes.length), 0);
    });
    await check('a click that clears the DOM selection uses the clicked text position', async () => {
      await page.evaluate(async () => { await openDoc('- First item\n- Second item\n- Last item\n'); previewSelect('Last item'); });
      await page.waitForFunction(() => native.anchor !== native.focus);
      const point = await page.locator('#content li').first().evaluate(li => {
        const node = li.firstChild, range = document.createRange();
        range.setStart(node, 3); range.setEnd(node, 4);
        const rect = range.getBoundingClientRect(), x = rect.left + 1, y = (rect.top + rect.bottom) / 2;
        const hit = document.caretRangeFromPoint(x, y);
        // WebView2 can discard even the collapsed DOM range after the click.
        li.addEventListener('click', () => getSelection().removeAllRanges(), { once: true });
        return { x, y, expected: fixture.text.indexOf('First') + hit.startOffset };
      });
      await page.mouse.click(point.x, point.y);
      await page.waitForFunction(offset => native.anchor === offset && native.focus === offset, point.expected, { timeout: 1500 });
      assert.equal(await page.evaluate(() => getSelection().rangeCount), 0);
    });
    await check('scroll-only navigation cannot cancel a pending preview selection', async () => {
      await page.evaluate(async () => {
        await openDoc('- Before\n- New selection\n');
        previewSelect('New selection'); navMessages.length = 0;
        sourceSelect(0, 0, { selection: false, scroll: true });
      });
      await page.waitForFunction(() => navMessages.some(s => s.selection && fixture.text.slice(s.anchor, s.focus) === 'New selection'), null, { timeout: 1500 });
    });
    await check('source navigation does not echo a browser range clear or discard a matching selection', async () => {
      await page.evaluate(async () => { await openDoc('Mirror this word.\n'); previewSelect('this'); });
      await page.waitForFunction(() => fixture.text.slice(native.anchor, native.focus) === 'this');
      await page.evaluate(() => { navMessages.length = 0; sourceSelect(native.anchor, native.focus); });
      assert.equal(await page.evaluate(() => getSelection().toString()), 'this');
      await page.evaluate(() => sourceSelect(0, 6));
      await page.waitForTimeout(125);
      assert.equal(await page.evaluate(() => highlightText()), 'Mirror');
      assert.equal(await page.evaluate(() => navMessages.filter(s => s.selection).length), 0);
    });
    const tableDoc = [
      '# Tables', '', '| Key | Description | Status | Notes |', '| --- | --- | --- | --- |',
      ...Array.from({ length: 42 }, (_, i) =>
        '| Key-' + i + ' | ' + (i === 31 ? 'Path to `config.yml` for `ibcmd`-based ops' : 'Description') +
        ' | Defaulted | ' + (i === 2 ? 'wide '.repeat(115) : i === 9 ? '' : 'repeat') + ' |'),
      '', '| Name | Status |', '| --- | --- |', '| Other | Defaulted |', ''
    ].join('\r\n');
    async function selectTable(mode, col, kind, a, f) {
      return page.evaluate(({ mode, col, kind, a, f }) => {
        const index = 32;
        if (mode === 'edit') {
          const m = amEditor.editor.model, table = [...m.document.getRoot().getChildren()].find(n => n.name === 'table');
          const cell = table.getChild(index).getChild(col), p = cell.getChild(0);
          m.change(w => {
            if (kind === 'cell') { w.setSelection(cell, 'on'); return; }
            w.setSelection(p, a ?? 0); w.setSelectionFocus(p, f ?? p.maxOffset);
          });
        } else {
          const cell = document.querySelector('#content table').rows[index].cells[col];
          const walker = document.createTreeWalker(cell, NodeFilter.SHOW_TEXT), nodes = [];
          while (walker.nextNode()) nodes.push(walker.currentNode);
          getSelection().setBaseAndExtent(nodes[0], a ?? 0, nodes.at(-1), f ?? nodes.at(-1).length);
        }
        const { anchor, focus } = amEditor.navigation.capture();
        return [anchor, focus];
      }, { mode, col, kind, a, f });
    }
    await check('padded tables map selection, cell boundaries and carets to the correct row in both directions', async () => {
      await page.evaluate(text => openDoc(text), tableDoc);
      const raw = 'Path to `config.yml` for `ibcmd`-based ops';
      const visible = 'Path to config.yml for ibcmd-based ops';
      for (const mode of ['preview', 'edit']) {
        if (mode === 'edit') await page.evaluate(() => amEditorCommand('edit', fixture.session));
        const text = await page.evaluate(() => fixture.text), start = text.indexOf(raw);
        assert.deepEqual(await selectTable(mode, 1), [start, start + raw.length]);
        const status = text.indexOf('Defaulted', start);
        assert.deepEqual(await selectTable(mode, 2), [status, status + 9]);
        if (mode === 'edit') {
          assert.deepEqual(await selectTable(mode, 1, 'cell'), [start, start + raw.length]);
          assert.deepEqual(await selectTable(mode, 1, 'text', visible.length, 0), [start + raw.length, start]);
          const caret = visible.indexOf('ibcmd') + 2, source = start + raw.indexOf('ibcmd') + 2;
          assert.deepEqual(await selectTable(mode, 1, 'text', caret, caret), [source, source]);
          await page.evaluate(([s, f]) => sourceSelect(s, f), [start, start + raw.length]);
          assert.equal(await page.evaluate(() => modelText()), visible);
        }
      }
    });
    await check('formatting and restoring a wide table keep the same cell, including the next table', async () => {
      await page.evaluate(text => openDoc(text), tableDoc);
      const old = tableDoc.lastIndexOf('Defaulted');
      await page.evaluate(([a,f]) => sourceSelect(a,f), [old+9, old]);
      await page.evaluate(() => amEditorCommand('edit', fixture.session));
      assert.equal(await page.evaluate(() => modelText()), 'Defaulted');
      assert.equal(await page.evaluate(() => amEditor.editor.model.document.selection.isBackward), true);
      const result = await page.evaluate(() => {
        const { anchor, focus } = amEditor.navigation.capture();
        return [anchor,focus,fixture.text.lastIndexOf('Defaulted')];
      });
      assert.deepEqual(result.slice(0,2), [result[2]+9,result[2]]);
      await page.evaluate(() => amEditorCommand('preview', fixture.session));
      assert.equal(await page.evaluate(() => highlightText()), 'Defaulted');
      assert.equal(await page.evaluate(() => fixture.text), tableDoc);
    });
    await check('editing a cell changes padding without moving later cell selections', async () => {
      await page.evaluate(async text => {
        await openDoc(text, true);
        const m = amEditor.editor.model, table = [...m.document.getRoot().getChildren()].find(n => n.name === 'table');
        m.change(w => w.insertText('New width '.repeat(20), table.getChild(2).getChild(0).getChild(0), 0));
        await amEditor.flush(true);
      }, tableDoc);
      const text = await page.evaluate(() => fixture.text);
      const start = text.indexOf('Path to `config.yml`'), repeated = text.indexOf('Defaulted', start);
      assert.deepEqual(await selectTable('edit', 2), [repeated,repeated+9]);
    });
    await check('empty cells, escaped pipes and UTF-16 characters keep their cell boundaries', async () => {
      const input = '| First | Second | Third |\n| --- | --- | --- |\n| left |  | `A\\|B` \ud83d\ude00 |\n';
      for (const edit of [false, true]) {
        const result = await page.evaluate(async ({input,edit}) => {
          await openDoc(input,edit);
          const table = [...amEditor.editor.model.document.getRoot().getChildren()].find(n=>n.name==='table');
          const row = table.getChild(1), dom = document.querySelector('#content table').rows[1];
          if (edit) amEditor.editor.model.change(w=>w.setSelection(row.getChild(1).getChild(0),0));
          else getSelection().collapse(dom.cells[1],0);
          const empty = amEditor.navigation.capture().focus;
          if (edit) amEditor.editor.model.change(w=>{const p=row.getChild(2).getChild(0);w.setSelection(p,1);w.setSelectionFocus(p,2);});
          else {const n=dom.cells[2].querySelector('code').firstChild;getSelection().setBaseAndExtent(n,1,n,2);}
          const pipe = amEditor.navigation.capture(), emoji = fixture.text.indexOf('\ud83d\ude00');
          sourceSelect(emoji,emoji+2);
          return {text:fixture.text,empty,pipe:fixture.text.slice(pipe.anchor,pipe.focus),emoji:edit?modelText():highlightText()};
        }, {input,edit});
        const row = result.text.split('\n').find(line=>line.includes('left')), from = result.text.indexOf(row);
        const left = row.indexOf('|',row.indexOf('left')), right = row.indexOf('|',left+1);
        assert.ok(result.empty > from+left && result.empty <= from+right);
        assert.equal(result.pipe,'\\|'); assert.equal(result.emoji,'\ud83d\ude00');
      }
    });
    await check('nested list paragraphs with identical text map to the selected item', async () => {
      const text = Array.from({length:150},(_,i)=>'- Record '+i+'\n  - same **nested text**').join('\n')+'\n';
      await page.evaluate(text=>openDoc(text,true),text);
      const result = await page.evaluate(()=>{
        const m=amEditor.editor.model;
        const items=[...m.document.getRoot().getChildren()].filter(n=>[...n.getChildren()].some(t=>t.data==='nested text'));
        m.change(w=>{w.setSelection(items[120],5);w.setSelectionFocus(items[120],16);});
        const {anchor,focus}=amEditor.navigation.capture();
        return [anchor,focus,fixture.text.indexOf('nested text',fixture.text.indexOf('Record 120'))];
      });
      assert.deepEqual(result.slice(0,2),[result[2],result[2]+11]);
    });
    for (const edit of [false, true]) {
      await check((edit ? 'editing' : 'preview') + ' reveals the caret inside horizontally scrolled content with a simultaneous viewport update', async () => {
        const text = '# Top\n\n' + 'Short paragraph.\n\n'.repeat(24) + '```text\n' + 'abcdefgh'.repeat(120) + 'TARGET\n```\n';
        await page.evaluate(async ({ text, edit }) => {
          await openDoc(text, edit);
          const pre = document.querySelector(edit ? '.ck-content pre' : '#content pre');
          pre.style.cssText += ';max-width:280px;overflow-x:auto;white-space:pre';
          pre.querySelector('code').style.whiteSpace = 'pre';
          navMessages.length = 0;
          sourceSelect(text.indexOf('TARGET') + 2, text.indexOf('TARGET') + 2, { top:0, scroll:true, reveal:true });
        }, { text, edit });
        await page.waitForTimeout(120);
        const result = await page.evaluate(edit => {
          const pre = document.querySelector(edit ? '.ck-content pre' : '#content pre');
          const caret = document.querySelector('.am-synced-caret'), rect = caret.getBoundingClientRect(), box = pre.getBoundingClientRect();
          return { x:pre.scrollLeft, visible:!caret.hidden && rect.left >= box.left && rect.left < box.right &&
            rect.top >= 0 && rect.bottom <= innerHeight, echoes:navMessages.filter(s=>s.scroll || s.left != null).length };
        }, edit);
        assert.ok(result.x > 0 && result.visible, JSON.stringify(result));
        assert.equal(result.echoes, 0);
        await page.evaluate(edit=>{
          navMessages.length=0;
          const pre=document.querySelector(edit?'.ck-content pre':'#content pre');
          pre.scrollLeft=Math.round(pre.scrollLeft/2);
        },edit);
        await page.waitForFunction(()=>navMessages.some(s=>s.left != null),null,{timeout:1500});
        const scroll = await page.evaluate(()=>navMessages.filter(s=>s.left != null).at(-1));
        assert.ok(scroll.left > text.indexOf('abcdefgh')+10 && scroll.left < text.indexOf('TARGET'), JSON.stringify(scroll));
        assert.equal(scroll.scroll,false); assert.equal(scroll.selection,false);
        const position = await page.evaluate(edit => {
          const pre = document.querySelector(edit ? '.ck-content pre' : '#content pre');
          const x = pre.scrollLeft, top = fixture.text.indexOf('TARGET');
          sourceSelect(0, 0, { top, selection: false, scroll: true });
          return [x, pre.scrollLeft];
        }, edit);
        assert.equal(position[0], position[1], 'a vertical source update preserves the horizontal code viewport');
      });
    }
    await check('navigation never generates HTML or an extra undo step', async () => {
      await page.evaluate(async () => { await openDoc('**Original** text.\r\n', true); sourceSelect(2, 10); sourceSelect(5); });
      assert.equal(await page.evaluate(() => amEditor.source.serialize()), '**Original** text.\r\n');
      assert.equal(await page.evaluate(() => amEditor.editor.commands.get('undo').isEnabled), false);
      assert.equal(await page.evaluate(() => writes.length), 0);
    });
    assert.deepEqual(errors, []);
    await page.screenshot({ path: path.join(output, doctype ? 'navigation.png' : 'navigation-quirks.png') });
  } finally { await page.close(); }
}
async function main() {
  fs.mkdirSync(output, { recursive: true }); process.env.TEMP = process.env.TMP = output;
  const browser = await chromium.launch({ channel: process.env.AM_BROWSER_CHANNEL || undefined, headless: true });
  try {
    // The shipped WebView2 loader uses quirks mode. Also exercise the standards-mode viewport.
    for (const doctype of ['', '<!doctype html>']) await testNavigation(browser, doctype);
    console.log('PASS navigation: ' + checks + ' scenarios');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
