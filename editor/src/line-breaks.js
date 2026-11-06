import { MarkdownGfmMdToHtml, MarkdownGfmMdToHtmlDefaultPlugins } from 'ckeditor5';

export function configureLineBreaks(editor) {
  const processor = editor.data.processor, html = editor.data.htmlProcessor;
  const originalToView = processor.toView.bind(processor), originalToData = processor.toData.bind(processor);
  const plugins = { ...MarkdownGfmMdToHtmlDefaultPlugins };
  delete plugins.remarkBreaks;
  const normalMarkdown = new MarkdownGfmMdToHtml({ plugins });
  let preserve = false;
  processor.toView = text => preserve ? originalToView(text) : html.toView(normalMarkdown.parse(text));
  processor.toData = fragment => {
    const markup = html.toData(fragment);
    if (!/<br\b/i.test(markup)) return originalToData(fragment);
    // CKEditor's default writer emits bare newlines for BR, relying on its
    // remarkBreaks reader. Write explicit GFM breaks so Shift+Enter and existing
    // hard breaks survive reload with either reader. A temporary text marker
    // lets the upstream writer retain its list/table/link handling unchanged.
    let serial = 0, marker;
    do { marker = 'AMGFMHARDBREAK' + serial++ + 'TOKEN'; } while (markup.includes(marker));
    const protectedView = html.toView(markup.replace(/<br\b[^>]*>/gi, marker));
    return originalToData(protectedView).replaceAll(marker, '\\\n');
  };
  return { get preserve() { return preserve; }, set(value) { preserve = !!value; } };
}
