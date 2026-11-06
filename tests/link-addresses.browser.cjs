const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createRequire } = require('node:module');
const deps = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const root = path.resolve(__dirname, '..');
const assets = path.resolve(process.argv[4] || path.join(root, 'assets'));
const requireDeps = createRequire(path.join(deps, 'package.json'));
const { chromium } = requireDeps('playwright-core');
async function main() {
  fs.mkdirSync(output, { recursive: true }); process.env.TEMP = process.env.TMP = output;
  const browser = await chromium.launch({ channel: process.env.AM_BROWSER_CHANNEL || 'chrome', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1150, height: 800 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.route('http://links.test/**', async route => {
      const name = new URL(route.request().url()).pathname;
      const files = { '/loader.js': path.join(assets, 'loader.js'), '/editor.js': path.join(assets, 'editor/editor.js'),
        '/editor.css': path.join(assets, 'editor/editor.css') };
      if (files[name]) await route.fulfill({ body: fs.readFileSync(files[name]), contentType: name.endsWith('.css') ? 'text/css' : 'text/javascript' });
      else await route.fulfill({ contentType: 'text/html', body: '<!doctype html><html><head><meta charset="utf-8"><base href="http://local.example/diskD/notes/"><link rel="stylesheet" href="http://links.test/editor.css"></head><body><div id="content"></div><script src="http://links.test/loader.js"></script><script src="http://links.test/editor.js"></script></body></html>' });
    });
    await page.goto('http://links.test/');
    const moduleName = String.fromCharCode(1054,1073,1097,1077,1075,1086,1053,1072,1079,1085,1072,1095,1077,1085,1080,1103,1059,1058);
    const target = 'D:/Projects/example/base_files/CommonModules/' + moduleName + '/Ext/Module.bsl:6531';
    const fixtures = [
      { label: 'Drive path', href: target, expected: target },
      { label: 'Encoded file URL', href: 'file:///' + encodeURI(target), expected: target },
      { label: 'Relative basename with line', href: 'Module.bsl:6531', expected: 'D:/notes/Module.bsl:6531' },
      { label: 'Relative path', href: '../source/module.bsl:42', expected: 'D:/source/module.bsl:42' },
      { label: 'Virtual path', href: 'http://local.example/diskD/Folder/a%20b%23c%25.txt#L9', expected: 'D:/Folder/a b#c%.txt#L9' },
      { label: 'External URL', href: 'https://example.test/a%20b?q=%D0%B0#L9', expected: 'https://example.test/a%20b?q=%D0%B0#L9' },
      { label: 'UNC file', href: 'file://server/share/a%20b.txt', expected: '//server/share/a b.txt' },
      { label: 'Malformed escape', href: 'file:///D:/notes/bad%ZZ.txt', expected: 'D:/notes/bad%ZZ.txt' }
    ];
    await page.evaluate(async fixtures => {
      window.fixture = { session: 'links', path: 'D:/notes/test.md', revision: 1, showLinkAddresses: true,
        text: '| Target |\n| --- |\n' + fixtures.map(f => '| [**' + f.label + '**](<' + f.href + '>) |').join('\n') };
      window.writes = []; window.openedLinks = [];
      window.createLegacyView = async () => {
        const setOptions = async () => {
          const container = document.getElementById('content'); container.replaceChildren();
          for (const f of fixtures) { const p = document.createElement('p'), a = document.createElement('a'); a.href = f.href; a.textContent = f.label; p.append(a); container.append(p); }
          window.amSetShowLinkAddresses(fixture.showLinkAddresses);
        };
        await setOptions(); return { setOptions, dispose() {} };
      };
      window.chrome.webview = { addEventListener() {}, removeEventListener() {}, postMessage(message) { if (message.action === 'apply') writes.push(message); if (message.type === 'am.openLink') openedLinks.push(message.href); } };
      window.testView = await AnotherMarkdownEditor.create({ editorSnapshot: fixture });
    }, fixtures);
    let checks = 0;
    for (const editing of [false, true]) {
      await page.evaluate(async editing => { await amEditorCommand(editing ? 'edit' : 'preview', fixture.session); }, editing);
      for (const fixture of fixtures) {
        const link = page.locator(editing ? '.ck-content a' : '#content a').filter({ hasText: fixture.label });
        const hrefBefore = await link.getAttribute('href');
        assert.notEqual(hrefBefore, '#', 'recognized local files must remain clickable'); checks++;
        const selectionBefore = await page.evaluate(() => JSON.stringify([...amEditor.editor.model.document.selection.getRanges()].map(r => [r.start.path, r.end.path])));
        await link.hover();
        const actual = await page.locator('#am-link-address').textContent();
        console.log('PASS ' + (editing ? 'editing: ' : 'preview: ') + fixture.label);
        assert.equal(actual, fixture.expected, fixture.label + ' in ' + (editing ? 'editing' : 'preview')); checks++;
        assert.equal(await link.getAttribute('href'), hrefBefore, 'display does not rewrite navigation or weaken CKEditor sanitization'); checks++;
        assert.equal(await page.evaluate(() => JSON.stringify([...amEditor.editor.model.document.selection.getRanges()].map(r => [r.start.path, r.end.path]))), selectionBefore, 'hover does not move the source selection'); checks++;
        await page.evaluate(() => amSetShowLinkAddresses(false));
        assert.equal(await page.locator('#am-link-address').isVisible(), false); checks++;
        if (fixture.label !== 'External URL') {
          const count = await page.evaluate(() => openedLinks.length);
          await link.click(editing ? { modifiers: ['Control'] } : {});
          assert.equal(await page.evaluate(() => openedLinks.length), count + 1, 'file opens through the native bridge with addresses hidden'); checks++;
        }
        await page.evaluate(() => amSetShowLinkAddresses(true));
        assert.equal(await page.locator('#am-link-address').isVisible(), true); checks++;
        if (editing) {
          await link.click();
          const balloon = page.locator('.ck-link-toolbar a'); await balloon.waitFor();
          assert.notEqual(await balloon.getAttribute('href'), '#', 'balloon must point to the real target'); checks++;
          await balloon.hover();
          assert.equal(await page.locator('#am-link-address').textContent(), fixture.expected, 'balloon hover must show the same address'); checks++;
          if (fixture.label !== 'External URL') {
            const count = await page.evaluate(() => openedLinks.length);
            await balloon.click();
            assert.equal(await page.evaluate(() => openedLinks.length), count + 1, 'balloon sends the actual file to the host'); checks++;
          }
          await page.keyboard.press('Escape');
          await page.evaluate(() => amEditor.editor.model.change(writer => writer.setSelection(amEditor.editor.model.document.getRoot(), 'end')));
          await page.waitForFunction(() => !document.querySelector('.ck-link-toolbar__preview')?.getClientRects().length);
        }
      }
    }
    const count = await page.evaluate(() => openedLinks.length);
    await page.locator('.ck-content a').filter({ hasText: 'Drive path' }).click();
    await page.keyboard.press('Alt+Enter');
    assert.equal(await page.evaluate(() => openedLinks.length), count + 1, 'Alt+Enter opens a local link'); checks++;
    await page.keyboard.press('Escape');
    assert.deepEqual(await page.evaluate(() => writes), []); checks++;
    assert.equal(await page.evaluate(() => amEditor.source.serialize()), await page.evaluate(() => fixture.text)); checks++;
    await page.evaluate(async () => {
      fixture = { ...fixture, session: 'unsafe', revision: 2,
        text: '[Script](<javascript:alert(1)>)\n\n[Data](<data:text/html,test>)\n\n[Drive-relative](<d:relative.bsl>)' };
      await amEditor.receiveSnapshot(fixture); await amEditorCommand('edit', fixture.session);
    });
    for (const label of ['Script', 'Data', 'Drive-relative']) {
      const link = page.locator('.ck-content a').filter({ hasText: label });
      assert.equal(await link.getAttribute('href'), '#', 'unsafe or ambiguous schemes remain blocked: ' + label); checks++;
      await link.click();
      assert.equal(await page.locator('.ck-link-toolbar a').getAttribute('href'), '#', 'balloon also blocks: ' + label); checks++;
      await page.keyboard.press('Escape');
    }
    assert.deepEqual(errors, []); checks++;
    console.log('PASS link addresses: ' + checks + ' assertions');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
