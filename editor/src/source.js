import MarkdownIt from 'markdown-it';
import { diff3Merge } from 'node-diff3';
import { applySourceEdits, patchSource, sourceAlignment } from './source-patch.js';

const parser = new MarkdownIt({ html: true });
const lines = text => text.match(/[^\n]*\n|[^\n]+$/g) || [];

export function mergeSource(base, visual, source) {
  if (visual === base || visual === source) return { text: source, regions: [] };
  if (source === base) return { text: visual, regions: [] };
  const regions = diff3Merge(lines(visual), lines(base), lines(source), { excludeFalseConflicts: true });
  return { regions, text: regions.some(r => r.conflict) ? null : regions.flatMap(r => r.ok).join('') };
}

export function hasHtml(markdown) {
  return parser.parse(markdown, {}).some(t => t.type === 'html_block' || t.children?.some(c => c.type === 'html_inline'));
}

function markdownMeaning(markdown) {
  const describe = token => [token.type, token.tag, token.nesting, token.attrs,
    token.children ? token.children.map(describe) : token.content, token.info];
  // Tight and loose lists have the same token structure. Their `hidden` flags
  // differ after copying a selection out of CKEditor; retain the source spacing.
  return JSON.stringify(parser.parse(markdown, {}).map(describe));
}

export function splitSource(source) {
  const offsets = [0];
  for (const match of source.matchAll(/\r\n|\r|\n/g)) offsets.push(match.index + match[0].length);
  const env = {};
  const tokens = parser.parse(source, env);
  const blocks = [];
  let cursor = 0;
  const frontmatter = /^(?:\uFEFF)?---\r?\n[\s\S]*?\r?\n(?:---|\.\.\.)(?:\r?\n|$)/.exec(source);
  if (frontmatter) { blocks.push({ raw: frontmatter[0], opaque: true }); cursor = frontmatter[0].length; }
  const gap = end => {
    const raw = source.slice(cursor, end);
    if (!raw) return;
    if (!raw.trim() && blocks.length) blocks[blocks.length - 1].raw += raw;
    else blocks.push({ raw, opaque: !!raw.trim() });
  };
  for (const token of tokens) {
    if (!token.map || token.level !== 0 || token.nesting === -1 || token.type === 'inline') continue;
    const start = offsets[token.map[0]] ?? source.length;
    const end = offsets[token.map[1]] ?? source.length;
    if (start < cursor) continue;
    gap(start);
    const raw = source.slice(start, end);
    const extension = /(^|\n)[ \t]*(:::|\[\^|\*\[)|\[\^[^\]]+\]|\{\{|\{[.#][\w-]|\$\$|(?<!\\)\$[^\s$][^\n$]*\$|==[^=]+==|\+\+[^+]+\+\+/.test(raw);
    blocks.push({ raw, opaque: extension || hasHtml(raw) });
    cursor = end;
  }
  gap(source.length);
  const references = Object.entries(env.references || {}).map(([name, ref]) =>
    `[${name}]: <${ref.href}>${ref.title ? ' "' + ref.title.replace(/"/g, '\\"') + '"' : ''}`).join('\n');
  return { blocks, references };
}

// Source spans are attached to model blocks, never emitted as HTML attributes.
// Only edited groups are serialized. Unsupported blocks remain opaque objects.
export class SourceDocument {
  constructor(editor) { this.editor = editor; this.records = new Map(); this.serial = 0; }

  load(source) {
    this.original = source;
    this.eol = /\r\n/.test(source) ? '\r\n' : /\r/.test(source) && !/\n/.test(source) ? '\r' : '\n';
    this.records.clear();
    const { blocks, references } = splitSource(source);
    this.references = references;
    const { model, data } = this.editor;
    const root = model.document.getRoot();
    model.enqueueChange({ isUndoable: false }, writer => {
      writer.remove(writer.createRangeIn(root));
      for (const block of blocks) {
        const id = 's' + (++this.serial);
        this.records.set(id, { ...block });
        if (block.opaque) {
          writer.append(writer.createElement('amRaw', { amSourceId: id, raw: block.raw }), root);
        } else {
          const fragment = data.parse(block.raw + (references ? '\n\n' + references : ''));
          if (!fragment.childCount) writer.append(writer.createElement('paragraph'), fragment);
          for (const child of fragment.getChildren()) writer.setAttribute('amSourceId', id, child);
          writer.append(fragment, root);
        }
      }
      if (!root.childCount) writer.append(writer.createElement('paragraph'), root);
      writer.setSelection(root, 'end');
    });
    this.editor.commands.get('undo').clearStack();
    this.editor.commands.get('redo').clearStack();
    this.originalOrder = this.groups().map(group => group.id);
    for (const group of this.groups()) {
      const record = this.records.get(group.id);
      if (record && !record.opaque) record.canonical = this.stringify(group);
    }
    this.initialSignature = this.signature();
  }

  groups() {
    const groups = [];
    for (const node of this.editor.model.document.getRoot().getChildren()) {
      const id = node.getAttribute('amSourceId');
      const previous = groups[groups.length - 1];
      // Consecutive list paragraphs must serialize together, including newly inserted items.
      const list = node.hasAttribute('listType');
      if (previous && ((id && previous.id === id) || (!id && list && previous.list))) previous.nodes.push(node);
      else groups.push({ id, nodes: [node], list });
    }
    return groups;
  }

  stringify(group) {
    const model = this.editor.model;
    const range = model.createRange(model.createPositionBefore(group.nodes[0]), model.createPositionAfter(group.nodes.at(-1)));
    return this.editor.data.stringify(model.getSelectedContent(model.createSelection(range)));
  }

  signature() {
    return JSON.stringify(this.groups().map(g => [g.id, this.records.get(g.id)?.opaque ? g.nodes[0].getAttribute('raw') : this.stringify(g)]));
  }

  preserve(record, canonical) {
    if (!record) return null;
    if (record.editedCanonical === canonical) return record.editedRaw;
    record.editedCanonical = canonical; record.editedRaw = null;
    if (record.alignment === undefined) record.alignment = sourceAlignment(record.canonical, record.raw);
    let edits = patchSource(record.raw, record.canonical, canonical, record.alignment, this.eol);
    if (!edits) return null;
    const expected = markdownMeaning(canonical);
    const valid = candidate => {
      if (hasHtml(candidate)) return false;
      const fragment = this.editor.data.parse(candidate + (this.references ? '\n\n' + this.references : ''));
      return markdownMeaning(this.editor.data.stringify(fragment)) === expected;
    };
    if (!valid(applySourceEdits(record.raw, edits))) return null;
    // Serializers also adjust table padding and delimiter widths after a cell edit.
    // Remove those incidental edits in batches, retaining every meaningful change.
    let budget = 64;
    const prune = candidates => {
      if (!candidates.length || budget-- <= 0) return;
      const omitted = new Set(candidates), trial = edits.filter(edit => !omitted.has(edit));
      if (valid(applySourceEdits(record.raw, trial))) { edits = trial; return; }
      if (candidates.length > 1) {
        const middle = Math.floor(candidates.length / 2);
        prune(candidates.slice(0, middle)); prune(candidates.slice(middle));
      }
    };
    prune(edits.filter(edit => /^[\s*_:|\-]*$/.test(edit.text + record.raw.slice(edit.start, edit.end))));
    record.editedRaw = applySourceEdits(record.raw, edits);
    return record.editedRaw;
  }

  serialize(withRanges = false) {
    if (!withRanges && this.signature() === this.initialSignature) return this.original;
    const groups = this.groups();
    const ranges = [];
    let output = '';
    let previousOriginalIndex = -2;
    for (const group of groups) {
      const record = this.records.get(group.id);
      const canonical = record?.opaque ? null : this.stringify(group);
      const unchanged = record && (record.opaque || canonical === record.canonical);
      const originalIndex = this.originalOrder.indexOf(group.id);
      let preserved = unchanged;
      let text;
      if (unchanged) text = record.raw;
      else {
        if (hasHtml(canonical)) throw new Error('html-not-supported');
        text = this.preserve(record, canonical);
        preserved = text !== null;
        if (!preserved) {
          text = canonical.replace(/\r\n|\r|\n/g, this.eol);
          const tail = record?.raw.match(/(?:(?:\r\n|\r|\n)[\t ]*)+$/)?.[0];
          text = text.replace(/(?:\r\n|\r|\n)+$/, '') + (tail || '');
        }
      }
      if (output && !(preserved && originalIndex === previousOriginalIndex + 1)) {
        if (!output.endsWith(this.eol + this.eol) && !text.startsWith(this.eol))
          output += output.endsWith(this.eol) ? this.eol : this.eol + this.eol;
      }
      const start = output.length;
      output += text;
      ranges.push({ group, start, end: output.length });
      previousOriginalIndex = preserved ? originalIndex : -2;
    }
    // A newly inserted final block follows the document's existing final-newline convention.
    const last = groups.at(-1);
    const lastRecord = this.records.get(last?.id);
    if (!lastRecord) {
      output = output.replace(/(?:\r?\n)+$/, '');
      if (/(?:\r\n|\r|\n)$/.test(this.original)) output += this.eol;
    }
    if (ranges.length) ranges.at(-1).end = output.length;
    return withRanges ? { text: output, ranges } : output;
  }
}
