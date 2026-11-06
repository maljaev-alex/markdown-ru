import { Plugin, Table } from 'ckeditor5';

// GFM column alignment has a Markdown representation (:--- / :---: / ---:).
// Keep the cell attributes through CKEditor's model without enabling rich cell styles.
export class GfmTableAlignment extends Plugin {
  static get requires() { return [Table]; }
  init() {
    const editor = this.editor;
    editor.model.schema.extend('tableCell', { allowAttributes: ['amAlign'] });
    editor.conversion.for('upcast').attributeToAttribute({
      view: { name: /^(td|th)$/, key: 'align', value: /^(left|right|center)$/ },
      model: { key: 'amAlign', value: view => view.getAttribute('align') }
    });
    editor.conversion.for('downcast').attributeToAttribute({ model: 'amAlign', view: 'align' });
  }
}
