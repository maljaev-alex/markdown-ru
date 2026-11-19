import { Plugin, Markdown, GeneralHtmlSupport, MarkdownGfmMdToHtml, MarkdownGfmMdToHtmlDefaultPlugins } from 'ckeditor5';
import { unified } from 'unified';
import remarkFrontmatter from 'remark-frontmatter';
import remarkMath from 'remark-math';
import remarkGfm from 'remark-gfm';
import remarkStringify from 'remark-stringify';
import { fromDom } from 'hast-util-from-dom';
import { toMdast, defaultHandlers } from 'hast-util-to-mdast';
import { OpaqueMarkdown } from './opaque.js';
import { extensionRanges, inlineLiterals, remarkDirectiveLines } from './extensions.js';

const properties = (node, values) => {
  node.data ??= {};
  Object.assign(node.data.hProperties ??= {}, values);
};
const containsHtml = node => node.type === 'html' || !!node.children?.some(containsHtml);
const verbatimBlock = node => ['yaml', 'toml', 'math', 'definition', 'footnoteDefinition', 'html', 'amDirective'].includes(node.type) ||
  (node.type === 'code' && !!node.meta);
const protectedNode = (value, inline = false) => ({
  type: 'amSource', data: { hName: inline ? 'am-inline-source' : 'am-raw', hProperties: { 'data-md': JSON.stringify(value) } }
});

// remark supplies the actual source span, including delimiters and original line endings.
function retainSource() {
  return (root, file) => {
    const text = String(file);
    function raw(node, depth) {
      const { start, end } = node.position, value = text.slice(start.offset, end.offset);
      if (!depth || start.line === end.line) return value;
      let line = start.line;
      return value.replace(/(\r\n|\r|\n)([^\r\n]*)/g, (_, eol, content, offset) => {
        const prefixes = root.data?.containerPrefixes?.[++line];
        if (!prefixes?.length) return eol + content;
        const afterPrefix = prefixes[Math.min(depth, prefixes.length) - 1];
        return eol + content.slice(Math.max(0, afterPrefix - (start.offset + offset + eol.length)));
      });
    }
    const textNodes = value => value.split(/\r\n|\r|\n/).flatMap((line, i) => [
      ...(i ? [{ type: 'amWrap', data: { hName: 'am-wrap' } }] : []),
      ...(line ? [{ type: 'text', value: line }] : [])
    ]);
    const ranges = extensionRanges(text, root);
    function inlineSyntax(node, extended, depth = 0) {
      if (!node.children) return;
      if (node.type === 'list') {
        const tight = !node.spread && node.children.every(item => !item.spread);
        for (const item of node.children) {
          if (tight) properties(item, { 'data-am-tight': '1' });
          item.children = convertBlocks(item.children, depth + 1);
        }
        return;
      }
      if (node.type === 'blockquote') { node.children = convertBlocks(node.children, depth + 1); return; }
      node.children = node.children.flatMap(child => {
        if (child.type === 'inlineMath' || child.type === 'footnoteReference') return [protectedNode(raw(child, depth), true)];
        if (verbatimBlock(child)) {
          const inline = child.type === 'html' && !['listItem', 'blockquote', 'root'].includes(node.type);
          return [protectedNode(raw(child, depth), inline)];
        }
        if (child.type === 'code') properties(child, { 'data-am-lang': child.lang || '' });
        if (extended && ['text', 'delete', 'image', 'link'].includes(child.type)) {
          const value = raw(child, depth), literals = inlineLiterals(value);
          if (literals) {
            if (child.type !== 'text') return [protectedNode(value, true)];
            const parts = [];
            let cursor = 0;
            for (const { start, end } of literals) {
              parts.push(...textNodes(value.slice(cursor, start)), protectedNode(value.slice(start, end), true));
              cursor = end;
            }
            return parts.concat(textNodes(value.slice(cursor)));
          }
        }
        if (child.type === 'linkReference' || child.type === 'imageReference')
          properties(child, { 'data-am-reference': child.label || child.identifier });
        if (child.type === 'text' && /[\r\n]/.test(child.value)) return textNodes(child.value);
        inlineSyntax(child, extended, depth);
        return [child];
      });
    }
    const definitions = [];
    function collectDefinitions(node) {
      if (node.type === 'definition') definitions.push(node);
      else node.children?.forEach(collectDefinitions);
    }
    collectDefinitions(root);
    function convertBlocks(nodes, depth) {
      const children = [];
      let low = 0, high = ranges.length;
      while (low < high) {
        const mid = (low + high) >>> 1;
        if (ranges[mid].to < nodes[0]?.position.start.line) low = mid + 1;
        else high = mid;
      }
      for (let i = 0; i < nodes.length; i++) {
        const node = nodes[i], start = node.position.start;
        while (ranges[low]?.to < start.line) low++;
        const range = ranges[low], extended = range && range.from < node.position.end.line;
        const container = node.type === 'list' || node.type === 'blockquote' || node.type === 'table';
        let end = node.position.end, converted = node;
        if (extended && !range.inline && !container) {
          while (i + 1 < nodes.length && nodes[i + 1].position.end.line <= range.to) end = nodes[++i].position.end;
          converted = protectedNode(raw({ position: { start, end } }, depth));
        } else if (verbatimBlock(node)) converted = protectedNode(raw(node, depth));
        else {
          if (node.type === 'code') properties(node, { 'data-am-lang': node.lang || '' });
          inlineSyntax(node, extended, depth);
        }
        if (!depth) properties(converted, { 'data-am-source': start.offset + ':' + end.offset });
        children.push(converted);
      }
      return children;
    }
    // Definitions remain available to the resolver even inside a protected container.
    root.children = convertBlocks(root.children, 0).concat(definitions);
  };
}

// Keep the bundle's inspection API on the same grammar as the data processor.
const grammar = unified().use(MarkdownGfmMdToHtmlDefaultPlugins.remarkParse)
  .use(remarkGfm, { singleTilde: true }).use(remarkFrontmatter, ['yaml', 'toml']).use(remarkMath).use(remarkDirectiveLines);
export const parseMarkdown = text => grammar.parse(text);
export const hasHtml = text => containsHtml(parseMarkdown(text));
export function splitSource(text) {
  const root = parseMarkdown(text);
  const references = root.children.filter(node => node.type === 'definition')
    .map(node => text.slice(node.position.start.offset, node.position.end.offset)).join('\n');
  retainSource()(root, text);
  const blocks = [];
  let cursor = 0;
  for (const node of root.children) {
    const span = node.data?.hProperties?.['data-am-source'];
    if (!span) continue;
    const [start, end] = span.split(':').map(Number);
    if (cursor < start) {
      if (blocks.length) blocks.at(-1).raw += text.slice(cursor, start);
      else blocks.push({ raw: text.slice(cursor, start), opaque: false });
    }
    blocks.push({ raw: text.slice(start, end), opaque: node.type === 'amSource' }); cursor = end;
  }
  if (cursor < text.length) {
    if (blocks.length) blocks.at(-1).raw += text.slice(cursor);
    else blocks.push({ raw: text.slice(cursor), opaque: false });
  }
  return { blocks, references };
}

function reference(state, element) {
  const node = defaultHandlers[element.tagName](state, element);
  const label = element.properties.dataAmReference;
  if (!label || node.url !== element.properties.dataAmTarget) return node;
  return { ...node, type: node.type + 'Reference', identifier: label.toLowerCase(), label, referenceType: 'full' };
}

const handlers = {
  'am-raw': (_, node) => ({ type: 'amRaw', value: JSON.parse(node.properties.dataMd) }),
  'am-inline-source': (_, node) => ({ type: 'amRaw', value: JSON.parse(node.properties.dataMd) }),
  'am-wrap': () => ({ type: 'amWrap' }),
  a: reference, img: reference,
  pre: (state, element) => {
    const node = defaultHandlers.pre(state, element);
    const code = element.children.find(child => child.tagName === 'code');
    if (code && code.children.every(child => child.type === 'text')) node.value = code.children.map(child => child.value).join('');
    if (element.properties.dataAmDisplayLang === node.lang)
      node.lang = element.properties.dataAmLang || null;
    return node;
  },
  li: (state, element) => {
    const node = defaultHandlers.li(state, element);
    if (element.properties.dataAmTight && node.children.filter(child => child.type === 'paragraph').length <= 1) node.spread = false;
    return node;
  }
};
const writer = unified().use(remarkGfm, { singleTilde: true }).use(remarkStringify, {
  resourceLink: true, emphasis: '_', rule: '-', unsafe: [{ character: '<' }],
  handlers: { amRaw: node => node.value, amWrap: () => '\n', break: () => '\\\n' }
});

// remark puts layout newlines around block children and a separator after a
// checkbox. A space between two inline children of a tight item is user text.
function cleanListMarkup() {
  return function visit(node) {
    if (['ul', 'ol', 'li'].includes(node.tagName)) {
      delete node.properties.className;
      node.children = node.children.filter((child, index, children) =>
        child.type !== 'text' || /\S/.test(child.value) ||
        (node.tagName === 'li' && !/[\r\n]/.test(child.value) && children[index - 1]?.tagName !== 'input'));
    }
    node.children?.forEach(visit);
  };
}

export class SourceMarkdown extends Plugin {
  static get pluginName() { return 'SourceMarkdown'; }
  static get requires() { return [Markdown, GeneralHtmlSupport, OpaqueMarkdown]; }
  afterInit() {
    const editor = this.editor, html = editor.data.htmlProcessor, processor = editor.data.processor;
    const rememberEol = (_, [data]) => { this.eol = (typeof data === 'string' ? data : data?.main)?.match(/\r\n|\r|\n/)?.[0] || '\n'; };
    this.listenTo(editor.data, 'init', rememberEol, { priority: 'high' });
    this.listenTo(editor.data, 'set', rememberEol, { priority: 'high' });
    html.domConverter.inlineObjectElements.push('am-inline-source', 'am-wrap');
    html.domConverter.blockElements.push('am-raw');
    const upstream = processor.toData.bind(processor);
    const { remarkParse, remarkGfm, ...rest } = MarkdownGfmMdToHtmlDefaultPlugins;
    const reader = new MarkdownGfmMdToHtml({ plugins: {
      remarkParse, remarkGfm, frontmatter: [remarkFrontmatter, ['yaml', 'toml']], math: remarkMath, directives: remarkDirectiveLines,
      ...rest, remarkBreaks: retainSource, deleteClassesFromToDoLists: cleanListMarkup
    } });
    processor.toView = text => {
      const template = document.createElement('template'); template.innerHTML = reader.parse(text);
      // GFM puts the checkbox inside a paragraph in a loose list. CKEditor's
      // task-list converter reads it from the list item itself.
      for (const box of template.content.querySelectorAll('li > p:first-child > input[type="checkbox"]:first-child')) {
        const paragraph = box.parentElement, next = box.nextSibling;
        if (next?.nodeType === Node.TEXT_NODE && next.data.startsWith(' ')) next.deleteData(0, 1);
        paragraph.before(box);
      }
      for (const link of template.content.querySelectorAll('[data-am-reference]'))
        link.dataset.amTarget = link.getAttribute(link.tagName === 'IMG' ? 'src' : 'href');
      for (const code of template.content.querySelectorAll('pre > code[data-am-lang]')) {
        const pre = code.parentElement, lang = code.dataset.amLang;
        const languages = editor.config.get('codeBlock.languages');
        pre.dataset.amLang = lang;
        pre.dataset.amDisplayLang = languages.some(item => item.language === lang) ? lang : languages[0].language;
        if (code.dataset.amSource) pre.dataset.amSource = code.dataset.amSource;
      }
      return html.toView(template.innerHTML);
    };
    processor.toData = fragment => {
      const markup = html.toData(fragment);
      if (!/<(?:pre\b|br\b|am-raw\b|am-inline-source\b|am-wrap\b)|data-am-(?:reference|tight)/i.test(markup))
        return upstream(fragment).replace(/\r\n|\r|\n/g, this.eol);
      const template = document.createElement('template'); template.innerHTML = markup;
      for (const label of template.content.querySelectorAll('li > label')) label.replaceWith(...label.childNodes);
      const ast = toMdast(fromDom(template.content), { handlers });
      // Verbatim blocks may contain mixed line endings. Apply the buffer's EOL
      // only to newly generated Markdown, never to their stored payload.
      const parts = [], run = [];
      const emit = () => {
        if (run.length) parts.push(writer.stringify({ type: 'root', children: run.splice(0) }).replace(/\n$/, '').replace(/\r\n|\r|\n/g, this.eol));
      };
      for (const node of ast.children) {
        if (node.type === 'amRaw') { emit(); parts.push(node.value); }
        else run.push(node);
      }
      emit();
      return parts.join(this.eol + this.eol);
    };
  }
  eol = '\n';
  preserve = false;
}
