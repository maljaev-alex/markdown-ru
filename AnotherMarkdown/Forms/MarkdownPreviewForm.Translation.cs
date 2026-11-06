using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Translation;

namespace AnotherMarkdown.Forms
{
  public partial class MarkdownPreviewForm
  {
    public event EventHandler TranslationSettingsRequested;
    public bool IsTranslationPreview { get; private set; }
    private readonly TranslationCache translationCache = new TranslationCache();
    private readonly SemaphoreSlim renderGate = new SemaphoreSlim(1, 1);
    private TranslationOptions translationOptions;
    private CancellationTokenSource translationCancellation;
    private ToolStripButton translateButton, originalButton, cancelButton, translationSettingsButton, retranslateButton;
    private ToolStripLabel translationStatus;
    private ToolStripLabel translationIndicator;
    private System.Windows.Forms.Timer translationAnimation;
    private int translationAnimationFrame;
    private string sourceText, sourcePath, translatedText, renderedText, renderedPath;
    private bool forceRender = true;
    private bool renderedTranslation;

    private void InitializeTranslation(Settings settings)
    {
      translationOptions = settings.Translation.Copy();
      translateButton = new ToolStripButton("Перевести") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Перевести документ на русский через выбранное подключение" };
      originalButton = new ToolStripButton("Оригинал") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
      retranslateButton = new ToolStripButton("Перевести заново") { DisplayStyle = ToolStripItemDisplayStyle.Text };
      cancelButton = new ToolStripButton("Отмена") { DisplayStyle = ToolStripItemDisplayStyle.Text, Visible = false };
      translationSettingsButton = new ToolStripButton("Настройки") { DisplayStyle = ToolStripItemDisplayStyle.Text };
      translationStatus = new ToolStripLabel();
      translationIndicator = new ToolStripLabel("◐") { Name = "translationIndicator", AccessibleName = "Выполняется перевод", Visible = false, ToolTipText = "Ожидание ответа модели" };
      if (components == null) components = new System.ComponentModel.Container();
      translationAnimation = new System.Windows.Forms.Timer(components) { Interval = 100 };
      translationAnimation.Tick += (_, __) => { translationAnimationFrame = (translationAnimationFrame + 1) % 4; translationIndicator.Text = "◐◓◑◒"[translationAnimationFrame].ToString(); };
      tbPreview.GripStyle = ToolStripGripStyle.Hidden;
      tbPreview.Items.AddRange(new ToolStripItem[] { translateButton, translationIndicator, originalButton, retranslateButton, cancelButton, translationSettingsButton, translationStatus });
      translateButton.Click += async (_, __) => await TranslateCurrentAsync();
      retranslateButton.Click += async (_, __) => {
        if (MessageBox.Show(this, "Заменить черновик новым переводом? Сначала сохрани нужные правки через «Сохранить как…».",
          "Перевести заново", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
          await RunTranslationAsync(true);
      };
      originalButton.Click += async (_, __) => {
        CancelTranslation();
        IsTranslationPreview = false;
        translationStatus.Text = "";
        UpdateTranslationButtons();
        await RenderPreviewAsync();
      };
      cancelButton.Click += async (_, __) => {
        CancelTranslation();
        var showingTranslation = IsTranslationPreview;
        IsTranslationPreview = false;
        translationStatus.Text = "Перевод отменён";
        UpdateTranslationButtons();
        if (showingTranslation) await RenderPreviewAsync();
      };
      translationSettingsButton.Click += (_, __) => {
        if (!translationSettingsButton.Enabled) return;
        translationSettingsButton.Enabled = false;
        // Return from UI Automation's Invoke before starting a modal message loop.
        BeginInvoke(new Action(() => {
          try { if (!_disposed) TranslationSettingsRequested?.Invoke(this, EventArgs.Empty); }
          finally { if (!_disposed) translationSettingsButton.Enabled = true; }
        }));
      };
      UpdateTranslationButtons();
    }

    private void UpdateTranslationSettings(Settings settings)
    {
      if (!settings.Translation.ShowButtons || TranslationCache.Key("", translationOptions) != TranslationCache.Key("", settings.Translation)) {
        CancelTranslation();
        IsTranslationPreview = false;
        translationStatus.Text = "";
      }
      translationOptions = settings.Translation.Copy();
      forceRender = true;
      UpdateTranslationButtons();
    }

    private async Task UpdateSourceAsync(string text, string path)
    {
      if (sourceText != text || sourcePath != path) {
        CancelTranslation();
        var differentFile = sourcePath != path;
        sourceText = text;
        sourcePath = path;
        editorRevision++;
        if (sourceIdentity == null) sourceIdentity = Guid.NewGuid().ToString("N");
        if (differentFile && !managedSourceIdentity) { translatedText = null; IsTranslationPreview = false; }
        translationStatus.Text = IsTranslationPreview ? "Исходник изменён · перевод редактируется отдельно" : "";
        UpdateTranslationButtons();
      }
      await RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
      await renderGate.WaitAsync();
      try {
        if (_disposed || _renderContent == null || sourcePath == null) return;
        var text = IsTranslationPreview ? translatedText : sourceText;
        if (!forceRender && renderedText == text && renderedPath == sourcePath && renderedTranslation == IsTranslationPreview) return;
        var path = sourcePath;
        var translated = IsTranslationPreview;
        forceRender = false;
        ResetEditorToolbar(GetEditorSnapshot().Session);
        if (_webView != null) _webView.EditorSnapshot = GetEditorSnapshot();
        await _renderContent(text, path, translated);
        renderedText = text;
        renderedPath = path;
        renderedTranslation = translated;
      }
      finally { renderGate.Release(); }
    }

    private Task TranslateCurrentAsync() => RunTranslationAsync(false);

    private async Task RunTranslationAsync(bool regenerate)
    {
      if (translationCancellation != null || string.IsNullOrWhiteSpace(sourceText)) return;
      if (!regenerate && translatedText != null && translationIdentity != null) {
        IsTranslationPreview = true;
        translationStatus.Text = "Перевод · черновик";
        UpdateTranslationButtons();
        await RenderPreviewAsync();
        return;
      }
      var text = sourceText;
      var path = sourcePath;
      var options = translationOptions.Copy();
      var key = TranslationCache.Key(text, options);
      var cancellation = new CancellationTokenSource();
      translationCancellation = cancellation;
      UpdateTranslationButtons();
      var modelLabel = CliModel.CleanDisplayName(options.UseApi ? options.ActiveApiConnection?.Model ?? "модель API" : options.UseDefaultModel ? "модель CLI" : options.Model);
      translationStatus.Text = "Перевод… " + modelLabel;
      var completedParts = 0;
      var acceptingProgress = true;
      var progress = new Progress<TranslationProgress>(value => {
        if (!acceptingProgress || _disposed || !ReferenceEquals(translationCancellation, cancellation) || cancellation.IsCancellationRequested || sourceText != text || sourcePath != path) return;
        completedParts = Math.Max(completedParts, value.Completed);
        translationStatus.Text = value.RetryPart > 0
          ? "Повтор части " + value.RetryPart + " · попытка " + value.RetryAttempt + " из " + value.RetryMaxAttempts
            + (value.Total > 1 ? " · готово " + completedParts + " из " + value.Total : "") + " · " + modelLabel
          : value.Total > 1 ? "Перевод… " + completedParts + " из " + value.Total + " частей · " + modelLabel : "Перевод… " + modelLabel;
      });
      try {
        string result = null;
        var cached = !regenerate && translationCache.TryGet(key, out result);
        if (!cached) {
          result = await new CliTranslator().TranslateAsync(text, options, cancellation.Token, progress);
          acceptingProgress = false;
          translationCache.Add(key, result);
        }
        acceptingProgress = false;
        if (_disposed || cancellation.IsCancellationRequested || sourceText != text || sourcePath != path) return;
        translatedText = result;
        translatedSourcePath = path;
        translationIdentity = Guid.NewGuid().ToString("N");
        editorRevision++;
        IsTranslationPreview = true;
        translationStatus.Text = cached ? "Русский · из кэша" : "Русский · " + modelLabel;
        await RenderPreviewAsync();
      }
      catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { acceptingProgress = false; }
      catch (Exception error) {
        acceptingProgress = false;
        if (!_disposed && !cancellation.IsCancellationRequested) {
          translationStatus.Text = "Ошибка перевода";
          MessageBox.Show(this, error.Message, "Перевод Markdown", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      }
      finally {
        acceptingProgress = false;
        if (ReferenceEquals(translationCancellation, cancellation)) translationCancellation = null;
        cancellation.Dispose();
        if (!_disposed) UpdateTranslationButtons();
      }
    }

    private void CancelTranslation()
    {
      var cancellation = translationCancellation;
      translationCancellation = null;
      cancellation?.Cancel();
      translationAnimation?.Stop();
    }

    private void UpdateTranslationButtons()
    {
      var visible = translationOptions.ShowButtons;
      translateButton.Visible = originalButton.Visible = translationStatus.Visible = visible;
      var busy = translationCancellation != null;
      translationIndicator.Visible = visible && busy;
      if (visible && busy) translationAnimation.Start(); else translationAnimation.Stop();
      var extension = Path.GetExtension(sourcePath);
      var markdown = string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".mdc", StringComparison.OrdinalIgnoreCase);
      translateButton.Enabled = !busy && !string.IsNullOrWhiteSpace(sourceText) && markdown;
      translateButton.Checked = IsTranslationPreview;
      translateButton.Text = translatedText == null ? "Перевести" : "Перевод";
      retranslateButton.Visible = visible && translatedText != null;
      retranslateButton.Enabled = !busy && markdown && !string.IsNullOrWhiteSpace(sourceText);
      originalButton.Enabled = IsTranslationPreview || busy;
      cancelButton.Visible = visible && busy;
    }
  }
}
