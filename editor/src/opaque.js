import { Plugin, Widget, toWidget } from 'ckeditor5';
import strings from './ru.json';

export class OpaqueMarkdown extends Plugin {
  static get requires() { return [Widget]; }
  init() {
    const { model, conversion } = this.editor;
    model.schema.extend('$block', { allowAttributes: ['amSourceId'] });
    model.schema.extend('$blockObject', { allowAttributes: ['amSourceId'] });
    model.schema.extend('softBreak', { allowAttributesOf: '$text' });
    for (const [name, tag, inline] of [['amRaw', 'am-raw', false], ['amInlineSource', 'am-inline-source', true]]) {
      model.schema.register(name, { inheritAllFrom: inline ? '$inlineObject' : '$blockObject', allowAttributes: ['raw', 'amSourceId'] });
      conversion.for('upcast').elementToElement({ view: { name: tag, attributes: ['data-md'] },
        model: (view, { writer }) => writer.createElement(name, { raw: JSON.parse(view.getAttribute('data-md')) }) });
      conversion.for('dataDowncast').elementToElement({ model: name, view: (node, { writer }) => {
        const raw = node.getAttribute('raw'), attrs = { 'data-md': JSON.stringify(raw) };
        return inline ? writer.createContainerElement(tag, attrs, [writer.createText(raw)]) : writer.createEmptyElement(tag, attrs);
      } });
      conversion.for('editingDowncast').elementToElement({ model: name, view: (node, { writer }) => {
        const wrapper = writer.createContainerElement(inline ? 'span' : 'div', { class: inline ? 'am-raw-inline' : 'am-raw-block' });
        const body = writer.createUIElement(inline ? 'span' : 'div', null, function(doc) {
          const dom = this.toDomElement(doc);
          if (inline) dom.textContent = node.getAttribute('raw');
          else {
            const label = doc.createElement('div'); label.className = 'am-raw-label'; label.textContent = strings.raw;
            const pre = doc.createElement('pre'); pre.textContent = node.getAttribute('raw'); dom.append(label, pre);
          }
          return dom;
        });
        writer.insert(writer.createPositionAt(wrapper, 0), body);
        return toWidget(wrapper, writer, { label: strings.raw });
      } });
    }
    model.schema.register('amSoftWrap', { inheritAllFrom: '$inlineObject' });
    conversion.for('upcast').elementToElement({ view: 'am-wrap', model: 'amSoftWrap' });
    conversion.for('dataDowncast').elementToElement({ model: 'amSoftWrap', view: (_, { writer }) => writer.createEmptyElement('am-wrap') });
    conversion.for('editingDowncast').elementToElement({ model: 'amSoftWrap', view: (_, { writer }) =>
      this.editor.plugins.get('SourceMarkdown').preserve ? writer.createEmptyElement('br') :
        writer.createUIElement('span', {}, function(doc) { const span = this.toDomElement(doc); span.textContent = ' '; return span; }) });
    // These positions are used only to map the native caret in a freshly loaded document.
    conversion.for('upcast').add(dispatcher => dispatcher.on('element', (_, data, api) => {
      const id = data.viewItem.getAttribute('data-am-source');
      if (id && data.modelRange) {
        for (const node of data.modelRange.getItems({ shallow: true }))
          if (model.schema.checkAttribute(node, 'amSourceId')) api.writer.setAttribute('amSourceId', id, node);
      }
    }, { priority: 'lowest' }));
  }
}
