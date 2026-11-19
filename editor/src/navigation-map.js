import { diffArrays, diffChars } from 'diff';
import { parseMarkdown } from './markdown.js';

export function lineOffsets(text) {
  const result = [0];
  for (const match of text.matchAll(/\r\n|\r|\n/g)) result.push(match.index + match[0].length);
  return result;
}

// These attributes belong only to the rendered DOM, never to the saved Markdown.
export function configurePreviewParser(parser) {
  if (parser.__amNavigation) return;
  parser.__amNavigation = true;
  const validateLink = parser.validateLink;
  parser.validateLink = href => /^file:\/\//i.test(href) || validateLink(href);
  parser.core.ruler.push('am_navigation', state => {
    for (const token of state.tokens) {
      if (!token.map || token.type === 'inline' || token.nesting < 0) continue;
      token.attrSet('data-am-from', String(token.map[0]));
      token.attrSet('data-am-to', String(token.map[1]));
    }
  });
}

function alignmentSource(raw, visible) {
  const chars = raw.split('');
  const hide = (start, end) => { for (let i = start; i < end; i++) chars[i] = '\u0000'; };
  const fence = /^(?:[ \t]*\r?\n)*[ \t]{0,3}(`{3,}|~{3,})[^\r\n]*(?:\r\n|\r|\n)/.exec(raw);
  if (fence) {
    hide(0, fence[0].length);
    const closing = new RegExp('(?:^|\\n)[ \\t]{0,3}' + fence[1][0] + '{' + fence[1].length + ',}[ \\t]*(?:\\r?\\n)*$').exec(raw);
    if (closing) hide(closing.index, raw.length);
    return chars.join('');
  }
  for (const match of raw.matchAll(/^(?:[ \t]{0,3}>[ \t]?)*(?:[ \t]{0,3}#{1,6}[ \t]+|[ \t]*(?:[-+*]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?|[ \t]*>[ \t]?)/gm)) hide(match.index, match.index + match[0].length);
  for (let i = 0; i < raw.length; i++) {
    if (raw[i] === '\\') { i++; continue; }
    if (raw[i] === '`') {
      const ticks = /^`+/.exec(raw.slice(i))[0];
      const end = raw.indexOf(ticks, i + ticks.length);
      if (end >= 0) { hide(i, i + ticks.length); hide(end, end + ticks.length); i = end + ticks.length - 1; }
      continue;
    }
    if (raw[i] !== '[') continue;
    let end = i + 1, depth = 1;
    for (; end < raw.length && depth; end++) {
      if (raw[end] === '\\') end++;
      else if (raw[end] === '[') depth++;
      else if (raw[end] === ']') depth--;
    }
    if (depth) continue;
    if (raw[end] === '(') {
      let tail = end + 1, parentheses = 1, quote = '';
      for (; tail < raw.length && parentheses; tail++) {
        const char = raw[tail];
        if (char === '\\') { tail++; continue; }
        if (quote) { if (char === quote) quote = ''; continue; }
        if ((char === '"' || char === "'") && /\s/.test(raw[tail - 1])) quote = char;
        else if (char === '(') parentheses++;
        else if (char === ')') parentheses--;
      }
      if (!parentheses && !visible.includes(raw.slice(i, tail))) { hide(i, i + 1); hide(end - 1, tail); i = tail - 1; }
    } else if (raw[end] === '[') {
      const tail = raw.indexOf(']', end + 1);
      if (tail >= 0 && !visible.includes(raw.slice(i, tail + 1))) { hide(i, i + 1); hide(end - 1, tail + 1); i = tail; }
    }
  }
  return chars.join('');
}

// Map within a single Markdown block, not across unrelated repeated paragraphs.
// Removed syntax maps to the nearest visible boundary. Boundary affinity keeps
// selections inside delimiters, while a caret at the next word stays with it.
export function alignText(raw, visible, maskSyntax = true) {
  if (raw === visible) {
    const offset = n => Math.max(0, Math.min(raw.length, n));
    return { toView: offset, toSource: offset };
  }
  const spans = [];
  let source = 0, view = 0;
  const comparable = maskSyntax ? alignmentSource(raw, visible) : raw;
  const diff = diffChars(comparable, visible, { timeout: 100 });
  for (const part of diff || []) {
    if (!part.added && !part.removed) spans.push({ source, view, length: part.value.length });
    if (!part.added) source += part.value.length;
    if (!part.removed) view += part.value.length;
  }
  // Formatting can add thousands of spaces to a single block. If a character
  // diff exceeds its budget, anchor to common words at their real offsets.
  if (!diff) {
    const words = value => Array.from(value.matchAll(/[\p{L}\p{M}\p{N}_]+|[^\s\0]/gu));
    const left = words(comparable), right = words(visible);
    const changes = diffArrays(left, right, { comparator: (a, b) => a[0] === b[0], timeout: 100 });
    let i = 0, j = 0;
    const match = (a, b) => spans.push({ source: a.index, view: b.index, length: a[0].length });
    if (changes) {
      for (const part of changes) {
        if (!part.added && !part.removed) for (let n = 0; n < part.count; n++) match(left[i + n], right[j + n]);
        if (!part.added) i += part.count;
        if (!part.removed) j += part.count;
      }
    } else {
      const hits = new Map();
      for (let n = right.length - 1; n >= 0; n--) {
        const word = right[n][0];
        if (!hits.has(word)) hits.set(word, []);
        hits.get(word).push(n);
      }
      for (const word of left) {
        const positions = hits.get(word[0]);
        while (positions?.length && positions.at(-1) < j) positions.pop();
        if (positions?.length) {
          const at = positions.pop();
          match(word, right[at]); j = at + 1;
        }
      }
    }
  }
  function project(value, from, to, limit, affinity = 1) {
    let before, after;
    for (const span of spans) {
      if (value > span[from] && value < span[from] + span.length) return span[to] + value - span[from];
      if (span[from] + span.length <= value) before = span;
      if (span[from] >= value) { after = span; break; }
    }
    if (before && after) {
      const left = value - before[from] - before.length, right = after[from] - value;
      return left < right || (left === right && affinity < 0) ? before[to] + before.length : after[to];
    }
    if (before) return before[to] + before.length;
    return after ? after[to] : (affinity < 0 && value > 0 ? limit : 0);
  }
  // Markdown entities and escaped punctuation occupy one visible character (or
  // one surrogate pair). Include the complete spelling when selecting that glyph.
  const atoms = [];
  if (!maskSyntax) return { toView: (offset, affinity) => project(offset, 'source', 'view', visible.length, affinity) };
  let decoder;
  for (const match of raw.matchAll(/&(?:#[0-9]+|#x[0-9a-f]+|[a-z][a-z0-9]+);|\\[!"#$%&'()*+,\-./:;<=>?@[\]\\^_`{|}~]/gi)) {
    let decoded = match[0].slice(1);
    if (match[0][0] === '&') {
      decoder ??= document.createElement('textarea');
      decoder.innerHTML = match[0]; decoded = decoder.value;
    }
    const start = project(match.index, 'source', 'view', visible.length);
    if (decoded !== match[0] && visible.slice(start, start + decoded.length) === decoded &&
        !visible.slice(start, start + match[0].length).startsWith(match[0]))
      atoms.push({ source: match.index, end: match.index + match[0].length, view: start, length: decoded.length });
  }
  return {
    toSource(offset, affinity = 1) {
      for (const atom of atoms) {
        if (offset === atom.view) return atom.source;
        if (offset > atom.view && offset <= atom.view + atom.length) return atom.end;
      }
      return project(offset, 'view', 'source', raw.length, affinity);
    },
    toView(offset, affinity = 1) {
      for (const atom of atoms) if (offset >= atom.source && offset <= atom.end)
        return offset === atom.end ? atom.view + atom.length : atom.view;
      return project(offset, 'source', 'view', visible.length, affinity);
    }
  };
}

function choose(entries, offset, affinity) {
  let previous;
  for (const entry of entries) {
    if (offset < entry.start) return previous && affinity < 0 ? previous : entry;
    if (offset < entry.end || (offset === entry.end && affinity < 0)) return entry;
    previous = entry;
  }
  return previous;
}

const bounds = node => ({ start: node.position.start.offset, end: node.position.end.offset });

// Both the editor and its navigation use the same GFM grammar. Cell positions
// include their delimiters/padding; inline children delimit the editable text.
function sourceLayout(text) {
  const tables = [], paragraphs = [];
  function visit(node) {
    if (node.type === 'table') {
      tables.push({ ...bounds(node), line: node.position.start.line - 1, rows: node.children.map(row => ({
        ...bounds(row),
        cells: row.children.map(cell => {
          const span = bounds(cell);
          const contentStart = cell.children[0]?.position.start.offset ?? span.start + Number(text[span.start] === '|');
          return { ...span, contentStart, contentEnd: cell.children.at(-1)?.position.end.offset ?? contentStart };
        })
      })) });
    } else if (node.type === 'paragraph' || node.type === 'heading') {
      paragraphs.push({ ...bounds(node), type: node.type, contentStart: node.children[0]?.position.start.offset,
        contentEnd: node.children.at(-1)?.position.end.offset });
    } else if (node.type !== 'footnoteDefinition') node.children?.forEach(visit);
  }
  visit(parseMarkdown(text));
  return { tables, paragraphs };
}

function cellBounds(row, index) {
  // GFM renders missing trailing cells as empty cells in that same row.
  return row.cells[index] || { start: row.end, end: row.end };
}

function alignedGroup(text, span, entries, visible) {
  const from = span.contentStart ?? span.start, to = span.contentEnd ?? span.end;
  let alignment;
  const project = () => {
    if (!alignment) {
      const raw = text.slice(from, to);
      alignment = visible === '\uFFFC' ? {
        toSource: offset => offset > 0 ? raw.trimEnd().length : 0,
        toView: offset => offset >= raw.trimEnd().length ? 1 : 0
      } : alignText(raw, visible);
    }
    return alignment;
  };
  return { ...span, entries,
    sourceAt: (offset, affinity) => from + project().toSource(offset, affinity),
    viewAt: (offset, affinity) => project().toView(Math.max(0, Math.min(to - from, offset - from)), affinity) };
}

// Source spelling can change across the entire document on a mode switch.
// Follow corresponding syntax nodes, diffing only the token or gap containing
// the position (including padding between table cells and around inline code).
export function formattingPositionMap(before, after) {
  const left = parseMarkdown(before), right = parseMarkdown(after);
  function project(a, b, offset, affinity) {
    let a0 = a.position.start.offset, b0 = b.position.start.offset;
    let a1 = a.position.end.offset, b1 = b.position.end.offset;
    if (a.children?.length && a.children.length === b.children?.length &&
        a.children.every((node, i) => node.type === b.children[i].type)) {
      for (let i = 0; i < a.children.length; i++) {
        const child = a.children[i], target = b.children[i];
        const start = child.position.start.offset, end = child.position.end.offset;
        if (offset < start || (offset === start && affinity < 0 && offset > a0)) {
          a1 = start; b1 = target.position.start.offset; break;
        }
        if (offset < end || (offset === end && affinity < 0)) return project(child, target, offset, affinity);
        a0 = end; b0 = target.position.end.offset;
      }
    }
    if (offset === a0) return b0;
    if (offset === a1) return b1;
    return b0 + alignText(before.slice(a0, a1), after.slice(b0, b1), false).toView(offset - a0, affinity);
  }
  return (offset, affinity = 1) => project(left, right, Math.max(0, Math.min(before.length, offset)), affinity);
}

export function modelMap(editor, source) {
  const { text, ranges } = source.serialize(true), model = editor.model;
  const groups = [], scrollBlocks = [];
  function addScrollBlock(node, offset) {
    const view = editor.editing.mapper.toViewElement(node);
    const element = view && editor.editing.view.domConverter.mapViewToDom(view);
    if (element) scrollBlocks.push({ element, offset });
  }
  let layout, tableIndex = 0, paragraphIndex = 0;
  for (const range of ranges) {
    let visible = '', entries = [], segmentStart = range.start;
    const paragraphs = new Map();
    if (range.group.nodes.some(n => n.hasAttribute('listType') || n.name === 'blockQuote')) {
      layout ??= sourceLayout(text);
      const blocks = [], spans = [];
      function collect(node) {
        if (node.name === 'blockQuote') for (const child of node.getChildren()) collect(child);
        else if (node.name === 'paragraph' || /^heading[1-6]$/.test(node.name)) blocks.push(node);
      }
      range.group.nodes.forEach(collect);
      while (layout.paragraphs[paragraphIndex]?.start < range.end) {
        const span = layout.paragraphs[paragraphIndex++];
        if (span.start >= range.start && span.end <= range.end) spans.push(span);
      }
      if (blocks.length === spans.length && blocks.every((n, i) => n.name.startsWith(spans[i].type)))
        blocks.forEach((node, i) => paragraphs.set(node, spans[i]));
    }
    function finish(span) {
      if (entries.length) groups.push(alignedGroup(text, span, entries, visible));
      visible = ''; entries = [];
    }
    function visit(node) {
      const paragraph = paragraphs.get(node);
      if (paragraph) addScrollBlock(node, paragraph.start);
      else if (node === range.group.nodes[0]) addScrollBlock(node, range.start);
      if (node.name === 'table') {
        layout ??= sourceLayout(text);
        const tables = layout.tables;
        while (tables[tableIndex]?.end <= segmentStart) tableIndex++;
        const table = tables[tableIndex];
        if (table && table.start < range.end && table.rows.length === node.childCount) {
          tableIndex++;
          finish({ start: segmentStart, end: table.start });
          for (const row of node.getChildren()) {
            const rowSource = table.rows[row.index];
            addScrollBlock(row, rowSource.start);
            for (const cell of row.getChildren()) {
              for (const child of cell.getChildren()) visit(child);
              finish(cellBounds(rowSource, cell.index));
            }
          }
          segmentStart = table.end;
          return;
        }
      }
      if (node.is('$text')) {
        const start = visible.length; visible += node.data;
        entries.push({ start, end: visible.length, parent: node.parent, offset: node.startOffset,
          before: model.createPositionBefore(node), after: model.createPositionAfter(node) });
      } else if (node.name === 'softBreak' || (model.schema.isObject(node) && node.name !== 'table')) {
        const start = visible.length; visible += node.name === 'softBreak' || node.name === 'amSoftWrap' ? '\n' : '\uFFFC';
        entries.push({ start, end: visible.length, before: model.createPositionBefore(node), after: model.createPositionAfter(node) });
      } else {
        const span = paragraphs.get(node);
        if (span) finish({ start: segmentStart, end: span.start });
        for (const child of node.getChildren()) visit(child);
        if (!node.childCount) entries.push({ start: visible.length, end: visible.length, parent: node, offset: 0,
          before: model.createPositionAt(node, 0), after: model.createPositionAt(node, 'end') });
        if (span) { finish(span); segmentStart = span.end; }
        else if (model.schema.isBlock(node)) visible += '\n';
      }
    }
    range.group.nodes.forEach(visit);
    finish({ start: segmentStart, end: range.end });
  }
  return {
    text, scrollBlocks,
    toSource(position, affinity = 1) {
      let previous;
      for (const group of groups) for (const entry of group.entries) {
        if (position.parent === entry.parent && position.offset >= entry.offset && position.offset <= entry.offset + entry.end - entry.start)
          return group.sourceAt(entry.start + position.offset - entry.offset, affinity);
        if (position.isBefore(entry.before)) return affinity < 0 && previous
          ? previous.group.sourceAt(previous.entry.end, -1) : group.sourceAt(entry.start, 1);
        previous = { group, entry };
        if (position.isEqual(entry.before)) return group.sourceAt(entry.start, 1);
        if (position.isEqual(entry.after)) return group.sourceAt(entry.end, -1);
      }
      return previous ? previous.group.sourceAt(previous.entry.end, -1) : 0;
    },
    toPosition(offset, affinity = 1) {
      const group = choose(groups, offset, affinity);
      if (!group) return model.createPositionAt(model.document.getRoot(), 0);
      const local = group.viewAt(offset, affinity);
      const entry = choose(group.entries, local, affinity);
      if (!entry) return model.createPositionBefore(group.group.nodes[0]);
      if (entry.parent) return model.createPositionAt(entry.parent, entry.offset + Math.max(0, Math.min(entry.end - entry.start, local - entry.start)));
      return local <= entry.start ? entry.before : entry.after;
    }
  };
}

export function previewMap(root, text) {
  const offsets = lineOffsets(text), byOwner = new Map(), cells = new Map(), rowStarts = new Map(), scrollBlocks = [];
  const renderedTables = root.querySelectorAll('table[data-am-from]');
  if (renderedTables.length) {
    const tables = new Map(sourceLayout(text).tables.map(table => [table.line, table]));
    for (const element of renderedTables) {
      const table = tables.get(Number(element.dataset.amFrom));
      if (!table) continue;
      for (const row of element.rows) {
        const source = table.rows[row.rowIndex];
        if (source) {
          rowStarts.set(row, source.start);
          for (const cell of row.cells) cells.set(cell, cellBounds(source, cell.cellIndex));
        }
      }
    }
  }
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
  for (let node; (node = walker.nextNode());) {
    if (node.nodeType === Node.ELEMENT_NODE) {
      const offset = rowStarts.get(node) ?? (node.hasAttribute('data-am-from') && !node.parentElement.closest('table')
        ? offsets[Number(node.dataset.amFrom)] : undefined);
      if (offset != null) scrollBlocks.push({ element: node, offset });
      if (cells.has(node)) byOwner.set(node, { owner: node, ...cells.get(node), visible: '', entries: [] });
      continue;
    }
    if (node.parentElement.closest('.linemark,script,style,[aria-hidden="true"]')) continue;
    // Renderer indentation between block elements is not displayed text. In
    // particular, a tbody and its tr can share a source range; map the cell text.
    if (!node.data.trim() && !node.parentElement.closest('p,li,th,td,pre,h1,h2,h3,h4,h5,h6')) continue;
    const cell = node.parentElement.closest('td,th');
    const owner = cells.has(cell) ? cell : node.parentElement.closest('[data-am-from]') || root;
    if (!root.contains(owner)) continue;
    let group = byOwner.get(owner);
    if (!group) {
      group = { owner, start: offsets[Number(owner.dataset.amFrom)] ?? 0,
        end: offsets[Number(owner.dataset.amTo)] ?? text.length, visible: '', entries: [] };
      byOwner.set(owner, group);
    }
    const start = group.visible.length; group.visible += node.data;
    group.entries.push({ node, start, end: group.visible.length });
  }
  const groups = Array.from(byOwner.values(), group => alignedGroup(text, group, group.entries, group.visible));
  const byNode = new Map();
  for (const group of groups) {
    for (const entry of group.entries) byNode.set(entry.node, { group, entry });
    if (!group.entries.length) byNode.set(group.owner, { group, entry: { start: 0, end: 0 } });
  }
  function edge(node, forward) {
    const match = byNode.get(node);
    if (match) return match.group.sourceAt(forward ? match.entry.start : match.entry.end, forward ? 1 : -1);
    for (let child = forward ? node.firstChild : node.lastChild; child; child = forward ? child.nextSibling : child.previousSibling) {
      const offset = edge(child, forward);
      if (offset != null) return offset;
    }
    return null;
  }
  return {
    groups, scrollBlocks,
    toSource(node, offset, affinity = 1) {
      if (node?.nodeType === Node.ELEMENT_NODE) {
        for (const forward of [affinity > 0, affinity <= 0]) {
          for (let child = node.childNodes[forward ? offset : offset - 1]; child; child = forward ? child.nextSibling : child.previousSibling) {
            const boundary = edge(child, forward);
            if (boundary != null) return boundary;
          }
        }
        if (!byNode.has(node)) node = node.closest('td,th');
      }
      const match = byNode.get(node);
      return match ? match.group.sourceAt(match.entry.start + offset, affinity) : null;
    },
    toPosition(offset, affinity = 1) {
      const candidates = groups.filter(g => (offset > g.start || offset === g.start && affinity > 0) &&
        (offset < g.end || offset === g.end && affinity < 0));
      const group = candidates.sort((a, b) => a.end - a.start - (b.end - b.start))[0] || choose(groups, offset, affinity);
      if (!group) return { node: root, offset: 0 };
      const local = group.viewAt(offset, affinity);
      const entry = choose(group.entries, local, affinity);
      return entry ? { node: entry.node, offset: Math.max(0, Math.min(entry.node.length, local - entry.start)) } : { node: group.owner, offset: 0 };
    }
  };
}
