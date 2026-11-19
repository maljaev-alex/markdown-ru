// Browser fixtures use the same compare-before-write contract as the native buffer.
module.exports = function installEditorModes() {
  const editing = new Map();
  window.editorModeReply = (message, file) => {
    if (message.action !== 'beginEdit' && message.action !== 'endEdit') return null;
    const reply = { requestId: message.requestId, status: 'applied' };
    if (message.session !== file.session) reply.status = 'stale';
    else if (message.action === 'beginEdit') {
      if (message.baseText !== file.text || file.readOnly) reply.status = 'conflict';
      else {
        editing.set(file.session, { original: file.text, formatted: message.content, modified: file.modified });
        file.modified ||= file.text !== message.content;
        file.text = message.content; file.visualEditing = true; file.revision++;
      }
    } else {
      const baseline = editing.get(file.session);
      if (baseline && file.text === baseline.formatted) {
        file.text = baseline.original; file.modified = baseline.modified;
      }
      editing.delete(file.session); file.visualEditing = false; file.revision++;
    }
    reply.snapshot = { ...file };
    return reply;
  };
};
