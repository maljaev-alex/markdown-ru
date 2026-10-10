using System;
using System.Threading;
using System.Threading.Tasks;

namespace AnotherMarkdown.Translation
{
  // Messages supplied here describe a validation rule, never the model output.
  internal sealed class MarkdownProtectionException : InvalidOperationException
  {
    internal MarkdownProtectionException(string safeMessage) : base(safeMessage) { }
    internal MarkdownProtectionException(string safeMessage, Exception innerException) : base(safeMessage, innerException) { }
  }

  internal static class ProtectedTranslation
  {
    internal const int MaximumAttempts = 3;
    private const string Correction = "A previous translation failed protected Markdown validation. " +
      "Translate the original target again. Copy every CURRENT protected marker exactly once, unchanged, " +
      "in its original structural block. Keep code blocks in source order; only inline-code markers within the same paragraph may change order. " +
      "Do not add, remove, duplicate, move to another block, quote or wrap markers. " +
      "Do not emit protected context or markers from a previous attempt. Follow the CURRENT output contract exactly, " +
      "including the JSON markdown and annotations envelope when requested. Preserve every annotation ID and AM_TERM marker exactly. " +
      "Never insert a newline or a source delimiter into an annotation slot. Do not add notes outside the required response.\n\n";

    internal static async Task<string> RunAsync(string markdown, Func<string, string> createPrompt,
      Func<string, CancellationToken, Task<string>> transport, CancellationToken token, Action<int, int> onRetry = null)
    {
      if (markdown == null) throw new ArgumentNullException(nameof(markdown));
      if (createPrompt == null) throw new ArgumentNullException(nameof(createPrompt));
      if (transport == null) throw new ArgumentNullException(nameof(transport));
      string validationRule = null;
      for (var attempt = 1; attempt <= MaximumAttempts; attempt++) {
        token.ThrowIfCancellationRequested();
        var protection = new MarkdownCodeProtection(markdown);
        var prompt = protection.Prompt(createPrompt);
        if (attempt > 1) {
          prompt = Correction + "Validation rule that failed (application diagnostic): " + validationRule + "\n\n" + prompt;
          onRetry?.Invoke(attempt, MaximumAttempts);
          token.ThrowIfCancellationRequested();
        }
        var translated = await transport(prompt, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try { return protection.Restore(translated); }
        catch (MarkdownProtectionException error) {
          // Rebuild from the original source and fresh markers on the next pass.
          // Failed output is deliberately neither retained nor sent to the model.
          token.ThrowIfCancellationRequested();
          validationRule = error.Message;
          if (attempt == MaximumAttempts)
            throw new MarkdownProtectionException("\u041f\u043e\u0441\u043b\u0435 3 \u043f\u043e\u043f\u044b\u0442\u043e\u043a \u043d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0441\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c \u0437\u0430\u0449\u0438\u0449\u0451\u043d\u043d\u044b\u0435 \u0444\u0440\u0430\u0433\u043c\u0435\u043d\u0442\u044b Markdown. \u041f\u0435\u0440\u0435\u0432\u043e\u0434 \u043d\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d. \u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 \u0434\u0440\u0443\u0433\u0443\u044e \u043c\u043e\u0434\u0435\u043b\u044c \u0438\u043b\u0438 \u043f\u043e\u0432\u0442\u043e\u0440\u0438\u0442\u0435 \u043f\u0435\u0440\u0435\u0432\u043e\u0434.", error);
        }
      }
      throw new InvalidOperationException("Protected translation exhausted its attempts.");
    }
  }
}
