const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const dependencies = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const root = path.resolve(__dirname, '..'), requireDeps = createRequire(path.join(dependencies, 'package.json'));
const { chromium } = requireDeps('playwright-core');
let checks = 0;
async function main() {
  fs.mkdirSync(output, { recursive: true }); process.env.TEMP = process.env.TMP = output;
  const browser = await chromium.launch({ channel: process.env.AM_BROWSER_CHANNEL || undefined, headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.route('http://navigation.test/**', async route => {
      const name = new URL(route.request().url()).pathname;
      if (name === '/editor.js' || name === '/editor.css') await route.fulfill({ body: fs.readFileSync(path.join(root, 'assets/editor', name.slice(1))), contentType: name.endsWith('.css') ? 'text/css' : 'text/javascript' });
      else if (name === '/markdown-it.js') await route.fulfill({ body: fs.readFileSync(path.join(dependencies, 'node_modules/markdown-it/dist/markdown-it.min.js')), contentType: 'text/javascript' });
      else await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="/editor.css"><style>#content{font:17px/1.7 sans-serif;padding:20px 40px}#content td,#content th{padding:8px}#content p{margin:20px 0}</style></head><body><div id="content"></div><script src="/markdown-it.js"></script><script src="/editor.js"></script></body></html>' });
    });
    await page.goto('http://navigation.test/');
    await page.evaluate(async () => {
      window.fixture = { session: 'initial', path: 'D:/notes/navigation.md', text: '# Initial\n', revision: 1, syncSelection: true, syncScroll: true };
      window.navMessages = []; window.writes = []; window.native = { anchor: 0, focus: 0, top: 0 }; let receiver;
      window.createLegacyView = async () => {
        const parser = markdownit(); AnotherMarkdownEditor.configurePreviewParser(parser);
        const setOptions = async () => { document.getElementById('content').innerHTML = parser.render(fixture.text); };
        await setOptions(); return { setOptions, dispose() {} };
      };
      window.chrome = { webview: { addEventListener(_, fn) { receiver = fn; }, removeEventListener() {},
        postMessage(message) {
          if (message.action === 'draft' || message.action === 'ui') return;
          if (message.action === 'navigation') {
            if (message.state.request) return;
            navMessages.push(message.state); Object.assign(native, message.state); return;
          }
          if (message.action === 'apply') {
            fixture.text = message.content; fixture.revision++; writes.push(message);
            if (message.navigation) Object.assign(native, message.navigation);
            setTimeout(() => receiver({ data: { type: 'am.editor.reply', reply: { requestId: message.requestId, status: 'applied', snapshot: { ...fixture } } } }), 5);
          }
        }
      } };
      await AnotherMarkdownEditor.create({ editorSnapshot: fixture });
      window.openDoc = async (text, edit = false) => {
        fixture = { ...fixture, text, session: 's' + (++fixture.revision), revision: fixture.revision + 1, translated: false, readOnly: false, syncSelection: true, syncScroll: true };
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
    const text = '# Heading\r\n\r\nA **bold** word, [link label](https://example.test/link-label), \u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00.\r\n\r\n- First item\r\n- Second __entry__\r\n\r\n| Name | Value |\r\n| --- | --- |\r\n| alpha | beta |\r\n\r\n```js\r\nconst x = 1;\r\n```\r\n';
    for (const edit of [false, true]) {
      const mode = edit ? 'editing' : 'preview';
      await page.evaluate(({ text, edit }) => openDoc(text, edit), { text, edit });
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
        assert.equal(result.anchor, 2); assert.equal(result.focus, 7);
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
        assert.equal(result.anchor, 21); assert.equal(result.focus, 36);
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
    await check('read-only preview still synchronizes; translation stays isolated', async () => {
      await page.evaluate(async () => { await openDoc('Read only.\n'); fixture.readOnly = true; await amEditor.receiveSnapshot({ ...fixture }); sourceSelect(0, 4); });
      assert.equal(await page.evaluate(() => highlightText()), 'Read');
      await page.evaluate(async () => { fixture.translated = true; fixture.session = 'translation'; await amEditor.receiveSnapshot({ ...fixture }); sourceSelect(0, 4); navMessages.length = 0; previewSelect('Read'); });
      await page.waitForTimeout(100);
      assert.equal(await page.evaluate(() => navMessages.length), 0); assert.equal(await page.evaluate(() => highlightText()), '');
    });
    await check('navigation never generates HTML or an extra undo step', async () => {
      await page.evaluate(async () => { await openDoc('**Original** text.\r\n', true); sourceSelect(2, 10); sourceSelect(5); });
      assert.equal(await page.evaluate(() => amEditor.source.serialize()), '**Original** text.\r\n');
      assert.equal(await page.evaluate(() => amEditor.editor.commands.get('undo').isEnabled), false);
      assert.equal(await page.evaluate(() => writes.length), 0);
    });
    assert.deepEqual(errors, []);
    await page.screenshot({ path: path.join(output, 'navigation.png') });
    console.log('PASS navigation: ' + checks + ' scenarios');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
