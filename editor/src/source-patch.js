import { diffChars } from 'diff';

// Project edits in CKEditor's normalized Markdown back onto the original spelling.
// The caller must round-trip the candidate before accepting it: equal text spans
// alone cannot prove that Markdown delimiters still have the same meaning.
export function sourceAlignment(canonical, raw) {
  const parts = diffChars(canonical, raw, { timeout: 100 });
  if (!parts) return null;
  const spans = [];
  let before = 0, source = 0;
  for (const part of parts) {
    const length = part.value.length;
    if (!part.added && !part.removed) spans.push({ before, source, length });
    if (!part.added) before += length;
    if (!part.removed) source += length;
  }
  return spans;
}

export function patchSource(raw, before, after, spans, eol) {
  if (!spans) return null;
  const parts = diffChars(before, after, { timeout: 100 });
  if (!parts) return null;
  const edits = [];
  let offset = 0, edit;
  for (const part of parts) {
    if (!part.added && !part.removed) { edit = null; offset += part.value.length; continue; }
    if (!edit) { edit = { start: offset, end: offset, text: '' }; edits.push(edit); }
    if (part.added) edit.text += part.value;
    else { offset += part.value.length; edit.end = offset; }
  }
  function locate(position, right) {
    const matches = spans.filter(span => span.before <= position && position <= span.before + span.length);
    const span = right ? matches.at(-1) : matches[0];
    return span ? span.source + position - span.before : null;
  }
  const projected = [];
  let cursor = 0;
  for (const edit of edits) {
    const start = locate(edit.start, true);
    const end = edit.start === edit.end ? start : locate(edit.end, false);
    if (start === null || end === null || start < cursor || end < start) return null;
    projected.push({ start, end, text: edit.text.replace(/\r\n|\r|\n/g, eol) });
    cursor = end;
  }
  return projected;
}

export function applySourceEdits(raw, edits) {
  let result = '', cursor = 0;
  for (const edit of edits) { result += raw.slice(cursor, edit.start) + edit.text; cursor = edit.end; }
  return result + raw.slice(cursor);
}
