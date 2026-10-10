## Version History

### Markdown RU 0.1.12-ru.21 (2026-10-10)

* Populate model lists independently of the parent window's paint events. Apply ready CLI/API catalogs before showing Settings, preload the persistent cache on a background worker at plugin initialization, and remove the temporary saved-model caption.
* Add cold and warm CLI/API regressions that deliberately suppress parent painting and never use screenshot rendering to trigger discovery.

### Markdown RU 0.1.12-ru.20 (unreleased)

* Keep API model catalogs when leaving or renaming a profile, and when editing the selected model or translation options during discovery. Preserve the current model on a late response, suppress empty popups, and safely replace an open model list.
* Group advertised Fast-only CLI aliases into the same model and effort selectors. Keep their exact launch IDs; show Fast checked and read-only when no normal counterpart exists. Refresh older CLI catalog caches once to apply the new normalization.
* Align field and action columns, match dropdown widths to their fields, expand the model when Effort/Fast are hidden, and wrap options on narrow windows. Recalculate scrolling after dynamic layout and font changes. Add UI regressions and verify real Cursor and RouterAI catalogs.

### Markdown RU 0.1.12-ru.19 (unreleased)

* Preserve API effort and its verified model/catalog identity when Settings is saved before API model discovery completes or after it fails. Keep unpaired Cursor Fast models selectable while paired aliases remain one model plus the Fast checkbox.
* Prevent stale disk metadata from replacing a newer in-memory catalog. Restore the original preview after cancellation during a deferred render; reject inline code moved out of its Markdown link; substitute CLI argument placeholders only once.
* Keep API connection actions visible at the default window size, align the detailed fields and profile buttons, and keep the "0 characters" help phrase together.

### Markdown RU 0.1.12-ru.18 (unreleased)

* Keep each CLI model on one row. Show effort only when levels are advertised; show Fast as a separate checkbox only for an exact matching CLI alias, including conservative support beyond Cursor. Restore the exact Fast ID in saved profiles and keep model-row geometry stable while metadata loads.
* Hide API effort when the selected model does not explicitly advertise protocol-supported levels. Invalidate older persisted model catalogs once so they cannot supply obsolete effort or Fast metadata; subsequent catalogs retain their 24-hour lifetime.

### Markdown RU 0.1.12-ru.17 (unreleased)

* Start CLI installation discovery and selected CLI/API model lookup on background workers while constructing the settings dialog. Populate the current API profile automatically on open, profile or connection changes and service selection; an empty model picker also requests the catalog. Keep Save and model editing responsive during discovery.
* Persist successful CLI and API model catalogs for 24 hours beside the plugin INI, without API credentials or endpoint URLs. Explicit **Refresh** bypasses completed cache entries. Keep installation scanning on a five-minute in-memory cache.
* Show each advertised CLI model once with effort in its own field. A conservative cross-provider heuristic groups corroborated effort aliases while preserving exact launcher model IDs; Cursor retains its provider-specific handling of Thinking and ZDR aliases.
* Enable API effort only when the selected model entry explicitly advertises protocol-supported levels. Otherwise the selector is disabled and the parameter is omitted; legacy unconfirmed values are ignored until reselected from the model catalog. The UI identifies the outgoing protocol field when effort is available.
* Start Markdown chunk planning and Codex isolation lookup concurrently before the existing bounded parallel translation workers. Add UI, cache, provider-effort and alias regression coverage.

### Markdown RU 0.1.12-ru.16 (unreleased)

* Translate human comments and annotations through separate prose slots inside immutable code blocks. Support conservative comment parsing for PowerShell, Python, shell, BSL, C-family languages and JavaScript/TypeScript, directory-tree notes, text pseudo-call notes and Markdown examples. Preserve commands, technical tokens, delimiters, source lines and signed PowerShell blocks.
* Automatically retry only a part whose protected Markdown or annotation validation failed, with fresh markers and a corrective prompt, up to three total attempts. Keep completed parts, show retry progress and retain cancellation. Network, authentication, rate-limit, timeout and truncated-response failures are not retried.
* Allow natural Russian word order to rearrange inline-code markers within their original paragraph or table cell. Continue rejecting missing/duplicate markers, code-block reordering and movement between source blocks; restore each marker by its ID.
* Invalidate old translation-cache entries and add regression coverage for annotation injection, exact source restoration and bounded retry orchestration.

### Markdown RU 0.1.12-ru.15 (2026-10-09)

* Parse Markdown structure with Markdig 1.4.0 before choosing translation boundaries. Keep sections up to 12,000 characters whole; split larger or heading-free documents between independent paragraphs, retaining code, lists, tables, quotes and related explanations together.
* Remove the former 8,000-character threshold. Add a configurable minimum part size (2,000 characters by default, 0 disables the minimum) and retain the 1–8 simultaneous-request limit. Document structure can reduce the actual number of parts.
* Protect fenced, indented and inline code and mathematics with unique markers; restore the original fragments exactly and reject missing, duplicate or reordered markers and unexpected wrappers. Continue translating descriptive YAML fields. Incomplete or invalid results are not displayed or cached.
* Persist model, effort, manual arguments, output format, timeout and parallel settings per CLI provider and launcher path. Reselecting the same CLI or file preserves edits; only the explicit defaults button resets its launch options.
* Preserve independent timeout, parallel settings and button visibility per API profile. Save commits all edited connection drafts; Cancel discards them. Existing INI settings migrate automatically, and unreadable CLI settings files are preserved.
* Show saved settings immediately. Discover installed CLIs and model catalogs in the background; cache successful results for five minutes and share in-flight requests. Late responses preserve current edits.
* Keep settings geometry stable during discovery and errors. Group multithreading controls, place model and effort side by side, align action buttons and reflow controls in small windows or with large fonts.
* Bundle verified Markdig/System.Memory runtime dependencies and their licenses; the markdown-it preview renderer remains unchanged.
* Expand regression coverage for Markdown protection, parallel CLI/API requests, per-connection persistence, asynchronous discovery and settings layout. Include an independent CommonMark corpus runner covering all 652 specification examples.

### Markdown RU 0.1.12-ru.9

* Add persistent 1-8 parallel translation requests for CLI and API, defaulting to one whole-document request.
* Split long Markdown at conservative block boundaries, provide neighboring source context, and assemble only target translations in source order.
* Show completed/total fragment progress; cancel all workers on failure and cache only complete translations.
* Resolve Codex isolation once per document and retain per-request timeouts and existing credential/proxy policies.
* Cover Markdown boundaries, concurrent CLI/API transports, stable assembly, cancellation and settings persistence with regression tests.

### Markdown RU 0.1.12-ru.8

* Add per-API system/direct/custom HTTP and SOCKS5 proxy settings, explicit authentication, remote/local SOCKS DNS and DPAPI-protected proxy credentials.
* Invalidate cached API translations when the output-token parameter changes.
* Report failed INI writes and apply accepted settings to the active preview even when persistence fails.
* Reload maximum-length text fields without an INI buffer-boundary exception.
* Use Unicode Windows profile APIs for settings paths and add native Unicode and maximum-length save/read checks.
* Send DeepSeek Harness documents over stdin so official Windows batch launchers accept multiline Markdown and long prompts.
* Add regression checks for API cache invalidation and native INI write failures.
* Handle JSON null fields in standard Responses/Gemini replies and token-budget diagnostics.
* Keep startup working when API settings are damaged or locked; preserve unreadable originals separately from rolling backups.
* Save unrelated preview/CLI options when API persistence fails, and remove rolling credential backups after clearing or deleting secrets.
* Reject credentials embedded in endpoint URLs before storage or requests, and use Anthropic's temperature range.
* Preserve CLI argument quotes across INI save/reload and reject templates exceeding the native storage boundary before writing.
* Refresh unchanged preview content after synchronization changes; ignore stale scroll events when synchronization is disabled.
* Save preview settings before a translator is configured, expand resource-path variables, and fit the settings window to the available screen area.
* Create new INI files in Unicode; preserve existing ANSI encoding and report unsupported characters instead of silently replacing them.
* Pin build dependencies to verified upstream digests and validate cached archives and extracted files before reuse.
* Show simple model names, merge Cursor reasoning variants behind effort, and retain exact advertised aliases when restoring saved choices.
* Preserve the selected API endpoint when preview-only settings are saved after a transient API catalog load failure.
* Translate all Markdown extension descriptions into Russian while retaining their original identifiers.
* Start CLI processes suspended and assign their entire process trees before execution; use independent BOM-free UTF-8 pipes.
* Apply provider environment policies using the selected CLI profile even for renamed executables.
* Send AGY prompts over documented JSON stdin and exclude unsupported Kimi batch launchers from automatic profiles.

### Markdown RU 0.1.12-ru.7

* Rename the plugin UI to AnotherMarkdown + Translate-RU; show the fork URL and retain upstream credits.
* Update the installed Help/README with translation, CLI/API setup and current model/effort behavior.
* Replace the stretched legacy artwork with a square vector mark, native icon sizes from 16 to 256 pixels, and light/dark toolbar variants.

### Markdown RU 0.1.12-ru.6

* Group Cursor effort aliases even when their display names omit the effort; hide Fast variants.
* Sort CLI and API model selectors alphabetically while preserving the saved choice.
* Let API services choose the output budget by default, avoiding an artificial 8192-token cap on reasoning and translation. Anthropic retains its required explicit limit.
* Distinguish exhausted token budgets, model refusals, tool calls and incomplete responses without displaying partial translations or sensitive server output.

### NppAnotherMarkdown 0.1.12 (released 2026-08-17)
* added support mermaid plugin (thanks to @MassimilianoPili)  
![](help/plugin-mermaid.jpg) 

will produce diagram:   
![](help/plugin-mermaid-result.jpg)

### NppAnotherMarkdown 0.1.7 (released 2026-01-20)

* rewrite all client-side code from plain JavaScript to TypeScript
* panoramic image add ability to display it without scene definition
* fix: reduce text flickering in the main editor window when synchronizing checklist checkboxes

### NppAnotherMarkdown 0.1.6 (released 2026-01-15)

- Support drag-and-drop and CTRL+V insert images into markdown preview.  
Markup `![](image path)` inserted into current cursor position. 
Image naming is starts from 010.ext and incrementing with step 5, ie 010.jpg 015.jpg, 020.jpg, and each image stored in folder "./img" where markdown document located.  
Location can be changed by edit [assets/markdown/markdown.js]().

- Added navigation over existing Markdown files when clicking a link to such a file in the preview window; previous behavior: such navigation was ignored.
- Preserve preview position after switch between documents
- highlight.js: code syntax highlight plugin added. Enable it via settings  
![](help/plugin-highlight.jpg)

### NppAnotherMarkdown 0.1.5 (released 2026-01-12)
* Markdown Plugin Pack added

| plugin        | description |
|---------------|-------------|
| [abbr](https://mdit-plugins.github.io/abbr.html)               | Support abbreviation tag \<abbr\> |
| [alert](https://mdit-plugins.github.io/alert.html)             | GFM style alerts |
| [align](https://mdit-plugins.github.io/align.html)             | Plugin to align contents |
| [attrs](https://mdit-plugins.github.io/attrs.html)             | Add attrs to Markdown content |
| [container](https://mdit-plugins.github.io/container.html)     | Creating block-level custom containers |
| [dl](https://mdit-plugins.github.io/dl.html)                   | Definition list |
| [emoji](https://github.com/markdown-it/markdown-it-emoji)      | Emoji |
| [figure](https://mdit-plugins.github.io/figure.html)           | Generating figures with captions from images |
| [footnote](https://mdit-plugins.github.io/footnote.html)       | Footnotes |
| [icon](https://mdit-plugins.github.io/icon.html)               | Icons |
| [imgLazyLoad](https://mdit-plugins.github.io/img-lazyload.html)| Lazy loading for images |
| [imgMark](https://mdit-plugins.github.io/img-mark.html)        | Mark images by ID suffix for theme mode |
| [imgSize](https://mdit-plugins.github.io/img-size.html)        | Support setting size for images |
| [ins](https://mdit-plugins.github.io/ins.html)                 | Аdd \<insert\> tag support |
| [katex](https://mdit-plugins.github.io/katex.html)             | Math Expressions<br> ![](help/plugin-katex.jpg) |
| [mark](https://mdit-plugins.github.io/mark.html)               | Mark and highlight contents |
| [plantuml](https://mdit-plugins.github.io/plantuml.html)       | Support plant uml schemes |
| [ruby](https://mdit-plugins.github.io/ruby.html)               | Ruby annotation \<ruby\> |
| [spoiler](https://mdit-plugins.github.io/spoiler.html)         | Plugin to hide content |
| [stylize](https://mdit-plugins.github.io/stylize.html)         | Plugin for stylizing tokens |
| [sub](https://mdit-plugins.github.io/sub.html)                 | Plugin to support subscript |
| [sup](https://mdit-plugins.github.io/sup.html)                 | Plugin to support superscript |
| [tab](https://mdit-plugins.github.io/tab.html)                 | Block-level custom tabs |


### NppAnotherMarkdown 0.1.4 (released 2026-01-08)
* Syncing view for both window (text and markdown preview) when "Sync with first visible line" enabled.  
![](example/sync-both.gif)

### NppAnotherMarkdown 0.1.3 (released 2026-01-06)
* Editable tasklists, (bi-direction sync)

Fixes:
- [x] fix: reduce flickering panorama during text editing
- [x] fix: another attempt to make more accurate positioning in the viewer when changing the caret position or the first line  

![](example/tasklist.gif)

### NppAnotherMarkdown 0.1.2 (released 2026-01-03)
Fixes:
- [x] minor, but possible memory leaks

### NppAnotherMarkdown 0.1.1 (released 2025-12-30)
* Scene editor for 360 panoramic photos.
* 360 pano scene example  
![](example/pano/preview.gif)

Fixes:
- [x] some memory leaks
- [x] scrolling, positioning in the viewer when changing the caret position

### NppAnotherMarkdown 0.1.0 (released 2025-12-26)
* Removed support for IE11
* Removed support for the MarkdownDig library
* Markdown rendering using the [markdown-it](https://github.com/markdown-it/markdown-it) library
* Added markup for displaying panoramic photos: `{% pano360 %}`
* Added markup for displaying QR codes: `{% qrcode text="12345" %}`
* More accurate positioning in the viewer when changing the caret position or the first line
