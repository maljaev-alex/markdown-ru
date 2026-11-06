import { diffChars } from 'diff';

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
export function alignText(raw, visible) {
  const spans = [];
  let source = 0, view = 0;
  for (const part of diffChars(alignmentSource(raw, visible), visible, { timeout: 100 }) || []) {
    if (!part.added && !part.removed) spans.push({ source, view, length: part.value.length });
    if (!part.added) source += part.value.length;
    if (!part.removed) view += part.value.length;
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
    return after ? after[to] : Math.min(value, limit);
  }
  // Markdown entities and escaped punctuation occupy one visible character (or
  // one surrogate pair). Include the complete spelling when selecting that glyph.
  const atoms = [];
  const decoder = document.createElement('textarea');
  for (const match of raw.matchAll(/&(?:#[0-9]+|#x[0-9a-f]+|[a-z][a-z0-9]+);|\\[!"#$%&'()*+,\-./:;<=>?@[\]\\^_`{|}~]/gi)) {
    decoder.innerHTML = match[0];
    const decoded = match[0][0] === '\\' ? match[0].slice(1) : decoder.value;
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

export function modelMap(editor, source) {
  const { text, ranges } = source.serialize(true), model = editor.model;
  const groups = ranges.map(range => {
    let visible = '';
    const entries = [];
    function visit(node) {
      if (node.is('$text')) {
        const start = visible.length; visible += node.data;
        entries.push({ start, end: visible.length, parent: node.parent, offset: node.startOffset,
          before: model.createPositionBefore(node), after: model.createPositionAfter(node) });
      } else if (node.name === 'softBreak' || (model.schema.isObject(node) && node.name !== 'table')) {
        const start = visible.length; visible += node.name === 'softBreak' ? '\n' : '\uFFFC';
        entries.push({ start, end: visible.length, before: model.createPositionBefore(node), after: model.createPositionAfter(node) });
      } else {
        const start = visible.length;
        for (const child of node.getChildren()) visit(child);
        if (start === visible.length) entries.push({ start, end: start, parent: node, offset: 0,
          before: model.createPositionAt(node, 0), after: model.createPositionAt(node, 'end') });
        if (model.schema.isBlock(node)) visible += '\n';
      }
    }
    range.group.nodes.forEach(visit);
    const raw = text.slice(range.start, range.end);
    const alignment = visible === '\uFFFC' ? {
      toSource: offset => offset > 0 ? raw.trimEnd().length : 0,
      toView: offset => offset >= raw.trimEnd().length ? 1 : 0
    } : alignText(raw, visible);
    return { ...range, entries, alignment };
  });
  return {
    text,
    toSource(position, affinity = 1) {
      let previous;
      for (const group of groups) for (const entry of group.entries) {
        if (position.parent === entry.parent && position.offset >= entry.offset && position.offset <= entry.offset + entry.end - entry.start)
          return group.start + group.alignment.toSource(entry.start + position.offset - entry.offset, affinity);
        if (position.isBefore(entry.before)) return group.start + group.alignment.toSource(entry.start, 1);
        previous = { group, entry };
        if (position.isEqual(entry.before)) return group.start + group.alignment.toSource(entry.start, 1);
        if (position.isEqual(entry.after)) return group.start + group.alignment.toSource(entry.end, -1);
      }
      return previous ? previous.group.start + previous.group.alignment.toSource(previous.entry.end, -1) : 0;
    },
    toPosition(offset, affinity = 1) {
      const group = choose(groups, offset, affinity);
      if (!group) return model.createPositionAt(model.document.getRoot(), 0);
      const local = group.alignment.toView(offset - group.start, affinity);
      const entry = choose(group.entries, local, affinity);
      if (!entry) return model.createPositionBefore(group.group.nodes[0]);
      if (entry.parent) return model.createPositionAt(entry.parent, entry.offset + Math.max(0, Math.min(entry.end - entry.start, local - entry.start)));
      return local <= entry.start ? entry.before : entry.after;
    }
  };
}

export function previewMap(root, text) {
  const offsets = lineOffsets(text), groups = [], byOwner = new Map();
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node; (node = walker.nextNode());) {
    if (node.parentElement.closest('.linemark,script,style,[aria-hidden="true"]')) continue;
    // Renderer indentation between block elements is not displayed text. In
    // particular, a tbody and its tr can share a source range; map the cell text.
    if (!node.data.trim() && !node.parentElement.closest('p,li,th,td,pre,h1,h2,h3,h4,h5,h6')) continue;
    const owner = node.parentElement.closest('[data-am-from]') || root;
    if (!root.contains(owner)) continue;
    let group = byOwner.get(owner);
    if (!group) {
      group = { owner, start: offsets[Number(owner.dataset.amFrom)] ?? 0,
        end: offsets[Number(owner.dataset.amTo)] ?? text.length, visible: '', entries: [] };
      byOwner.set(owner, group); groups.push(group);
    }
    const start = group.visible.length; group.visible += node.data;
    group.entries.push({ node, start, end: group.visible.length });
  }
  for (const group of groups) group.alignment = alignText(text.slice(group.start, group.end), group.visible);
  return {
    groups,
    toSource(node, offset, affinity = 1) {
      if (node?.nodeType === Node.ELEMENT_NODE) {
        const child = node.childNodes[offset] || node.childNodes[offset - 1];
        const nodes = groups.flatMap(g => g.entries).filter(e => child === e.node || child?.contains(e.node));
        const entry = offset < node.childNodes.length ? nodes[0] : nodes.at(-1);
        if (entry) { const atStart = offset < node.childNodes.length; node = entry.node; offset = atStart ? 0 : node.length; }
      }
      for (const group of groups) {
        const entry = group.entries.find(e => e.node === node);
        if (entry) return group.start + group.alignment.toSource(entry.start + offset, affinity);
      }
      return null;
    },
    toPosition(offset, affinity = 1) {
      const candidates = groups.filter(g => g.start <= offset && g.end >= offset);
      const group = candidates.sort((a, b) => a.end - a.start - (b.end - b.start))[0] || choose(groups, offset, affinity);
      if (!group) return { node: root, offset: 0 };
      const local = group.alignment.toView(offset - group.start, affinity);
      const entry = choose(group.entries, local, affinity);
      return entry ? { node: entry.node, offset: Math.max(0, Math.min(entry.node.length, local - entry.start)) } : { node: root, offset: 0 };
    }
  };
}
