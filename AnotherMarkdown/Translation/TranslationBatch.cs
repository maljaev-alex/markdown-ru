using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace AnotherMarkdown.Translation
{
  public sealed class TranslationProgress
  {
    public int Completed { get; }
    public int Total { get; }
    public TranslationProgress(int completed, int total) { Completed = completed; Total = total; }
  }

  public static class TranslationBatch
  {
    public static async Task<string> RunAsync(IReadOnlyList<TranslationChunk> chunks, int parallelRequests,
      Func<TranslationChunk, CancellationToken, Task<string>> translate, CancellationToken token,
      IProgress<TranslationProgress> progress = null)
    {
      if (chunks == null || chunks.Count == 0) throw new ArgumentException("Нет частей для перевода.");
      if (parallelRequests < 1 || parallelRequests > 8) throw new ArgumentOutOfRangeException(nameof(parallelRequests));
      if (translate == null) throw new ArgumentNullException(nameof(translate));
      token.ThrowIfCancellationRequested();
      var results = new string[chunks.Count];
      var next = -1; var completed = 0; var characters = 0; var progressGate = new object();
      Exception failure = null;
      using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token)) {
        progress?.Report(new TranslationProgress(0, chunks.Count));
        var workers = Enumerable.Range(0, Math.Min(parallelRequests, chunks.Count)).Select(_ => Task.Run(async () => {
          try {
            while (true) {
              cancellation.Token.ThrowIfCancellationRequested();
              var index = Interlocked.Increment(ref next);
              if (index >= chunks.Count) return;
              var result = await translate(chunks[index], cancellation.Token).ConfigureAwait(false);
              cancellation.Token.ThrowIfCancellationRequested();
              result = result?.TrimStart('\uFEFF', '\r', '\n').TrimEnd('\r', '\n');
              if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("Модель вернула пустой перевод части " + (index + 1) + ".");
              if (Interlocked.Add(ref characters, result.Length + chunks[index].PrefixBefore.Length + chunks[index].SeparatorAfter.Length) > 32000000)
                throw new InvalidOperationException("Итоговый перевод слишком большой (более 32 млн символов).");
              results[index] = result;
              lock (progressGate) { completed++; progress?.Report(new TranslationProgress(completed, chunks.Count)); }
            }
          }
          catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
          catch (Exception error) {
            Interlocked.CompareExchange(ref failure, error, null);
            cancellation.Cancel();
          }
        })).ToArray();
        // Wait for every worker to release its HTTP request/process before returning an error.
        await Task.WhenAll(workers).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
      }
      if (chunks.Count == 1) return results[0];
      return string.Concat(chunks.Select((chunk, index) => chunk.PrefixBefore + results[index] + chunk.SeparatorAfter));
    }
  }
}
