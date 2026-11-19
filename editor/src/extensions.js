import MarkdownIt from 'markdown-it';
import { abbr } from '@mdit/plugin-abbr';
import { alert } from '@mdit/plugin-alert';
import { align } from '@mdit/plugin-align';
import { attrs } from '@mdit/plugin-attrs';
import { container } from '@mdit/plugin-container';
import { dl } from '@mdit/plugin-dl';
import { embed } from '@mdit/plugin-embed';
import { icon } from '@mdit/plugin-icon';
import { imgMark } from '@mdit/plugin-img-mark';
import { imgSize, legacyImgSize, obsidianImgSize } from '@mdit/plugin-img-size';
import { ins } from '@mdit/plugin-ins';
import { mark } from '@mdit/plugin-mark';
import { ruby } from '@mdit/plugin-ruby';
import { spoiler } from '@mdit/plugin-spoiler';
import { sub } from '@mdit/plugin-sub';
import { sup } from '@mdit/plugin-sup';
import { tab } from '@mdit/plugin-tab';
import { uml } from '@mdit/plugin-uml';
import { full as emoji } from 'markdown-it-emoji';

// Ask the preview's syntax plugins which blocks carry extra meaning. Rendering
// hooks (highlighting, generated images and external services) are never run.
const common = new MarkdownIt({ html: true });
const dialect = new MarkdownIt({ html: true });
for (const plugin of [abbr, alert, align, attrs, dl, icon, imgMark, imgSize, legacyImgSize, obsidianImgSize, ins, mark, ruby, spoiler, sub, sup, tab, emoji])
  dialect.use(plugin);
dialect.use(container, { name: 'markdown', validate: name => !!name.trim() });
dialect.use(uml, { name: 'plantuml', open: '@startuml', close: '@enduml', render: () => '' });
dialect.use(embed, { config: ['pano360', 'qrcode'].map(name => ({ name, allowInline: name === 'qrcode', setup: () => '' })) });

// The renderer identifies the directive lines. Give those exact lines to
// remark as leaf blocks so its usual grammar still parses their entire body.
export function remarkDirectiveLines() {
  const parse = this.parser, data = this.data();
  let markers;
  this.parser = (source, file) => {
    markers = directiveMarkers(source);
    try { return parse(source, file); }
    finally { markers = undefined; }
  };
  const construct = { name: 'amDirective', tokenize(effects, ok, nok) {
    const marker = markers?.get(this.now().line);
    return code => {
      if (code !== marker) return nok(code);
      effects.enter('amDirective');
      return consume(code);
    };
    function consume(code) {
      if (code === null || code < -2) { effects.exit('amDirective'); return ok(code); }
      effects.consume(code); return consume;
    }
  } };
  (data.micromarkExtensions ??= []).push({ flow: { 58: construct, 64: construct, 91: construct } });
  (data.fromMarkdownExtensions ??= []).push({
    enter: { amDirective(token) { this.enter({ type: 'amDirective' }, token); } },
    exit: { amDirective(token) { this.exit(token); },
      blockQuotePrefix: recordPrefix, listItemPrefix: recordPrefix, listItemIndent: recordPrefix }
  });
}

// Markdown writers add list indentation and quote markers themselves. Remember
// their actual source boundaries so a multiline literal owns just its payload.
function recordPrefix(token) {
  const data = this.stack[0].data ??= {}, prefixes = data.containerPrefixes ??= [];
  (prefixes[token.start.line] ??= []).push(token.end.offset);
}

function directiveMarkers(source) {
  if (!source.includes(':::') && !source.includes('[!')) return;
  const markers = new Map(), lines = source.split(/\r\n|\r|\n/);
  for (const token of dialect.parse(source, {})) {
    if (!token.map) continue;
    const [start, end] = token.map;
    if (token.type === 'alert_title') markers.set(start + 1, 91);
    else if (token.type === 'tabs_tab_open') markers.set(start + 1, 64);
    else if (token.nesting === 1 && /^:{3,}$/.test(token.markup)) {
      markers.set(start + 1, 58);
      // Tab groups omit trailing blank lines from their token map.
      let close = end;
      while (close < lines.length && /^[\s>]*$/.test(lines[close])) close++;
      const fence = /^[\s>]*(:{3,})[ \t]*$/.exec(lines[close] || '');
      if (fence && fence[1].length >= token.markup.length) markers.set(close + 1, 58);
    }
  }
  return markers;
}

const describe = token => [token.type, token.tag, token.attrs, token.content, token.info, token.children?.map(describe)];
const mayExtend = value => /[!:{@|*+~=^]/.test(value);

export function inlineLiterals(value) {
  if (!mayExtend(value)) return;
  const tokens = dialect.parse(value, {});
  if (JSON.stringify(tokens.map(describe)) === JSON.stringify(common.parse(value, {}).map(describe))) return;
  const children = tokens.find(token => token.type === 'inline')?.children || [];
  const ranges = [], end = value.trimEnd().length;
  let cursor = value.length - value.trimStart().length;
  for (let i = 0; i < children.length; i++) {
    const token = children[i], content = token.content;
    if (token.type !== 'text' || token.level !== 0 || !content) continue;
    let at = value.indexOf(content, cursor);
    if (i === children.length - 1 && value.endsWith(content, end)) at = end - content.length;
    else if (at !== cursor && value.indexOf(content, at + 1) !== -1) continue;
    if (at < cursor) continue;
    if (at > cursor) ranges.push({ start: cursor, end: at });
    cursor = at + content.length;
  }
  if (cursor < end) ranges.push({ start: cursor, end });
  return ranges.length ? ranges : undefined;
}

function blockTokens(parser, source) {
  const result = new Map(), parents = [];
  for (const token of parser.parse(source, {})) {
    if (token.nesting < 0) { parents.pop(); continue; }
    const span = token.map || parents.at(-1);
    if (token.nesting > 0) parents.push(span);
    if (!span || span[1] <= span[0]) continue;
    // A difference in one definition or embed must belong to that leaf, even
    // when it is nested several levels deep in a list, quote or table.
    if (/^(?:bullet_list|ordered_list|list_item|blockquote|table|thead|tbody|tr|th|td)_open$/.test(token.type)) continue;
    const key = span.join(':');
    if (!result.has(key)) result.set(key, { from: span[0], to: span[1], description: [] });
    result.get(key).description.push(describe(token));
  }
  for (const block of result.values()) {
    block.shape = block.description.map(token => token[0]).join(' ');
    block.tokens = JSON.stringify(block.description);
  }
  return result;
}

export function extensionRanges(text, root) {
  const parts = [];
  let cursor = 0;
  function mask(node) {
    if (['inlineMath', 'math', 'code', 'inlineCode', 'yaml', 'toml', 'html', 'amDirective'].includes(node.type)) {
      const { start, end } = node.position;
      parts.push(text.slice(cursor, start.offset), text.slice(start.offset, end.offset).replace(/[^\r\n]+/g, ' '));
      cursor = end.offset;
    } else node.children?.forEach(mask);
  }
  mask(root);
  const input = parts.length ? parts.join('') + text.slice(cursor) : text;
  if (!mayExtend(input)) return [];
  const base = blockTokens(common, input), extra = blockTokens(dialect, input), changed = [];
  for (const [key, block] of base) if (!extra.has(key)) changed.push(block);
  for (const [key, block] of extra) {
    const before = base.get(key);
    if (before?.tokens !== block.tokens) changed.push({ ...block, inline: before?.shape === block.shape });
  }
  const ranges = [];
  for (const { from, to, inline } of changed.sort((a, b) => a.from - b.from)) {
    const last = ranges.at(-1);
    if (last && from < last.to) { last.to = Math.max(last.to, to); last.inline &&= inline; }
    else ranges.push({ from, to, inline });
  }
  return ranges;
}
