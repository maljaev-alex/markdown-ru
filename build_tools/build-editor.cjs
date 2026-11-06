// Dependencies live outside the checkout; package-lock.json pins the build.
const fs = require('node:fs');
const path = require('node:path');
const { createRequire } = require('node:module');
const root = path.resolve(__dirname, '..');
const dependencies = path.resolve(process.argv[2] || 'D:/Temp/agent/markdown-ru-editor-deps');
const output = path.resolve(process.argv[3] || path.join(root, 'assets/editor'));
const resolveDependency = createRequire(path.join(dependencies, 'package.json'));
const esbuild = resolveDependency('esbuild');
esbuild.build({
  entryPoints: [path.join(root, 'editor/src/index.js')],
  bundle: true, format: 'iife', globalName: 'AnotherMarkdownEditor', metafile: true,
  target: ['chrome110'], outfile: path.join(output, 'editor.js'),
  nodePaths: [path.join(dependencies, 'node_modules')],
  minify: true, legalComments: 'linked', charset: 'utf8',
  loader: { '.svg': 'text', '.woff2': 'dataurl' },
  define: { 'process.env.NODE_ENV': '"production"' },
  logLevel: 'info'
}).then(result => {
  fs.copyFileSync(path.join(dependencies, 'node_modules/ckeditor5/COPYING.GPL'), path.join(output, 'CKEditor-COPYING.txt'));
  fs.copyFileSync(path.join(dependencies, 'node_modules/ckeditor5/LICENSE.md'), path.join(output, 'CKEditor-LICENSE.md'));
  const packages = new Map();
  for (const emitted of Object.values(result.metafile.outputs)) {
    for (const [input, usage] of Object.entries(emitted.inputs)) {
      if (!usage.bytesInOutput) continue;
      let directory = path.dirname(path.resolve(input));
      while (directory !== path.dirname(directory)) {
        const manifest = path.join(directory, 'package.json');
        if (fs.existsSync(manifest)) {
          const info = JSON.parse(fs.readFileSync(manifest, 'utf8'));
          if (info.name) { if (directory.includes('node_modules')) packages.set(directory, info); break; }
        }
        directory = path.dirname(directory);
      }
    }
  }
  const notices = [];
  for (const [directory, info] of [...packages].sort((a, b) => a[1].name.localeCompare(b[1].name))) {
    notices.push(`${info.name} ${info.version}\n${'='.repeat(64)}`);
    const files = fs.readdirSync(directory).filter(name => /^(licen[cs]e|copying|notice)(\.|$)/i.test(name) && fs.statSync(path.join(directory, name)).isFile());
    for (const file of files) notices.push(file + '\n' + fs.readFileSync(path.join(directory, file), 'utf8'));
    if (!files.length) notices.push('License: ' + (info.license || 'See upstream package source') + '\nSource: ' + JSON.stringify(info.repository || info.homepage || info.name));
  }
  fs.writeFileSync(path.join(output, 'THIRD-PARTY-LICENSES.txt'), notices.join('\n\n') + '\n');
}).catch(error => { console.error(error); process.exitCode = 1; });
