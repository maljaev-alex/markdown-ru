const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const dependencies = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
const root = path.resolve(__dirname, '..');
const { chromium } = createRequire(path.join(dependencies, 'package.json'))('playwright-core');
let checks = 0;
async function main() {
  fs.mkdirSync(output, { recursive: true });
  process.env.TEMP = process.env.TMP = output;
  const browser = await chromium.launch({ channel: process.env.AM_BROWSER_CHANNEL || undefined, headless: true });
  const page = await browser.newPage({ viewport: { width: 1050, height: 850 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  page.on('console', msg => { if (msg.type() === 'error' || msg.type() === 'warning') console.log('BROWSER ' + msg.text()); });
  await page.route('http://visual.test/**', async route => {
    const name = new URL(route.request().url()).pathname;
    if (name === '/editor.js' || name === '/editor.css') await route.fulfill({ body: fs.readFileSync(path.join(root, 'assets/editor', name.slice(1))), contentType: name.endsWith('css') ? 'text/css' : 'text/javascript' });
    else await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><head><meta charset="utf-8"><base href="http://visual.test/"><link rel="stylesheet" href="/editor.css"></head><body><div id="content"></div><script src="/editor.js"></script></body></html>' });
  });
  await page.goto('http://visual.test/');
  await page.evaluate(require('./editor-mode-fixture.cjs'));
  await page.evaluate(async () => {
    window.fixture = { session: 'one', path: 'D:/notes/test.md', text: '# Title\n\nA paragraph.\n', revision: 1, modified: false };
    window.writes = []; window.saves = []; window.drafts = {}; window.uiStates = []; let receiver;
    window.createLegacyView = async () => {
      const setOptions = async () => { document.getElementById('content').textContent = fixture.text; };
      await setOptions(); return { setOptions, scrollToLine() {}, dispose() {} };
    };
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      addEventListener(_, fn) { receiver = fn; }, removeEventListener() {},
      postMessage(message) {
        if (message.action === 'draft') { drafts[message.session] = message; return; }
        if (message.action === 'ui') { uiStates.push(message.state); return; }
        if (message.action === 'navigation') return;
        const run = () => {
          const mode = editorModeReply(message, fixture);
          if (mode) { receiver({ data: { type: 'am.editor.reply', reply: mode } }); return; }
          let reply = { requestId: message.requestId, status: 'state', snapshot: { ...fixture } };
          if (message.action !== 'state') {
            if (message.session !== fixture.session) reply.status = 'stale';
            else if (message.action === 'image') { reply.status = 'image'; reply.url = fixture.translated ? message.data : 'test.assets/pasted.png'; }
            else if (message.baseText !== fixture.text) reply.status = 'conflict';
            else if (message.action === 'apply') {
              fixture.text = message.content; fixture.revision++; fixture.modified = true; writes.push(message);
              reply = { ...reply, status: 'applied', snapshot: { ...fixture } };
            } else if (message.action === 'save' || message.action === 'saveAs') { saves.push(message); reply.status = 'saved'; }
          }
          receiver({ data: { type: 'am.editor.reply', reply } });
        };
        if (window.hold && message.action === 'apply') window.held = run; else setTimeout(run, 5);
      }
    };
    await AnotherMarkdownEditor.create({ editorSnapshot: fixture });
    window.newDoc = async (text, edit = true) => {
      window.fixture = { ...fixture, session: 's' + (++fixture.revision), text, revision: fixture.revision + 1, translated: false, readOnly: false, visualEditing: false, modified: false };
      await amEditor.receiveSnapshot({ ...fixture });
      if (edit) await amEditorCommand('edit', fixture.session);
    };
    window.editFirstParagraph = text => {
      const model = amEditor.editor.model;
      const node = [...model.document.getRoot().getChildren()].find(n => n.name === 'paragraph');
      if (!node) throw Error('Missing paragraph');
      model.change(writer => writer.insertText(text, node, 0));
    };
    window.replaceModelText = (needle, replacement) => {
      const model = amEditor.editor.model;
      const text = [...model.createRangeIn(model.document.getRoot()).getItems()].find(node => node.is('$textProxy') && node.data.includes(needle));
      if (!text) throw Error('Missing model text: ' + needle);
      const offset = text.startOffset + text.data.indexOf(needle), parent = text.parent;
      const attributes = Object.fromEntries(text.getAttributes());
      model.change(writer => {
        writer.remove(writer.createRange(writer.createPositionAt(parent, offset), writer.createPositionAt(parent, offset + needle.length)));
        writer.insertText(replacement, attributes, parent, offset);
      });
    };
  });
  async function check(label, operation) { await operation(); checks++; console.log('PASS ' + label); }
  await check('files start in preview with formatting tools hidden', async () => {
    assert.equal(await page.locator('#content').isVisible(), true);
    assert.equal(await page.locator('.am-editor-toolbar').isVisible(), false);
    assert.equal(await page.locator('.am-editor-surface').isVisible(), false);
    assert.equal(await page.evaluate(() => uiStates.at(-1).editing), false);
    assert.equal(await page.evaluate(() => writes.length), 0);
    assert.equal(await page.locator('.am-primary').isVisible(), false);
  });
  await check('host commands switch mode and reject a stale tab', async () => {
    await page.evaluate(() => amEditorCommand('edit', 'stale-tab'));
    assert.equal(await page.locator('#content').isVisible(), true);
    await page.evaluate(() => amEditorCommand('edit', fixture.session));
    assert.equal(await page.locator('.am-editor-toolbar').isVisible(), true);
    assert.equal(await page.evaluate(() => uiStates.at(-1).editing), true);
    await page.evaluate(async () => { fixture.text += '\nSource change.\n'; fixture.revision++; await amEditor.receiveSnapshot({ ...fixture }); });
    assert.equal(await page.locator('.am-editor-toolbar').isVisible(), true);
    await page.evaluate(() => amEditorCommand('preview', fixture.session));
    assert.equal(await page.locator('#content').textContent(), await page.evaluate(() => fixture.text));
  });
  const original = '# Heading\r\n\r\nSome __bold__ and *italic* text.\r\n\r\n* one\r\n* two\r\n\r\n| A | B |\r\n| :-- | --: |\r\n| 1 | 2 |\r\n';
  await check('untouched Markdown and CRLF remain byte-for-byte', async () => {
    await page.evaluate(text => newDoc(text, false), original);
    assert.equal(await page.evaluate(() => amEditor.source.serialize()), original);
    assert.equal(await page.locator('.am-editor-toolbar').isVisible(), false);
  });
  await check('enter formats distant blocks, including ordinary Markdown beside literal syntax, before the first edit', async () => {
    const input = [
      'First', '=====', '', '+ __Before__', '+ __Middle__ <kbd>F2</kbd> and :smile:', '+ __After__', '',
      '::: tip', '', '+ __Inside__', '', ':::', '', 'Last', '----', ''
    ].join('\r\n');
    const canonical = [
      '# First', '', '* **Before**', '* **Middle** <kbd>F2</kbd> and :smile:', '* **After**', '',
      '::: tip', '', '* **Inside**', '', ':::', '', '## Last', ''
    ].join('\r\n');
    await page.evaluate(text => newDoc(text, false), input);
    await page.evaluate(() => amEditorCommand('edit', fixture.session));
    assert.equal(await page.evaluate(() => fixture.text), canonical);
    assert.equal(await page.evaluate(() => amEditor.editor.getData()), canonical.trimEnd());
    await page.evaluate(() => amEditorCommand('preview', fixture.session));
    assert.equal(await page.evaluate(() => fixture.text), input);
    await page.evaluate(async () => {
      await amEditorCommand('edit', fixture.session);
      replaceModelText('Middle', 'Changed');
      await amEditorCommand('preview', fixture.session);
    });
    assert.equal(await page.evaluate(() => fixture.text), canonical.replace('Middle', 'Changed'));
  });
  await check('native CKEditor setData/getData round-trips extended Markdown without a host restore', async () => {
    const input = '---\ntitle: Demo\n---\n\nUse $x^2$ here and [guide][doc].\n\n[doc]: next.md "Manual"\n\n$$\nx^2\n$$\n\n<!-- comment -->\n\n::: tip\n\n+ __literal__\n\n:::';
    const result = await page.evaluate(text => {
      const editor = amEditor.editor;
      editor.setData(text);
      const first = editor.getData(); editor.setData(first);
      return [first, editor.getData()];
    }, input);
    const formatted = input.replace('+ __literal__', '* **literal**');
    assert.deepEqual(result, [formatted, formatted]);
  });
  await check('the CKEditor processor retains renderer dialect syntax and list semantics across repeated loads', async () => {
    const samples = [
      '+++\nname = "Demo"\n+++', '*[HTML]: Hyper Text Markup Language\n\nAn HTML note.',
      ['> [!TIP]\n> __Keep__', '> [!TIP]\n>\n> **Keep**'],
      ['::: center\n\n+ __Keep__\n\n:::', '::: center\n\n* **Keep**\n\n:::'], 'Heading {#id .wide}',
      [':::: outside\n\n::: inside\n\n+ __Keep__\n\n:::\n\n::::', ':::: outside\n\n::: inside\n\n* **Keep**\n\n:::\n\n::::'],
      'Term\n:   __Definition__',
      'A :smile: here.', '![Alt](photo.png "caption")', 'Note[^1].\n\n[^1]: __detail__', '::home::',
      '![Light](photo.png#light)', '![Alt =200x100](photo.png)', 'A ++term++ and ==mark==.',
      '{% pano360 scene.json %}', '@startuml\n\nAlice -> Bob: **msg**\n\n@enduml',
      '{% qrcode text="ABC" %}', '{\u6f22\u5b57|\u304b\u3093\u3058}', 'A !!spoiler!! here.', 'H~2~O', '2^10^',
      ['::: tabs#id\n\n@tab First\n\n+ __one__\n\n@tab Second\n\ntext\n\n:::',
        '::: tabs#id\n\n@tab First\n\n* **one**\n\n@tab Second\n\ntext\n\n:::'],
      ['+ __one__ _two_\n+ __three__ <em>four</em>', '* **one** _two_\n* **three** <em>four</em>'],
      '> $$\n> x^2\n> $$',
      ['> [l]: link.md\n>   "title"\n>\n> __next__', '> [l]: link.md\n>   "title"\n>\n> **next**'],
      ['+ one\n  ```js extra\n   code\n  ```\n+ __two__', '* one\n  ```js extra\n   code\n  ```\n* **two**'],
      ['+ [x] **task** <kbd>A</kbd>\n\n+ [ ] Loose', '* [x] **task** <kbd>A</kbd>\n\n* [ ] Loose'],
      ['> __Head__\n>\n> + __A__\n> + {% pano360 scene.json %}\n> + __B__\n>\n> __Tail__',
        '> **Head**\n>\n> * **A**\n> * {% pano360 scene.json %}\n> * **B**\n>\n> **Tail**'],
      ['+ __A__\n+ Term\n  :   definition\n+ __B__', '* **A**\n* Term\n  :   definition\n* **B**'],
      '```mermaid\nflowchart LR\n  A --> B\n```', '```unlisted\n  a < b\n\n```',
      'A [titled](file.md "Title") and ![photo][pic].\n\n[pic]: img.png "Caption"'
    ];
    const outputs = await page.evaluate(values => values.map(text => {
      const editor = amEditor.editor; editor.setData(text); const first = editor.getData();
      editor.setData(first); return [first, editor.getData()];
    }), samples.map(sample => Array.isArray(sample) ? sample[0] : sample));
    for (const [i, sample] of samples.entries()) {
      const expected = Array.isArray(sample) ? sample[1] : sample;
      assert.deepEqual(outputs[i], [expected, expected], expected);
    }
    assert.deepEqual(await page.evaluate(() => ['7) one\n8) two', '- one\n\n- two'].map(text => {
      amEditor.editor.setData(text); return amEditor.editor.getData();
    })), ['7. one\n8. two', '* one\n\n* two']);
  });
  await check('ordinary words between inline extensions remain editable', async () => {
    await page.evaluate(async () => {
      await newDoc('+ __Begin__ :smile: ordinary text :wave: <kbd>F2</kbd>\n');
      replaceModelText('ordinary text', 'changed');
      await amEditorCommand('preview', fixture.session);
    });
    assert.equal(await page.evaluate(() => fixture.text), '* **Begin** :smile: changed :wave: <kbd>F2</kbd>\n');
  });
  await check('enter writes canonical Markdown immediately and unchanged exit restores exact original', async () => {
    const input = 'Title\r\n=====\r\n\r\n+ __Bold__\r\n+ Next\r\n\r\n';
    const formatted = '# Title\r\n\r\n* **Bold**\r\n* Next\r\n';
    await page.evaluate(text => newDoc(text), input);
    assert.equal(await page.evaluate(() => fixture.text), formatted);
    await page.evaluate(() => amEditorCommand('preview', fixture.session));
    assert.equal(await page.evaluate(() => fixture.text), input);
    assert.equal(await page.evaluate(() => fixture.modified), false);
    await page.evaluate(async () => {
      await amEditorCommand('edit', fixture.session); replaceModelText('Bold', 'New'); await amEditor.flush();
      amEditor.editor.execute('undo'); await amEditor.flush(); await amEditorCommand('preview', fixture.session);
    });
    assert.equal(await page.evaluate(() => fixture.text), input);
    await page.evaluate(async () => {
      await amEditorCommand('edit', fixture.session); replaceModelText('Bold', 'New'); await amEditorCommand('preview', fixture.session);
    });
    assert.equal(await page.evaluate(() => fixture.text), formatted.replace('Bold', 'New'));
  });
  await check('a native edit made just before exit wins over the formatting checkpoint', async () => {
    await page.evaluate(async () => {
      await newDoc('+ __Keep__\n'); fixture.text = '+ Manually changed in Notepad++\n'; fixture.revision++;
      await amEditorCommand('preview', fixture.session);
    });
    assert.equal(await page.evaluate(() => fixture.text), '+ Manually changed in Notepad++\n');
  });
  await check('source line wraps can reflow across the editor width without changing Markdown', async () => {
    const text = Array.from({ length: 8 }, (_, i) => 'WrappedLine' + i + ' keeps its original source line and whitespace.').join('\r\n') + '\r\n';
    await page.evaluate(async text => { fixture.preserveLineBreaks = false; await newDoc(text); await amEditorCommand('edit', fixture.session); }, text);
    const layout = await page.locator('.ck-content p').evaluate(p => {
      const r = document.createRange(); r.selectNodeContents(p);
      const lines = new Map();
      for (const rect of r.getClientRects()) {
        const top = Math.round(rect.top), left = lines.get(top)?.left ?? rect.left;
        lines.set(top, { left: Math.min(left, rect.left), right: Math.max(rect.right, lines.get(top)?.right ?? rect.right) });
      }
      return { breaks: p.querySelectorAll('br').length, width: p.clientWidth,
        longestLine: Math.max(...[...lines.values()].map(line => line.right - line.left)) };
    });
    assert.equal(layout.breaks, 0);
    assert.ok(layout.longestLine > layout.width * .9, JSON.stringify(layout));
    await page.evaluate(async () => { replaceModelText('WrappedLine3', 'EditedLine3'); await amEditor.flush(); });
    assert.equal(await page.evaluate(() => fixture.text), text.replace('WrappedLine3', 'EditedLine3'));
    const writes = await page.evaluate(() => window.writes.length);
    for (const preserve of [true, false, true, false]) {
      await page.evaluate(async preserve => { fixture.preserveLineBreaks = preserve; await amEditor.receiveSnapshot({ ...fixture }); await amEditor.flush(); }, preserve);
      assert.equal(await page.locator('.ck-content p').evaluate(p => p.querySelectorAll('br').length), preserve ? 7 : 0);
      assert.equal(await page.evaluate(() => window.writes.length), writes);
      assert.equal(await page.evaluate(() => fixture.text), text.replace('WrappedLine3', 'EditedLine3'));
    }
  });
  await check('explicit Markdown breaks survive edits and both wrap settings', async () => {
    const text = 'Alpha  \r\nBeta\\\r\nGamma\r\nDelta\r\n';
    for (const preserve of [false, true]) {
      await page.evaluate(async ({ text, preserve }) => { fixture.preserveLineBreaks = preserve; await newDoc(text); await amEditorCommand('edit', fixture.session); }, { text, preserve });
      assert.equal(await page.locator('.ck-content p').evaluate(p => p.querySelectorAll('br').length), preserve ? 3 : 2);
      await page.evaluate(async () => { replaceModelText('Gamma', 'EditedGamma'); await amEditor.flush(); });
      assert.equal(await page.evaluate(() => fixture.text), text.replace('Alpha  ', 'Alpha\\').replace('Gamma', 'EditedGamma'));
    }
  });
  await check('Shift+Enter saves a standard GFM break that survives reloading with reflow enabled', async () => {
    await page.evaluate(async () => {
      fixture.preserveLineBreaks = false; await newDoc('LeftRight\n');
      const model = amEditor.editor.model, paragraph = model.document.getRoot().getChild(0);
      model.change(writer => writer.insertElement('softBreak', paragraph, 4));
      await amEditor.flush();
    });
    assert.equal(await page.evaluate(() => fixture.text), 'Left\\\nRight\n');
    await page.evaluate(async () => { fixture.revision++; await newDoc(fixture.text); });
    assert.equal(await page.locator('.ck-content p').evaluate(p => p.querySelectorAll('br').length), 1);
    assert.equal(await page.evaluate(() => AnotherMarkdownEditor.hasHtml(fixture.text)), false);
  });
  await check('hard breaks retain inline formatting, literal marker text and fenced code', async () => {
    const text = 'AMGFMHARDBREAK0TOKEN **bold  \nmore** and [link\\\nlabel](https://example.test).\n\n```md\none\n<br>\ntwo\n```\n';
    for (const preserve of [false, true]) {
      await page.evaluate(async ({ text, preserve }) => {
        fixture.preserveLineBreaks = preserve; await newDoc(text);
        replaceModelText('bold', 'BOLD'); await amEditor.flush();
      }, { text, preserve });
      assert.equal(await page.evaluate(() => fixture.text), text.replace('bold  ', 'BOLD\\'));
      assert.equal(await page.evaluate(() => AnotherMarkdownEditor.hasHtml(fixture.text)), false);
    }
  });
  await check('changing wrap display retains pending visual edits', async () => {
    const text = 'First wrapped line.\r\nSecond target line.\r\n';
    await page.evaluate(async text => {
      fixture.preserveLineBreaks = false; await newDoc(text);
      replaceModelText('target', 'changed'); fixture.preserveLineBreaks = true;
      await amEditor.receiveSnapshot({ ...fixture }); await amEditor.flush();
    }, text);
    assert.equal(await page.evaluate(() => fixture.text), text.replace('target', 'changed'));
    await page.evaluate(async () => { fixture.preserveLineBreaks = false; await amEditor.receiveSnapshot({ ...fixture }); });
  });
  await check('Cyrillic edits and undo operate against the formatted entry text', async () => {
    const word = '\u0412\u0435\u0440\u0431\u043b\u044e\u0436\u044c\u044f\u041d\u043e\u0442\u0430\u0446\u0438\u044f';
    const source = '## Core Principles\r\n\r\n- Prefer existing project code.\r\n- **Codebase conventions first:** follow the edited module; ' + word + ' identifiers.\r\n- Current source outranks stale summaries.\r\n\r\n## Active model adaptation\r\n';
    await page.evaluate(async ({ source, word }) => { await newDoc(source); replaceModelText(word, word.slice(0, 9) + ' ' + word.slice(9)); await amEditor.flush(); }, { source, word });
    const formatted = source.replace(/^- /gm, '* ');
    assert.equal(await page.evaluate(() => fixture.text), formatted.replace(word, word.slice(0, 9) + ' ' + word.slice(9)));
    await page.evaluate(async ({ word }) => { replaceModelText(word.slice(0, 9) + ' ' + word.slice(9), word); await amEditor.flush(); }, { word });
    assert.equal(await page.evaluate(() => fixture.text), formatted);
  });
  await check('edits serialize whole Markdown blocks in the same form as direct CKEditor output', async () => {
    const samples = [
      '-   TargetWord\r\n-   Keep **bold** and _italic_.\r\n\r\n',
      '+ TargetWord\n+ Keep text.\n\n',
      '7) TargetWord\n8) Keep text.\n\n',
      '- Parent\n  - TargetWord\n  - Keep nested.\n- Last\n',
      '> TargetWord\n>\n> Keep __bold__.\n\n',
      '|  First     | Second |\n| :--------  | -----: |\n| TargetWord | Keep   |\n\n',
      'A __TargetWord__ and *other* text.  \r\nA soft line.\r\n\r\n',
      'TargetWord\n==========\n\n',
      '```js\nconst value = "TargetWord";\n```\n\n',
      'A [TargetWord][target] and `TargetWordElsewhere`.\n\n[target]: https://example.org "Title"\n',
      'Text \ud83d\ude00 TargetWord without a final newline'
    ];
    for (const source of samples) {
      await page.evaluate(async source => { await newDoc(source); replaceModelText('TargetWord', 'Target Word'); await amEditor.flush(); }, source);
      const result = await page.evaluate(() => [fixture.text, amEditor.editor.getData()]);
      assert.ok(result[0].includes('Target Word'), source);
      assert.equal(result[0], result[1] + (source.endsWith('\r\n') ? '\r\n' : source.endsWith('\n') ? '\n' : ''), source);
    }
  });
  await check('visual edit keeps the normalized table and list from entry', async () => {
    await page.evaluate(text => newDoc(text), original);
    const atEntry = await page.evaluate(() => fixture.text);
    await page.evaluate(() => editFirstParagraph('\u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00 '));
    await page.evaluate(() => amEditor.flush());
    const text = await page.evaluate(() => fixture.text);
    assert.ok(text.includes('\u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00'));
    assert.ok(text.endsWith(atEntry.slice(atEntry.indexOf('* one'))));
    assert.equal(await page.evaluate(() => AnotherMarkdownEditor.hasHtml(fixture.text)), false);
  });
  await check('source edit updates WYSIWYG', async () => {
    await page.evaluate(async () => { fixture.text = '# Native change\n\nFrom source.\n'; fixture.revision++; await amEditor.receiveSnapshot({ ...fixture }); });
    assert.equal(await page.locator('.ck-content h1').textContent(), 'Native change');
  });
  await check('loading never enters Undo; visual edits undo and redo safely', async () => {
    assert.equal(await page.evaluate(() => amEditor.editor.commands.get('undo').isEnabled), false);
    await page.evaluate(async () => { editFirstParagraph('Undo '); await amEditor.flush(); amEditor.editor.execute('undo'); await amEditor.flush(); });
    assert.equal(await page.evaluate(() => fixture.text), '# Native change\n\nFrom source.\n');
    assert.equal(await page.evaluate(() => amEditor.editor.commands.get('undo').isEnabled), false);
    await page.evaluate(async () => { amEditor.editor.execute('redo'); await amEditor.flush(); });
    assert.ok((await page.evaluate(() => fixture.text)).includes('Undo From source.'));
  });
  await check('GFM table editing generates a pipe table', async () => {
    await page.evaluate(async () => {
      await newDoc('| A | B |\n| :--- | ---: |\n| one | two |\n');
      const model = amEditor.editor.model, table = model.document.getRoot().getChild(0);
      model.change(writer => writer.insertText('changed ', table.getChild(1).getChild(0).getChild(0), 0));
      await amEditor.flush();
    });
    const text = await page.evaluate(() => fixture.text);
    assert.ok(text.includes('changed one')); assert.ok(text.includes('|')); assert.equal(await page.evaluate(() => AnotherMarkdownEditor.hasHtml(fixture.text)), false);
    assert.match(text.split('\n')[1], /:\s*-+.*-+:/, 'GFM alignment survives a cell edit');
  });
  await check('opaque frontmatter/HTML/extensions survive adjacent edits', async () => {
    const text = '---\ntitle: Keep\n---\n\nOrdinary paragraph.\n\n<div class="special">Keep</div>\n\nMath $x^2$.\n';
    await page.evaluate(text => newDoc(text), text);
    await page.evaluate(() => editFirstParagraph('Edited ')); await page.evaluate(() => amEditor.flush());
    const result = await page.evaluate(() => fixture.text);
    assert.ok(result.startsWith('---\ntitle: Keep\n---\n')); assert.ok(result.endsWith(text.slice(text.indexOf('<div'))));
  });
  await check('independent concurrent source changes are merged', async () => {
    await page.evaluate(async () => {
      await newDoc('First.\n\nSecond.\n\nThird.\n');
      editFirstParagraph('Visual ');
      fixture.text = 'First.\n\nSecond.\n\nSource third.\n'; fixture.revision++;
      await amEditor.receiveSnapshot({ ...fixture }); await amEditor.flush();
    });
    assert.equal(await page.evaluate(() => fixture.text), 'Visual First.\n\nSecond.\n\nSource third.\n');
  });
  await check('concurrent edits in different items of one list do not conflict', async () => {
    const source = '- First item.\r\n- Middle item.\r\n- Last item.\r\n';
    await page.evaluate(async source => {
      await newDoc(source); replaceModelText('First', 'Visual first');
      fixture.text = fixture.text.replace('Last', 'Source last'); fixture.revision++;
      await amEditor.receiveSnapshot({ ...fixture }); await amEditor.flush();
    }, source);
    assert.equal(await page.evaluate(() => !!amEditor.state.conflict), false);
    assert.equal(await page.evaluate(() => fixture.text), source.replace(/^- /gm, '* ').replace('First', 'Visual first').replace('Last', 'Source last'));
  });
  await check('task checkbox changes preserve list markers and spacing', async () => {
    const source = '- [ ] First task\r\n- [ ] Second task\r\n';
    await page.evaluate(async source => {
      await newDoc(source);
      const model = amEditor.editor.model;
      model.change(writer => writer.setAttribute('todoListChecked', true, model.document.getRoot().getChild(0)));
      await amEditor.flush();
    }, source);
    assert.equal(await page.evaluate(() => fixture.text), source.replace(/^- /gm, '* ').replace('[ ] First', '[x] First'));
  });
  await check('overlapping edits stop automatic writes', async () => {
    await page.evaluate(async () => {
      await newDoc('Same paragraph.\n'); editFirstParagraph('Visual ');
      fixture.text = 'Source paragraph.\n'; fixture.revision++; await amEditor.receiveSnapshot({ ...fixture });
    });
    assert.equal(await page.evaluate(() => amEditor.flush()), false);
    assert.equal(await page.evaluate(() => fixture.text), 'Source paragraph.\n');
    assert.ok(await page.evaluate(() => !!amEditor.state.conflict));
    await page.locator('.am-editor-actions button').filter({ hasText: /\u0420\u0430\u0437\u043e\u0431\u0440\u0430\u0442\u044c/ }).click();
    await page.locator('dialog button').filter({ hasText: /^\u0418\u0437 \u0440\u0435\u0434\u0430\u043a\u0442\u043e\u0440\u0430$/ }).click();
    await page.locator('dialog button').filter({ hasText: /^\u041f\u0440\u0438\u043c\u0435\u043d\u0438\u0442\u044c/ }).click();
    await page.evaluate(() => amEditor.flush()); assert.equal(await page.evaluate(() => fixture.text), 'Visual Same paragraph.\n');
  });
  await check('translated editor uses Save As only', async () => {
    await page.evaluate(async () => { fixture = { ...fixture, session: 'translation', translated: true, text: 'Translated.\n', revision: fixture.revision + 1 }; await amEditor.receiveSnapshot({ ...fixture }); editFirstParagraph('Edited '); await amEditor.flush(); await amEditor.save(); });
    assert.equal(await page.evaluate(() => saves.at(-1).action), 'saveAs');
  });
  await check('typing during an in-flight apply is not lost', async () => {
    await page.evaluate(async () => { await newDoc('First.\n'); hold = true; editFirstParagraph('One '); window.pending = amEditor.flush(); editFirstParagraph('Two '); hold = false; held(); await pending; await amEditor.flush(); });
    assert.equal(await page.evaluate(() => fixture.text), 'Two One First.\n');
  });
  await check('late writes never reach another tab and a draft resumes on return', async () => {
    await page.evaluate(async () => {
      await newDoc('Original tab.\n'); window.firstTab = { ...fixture }; hold = true;
      editFirstParagraph('Draft '); window.pending = amEditor.flush();
      await newDoc('Other tab.\n', false); hold = false; held(); await pending;
    });
    assert.equal(await page.evaluate(() => fixture.text), 'Other tab.\n');
    await page.evaluate(async () => { fixture = firstTab; await amEditor.receiveSnapshot({ ...fixture }); await amEditor.flush(); });
    assert.equal(await page.evaluate(() => fixture.text), 'Draft Original tab.\n');
    assert.equal(await page.evaluate(() => !!amEditor.state.error), false);
  });
  await check('one Save includes edits made while apply is in flight', async () => {
    const before = await page.evaluate(() => saves.length);
    await page.evaluate(async () => {
      await newDoc('Save once.\n'); hold = true; editFirstParagraph('One '); window.saving = amEditor.save();
      editFirstParagraph('Two '); hold = false; held(); await saving;
    });
    assert.equal(await page.evaluate(() => saves.length), before + 1);
    assert.equal(await page.evaluate(() => saves.at(-1).content), 'Two One Save once.\n');
  });
  await check('reference links and task lists remain valid GFM', async () => {
    await page.evaluate(async () => { await newDoc('A [link][ref].\n\n- [ ] Task\n\n[ref]: https://example.org\n'); editFirstParagraph('Edited '); await amEditor.flush(); });
    const text = await page.evaluate(() => fixture.text);
    assert.ok(text.includes('[link][ref]')); assert.ok(text.includes('* [ ] Task')); assert.ok(text.endsWith('[ref]: https://example.org\n'));
  });
  await check('read-only source disables visual editing and saving', async () => {
    await page.evaluate(async () => { fixture.readOnly = true; fixture.revision++; await amEditor.receiveSnapshot({ ...fixture }); });
    assert.equal(await page.evaluate(() => amEditor.editor.isReadOnly), true);
    assert.equal(await page.locator('.am-primary').isDisabled(), true);
  });
  await check('keyboard and formatting toolbar save ordinary Markdown', async () => {
    await page.evaluate(() => newDoc('Keyboard.\n'));
    await page.evaluate(() => amEditorCommand('edit', fixture.session));
    await page.locator('.ck-editor__editable').click(); await page.keyboard.press('Control+End');
    await page.keyboard.type(' typed'); await page.keyboard.press('Control+Shift+ArrowLeft');
    await page.locator('.ck-toolbar button[data-cke-tooltip-text^="\u0416\u0438\u0440\u043d\u044b\u0439"]').first().click();
    await page.evaluate(() => amEditor.flush());
    assert.match(await page.evaluate(() => fixture.text), /\*\*typed\*\*/);
  });
  await check('light and dark text, headings and table cells stay readable', async () => {
    await page.evaluate(() => newDoc('# Heading\n\nParagraph.\n\n| A | B |\n| --- | --- |\n| One | Two |\n'));
    for (const dark of [true, false]) {
      await page.evaluate(async dark => { fixture.dark = dark; fixture.revision++; await amEditor.receiveSnapshot({ ...fixture }); await amEditorCommand('edit', fixture.session); }, dark);
      const colors = await page.evaluate(() => Object.fromEntries(['.ck-content', '.ck-content h1', '.ck-content td'].map(selector => {
        const s = getComputedStyle(document.querySelector(selector)); return [selector, [s.color, s.backgroundColor]];
      })));
      assert.equal(colors['.ck-content'][0], dark ? 'rgb(207, 207, 207)' : 'rgb(51, 51, 51)');
      assert.equal(colors['.ck-content'][1], dark ? 'rgb(30, 30, 30)' : 'rgb(255, 255, 255)');
      assert.equal(colors['.ck-content h1'][0], dark ? 'rgb(255, 255, 255)' : 'rgb(17, 17, 17)');
      assert.equal(colors['.ck-content td'][0], colors['.ck-content'][0]);
    }
  });
  await check('uploaded images wait for a durable URL before source sync', async () => {
    await page.evaluate(async () => {
      await newDoc('Image.\n');
      const data = Uint8Array.from(atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jzXkAAAAASUVORK5CYII='), c => c.charCodeAt(0));
      amEditor.editor.execute('uploadImage', { file: new File([data], 'image.png', { type: 'image/png' }) });
    });
    await page.waitForFunction(() => !amEditor.editor.plugins.get('FileRepository').loaders.length && amEditor.source.serialize().includes('test.assets/pasted.png'));
    await page.evaluate(() => amEditor.flush());
    assert.ok((await page.evaluate(() => fixture.text)).includes('test.assets/pasted.png'));
    assert.equal(await page.evaluate(() => writes.some(w => /blob:/.test(w.content))), false);
  });
  await page.screenshot({ path: path.join(output, 'visual-editor.png'), fullPage: true });
  assert.deepEqual(errors, []);
  await browser.close(); console.log('PASS visual editor: ' + checks + ' scenarios');
}
main().catch(error => { console.error(error); process.exit(1); });
