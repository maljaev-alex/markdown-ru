import { Plugin, Widget, toWidget } from 'ckeditor5';
import strings from './ru.json';

export class OpaqueMarkdown extends Plugin {
  static get requires() { return [Widget]; }
  init() {
    const editor = this.editor;
    editor.model.schema.extend('$block', { allowAttributes: ['amSourceId'] });
    editor.model.schema.extend('$blockObject', { allowAttributes: ['amSourceId'] });
    editor.model.schema.register('amRaw', { inheritAllFrom: '$blockObject', allowAttributes: ['raw', 'amSourceId'] });
    editor.conversion.for('editingDowncast').elementToElement({
      model: 'amRaw', view: (element, { writer }) => {
        const wrapper = writer.createContainerElement('div', { class: 'am-raw-block' });
        const body = writer.createUIElement('div', null, function(document) {
          const dom = this.toDomElement(document);
          const label = document.createElement('div'); label.className = 'am-raw-label'; label.textContent = strings.raw;
          const pre = document.createElement('pre'); pre.textContent = element.getAttribute('raw');
          dom.append(label, pre); return dom;
        });
        writer.insert(writer.createPositionAt(wrapper, 0), body);
        return toWidget(wrapper, writer, { label: strings.raw });
      }
    });
    editor.conversion.for('dataDowncast').elementToElement({ model: 'amRaw', view: 'am-raw' });
  }
}
