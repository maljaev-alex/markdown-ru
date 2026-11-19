import { diff3Merge } from 'node-diff3';
export { hasHtml, splitSource } from './markdown.js';

const lines = text => text.match(/[^\r\n]*(?:\r\n|\r|\n)|[^\r\n]+$/g) || [];

export function mergeSource(base, visual, source) {
  if (visual === base || visual === source) return { text: source, regions: [] };
  if (source === base) return { text: visual, regions: [] };
  const regions = diff3Merge(lines(visual), lines(base), lines(source), { excludeFalseConflicts: true });
  return { regions, text: regions.some(r => r.conflict) ? null : regions.flatMap(r => r.ok).join('') };
}

// CKEditor's data processor owns Markdown. This adapter keeps the exact native
// snapshot for preview and maps model ranges only when caret sync requests them.
export class SourceDocument {
  constructor(editor) { this.editor = editor; }
  load(text) {
    this.original = text;
    this.eol = text.match(/\r\n|\r|\n/)?.[0] || '\n';
    this.editor.setData(text);
    this.loadedVersion = this.editor.model.document.version;
    this.formattedVersion = -1;
  }
  formatted() {
    const version = this.editor.model.document.version;
    if (version !== this.formattedVersion) {
      let text = this.editor.getData();
      if (/[\r\n]$/.test(this.original) && !/[\r\n]$/.test(text)) text += this.eol;
      this.formattedText = text; this.formattedVersion = version;
    }
    return this.formattedText;
  }
  serialize(withRanges = false) {
    const pristine = this.loadedVersion === this.editor.model.document.version;
    const text = pristine ? this.original : this.formatted();
    if (!withRanges) return text;
    const groups = [], model = this.editor.model;
    for (const node of model.document.getRoot().getChildren()) {
      const id = node.getAttribute('amSourceId'), prev = groups.at(-1);
      if (prev && (id && prev.id === id || !id && node.hasAttribute('listType') && prev.nodes.at(-1).hasAttribute('listType')))
        prev.nodes.push(node);
      else groups.push({ id, nodes: [node] });
    }
    let cursor = 0;
    const ranges = groups.map(group => {
      let start = cursor, end;
      if (pristine && group.id) [start, end] = group.id.split(':').map(Number);
      else {
        const range = model.createRange(model.createPositionBefore(group.nodes[0]), model.createPositionAfter(group.nodes.at(-1)));
        const block = this.editor.data.stringify(model.getSelectedContent(model.createSelection(range)));
        const found = text.indexOf(block, cursor);
        if (found >= 0) start = found;
        end = Math.min(text.length, start + block.length);
      }
      cursor = end;
      return { group, start, end };
    });
    return { text, ranges };
  }
}
