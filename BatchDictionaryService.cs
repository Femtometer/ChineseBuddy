using System.Runtime.CompilerServices;

namespace ChineseBuddy;

public sealed record DictionaryLookupResult(string Character, string Pinyin, List<string> Words, Exception? Error);

public static class BatchDictionaryService
{
    public const int DefaultMaxConcurrency = 1;
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    public static async IAsyncEnumerable<DictionaryLookupResult> RunAsync(
        IReadOnlyList<string> characters,
        Func<string, CancellationToken, Task<(string Pinyin, List<string> Words)>> lookup,
        int maxConcurrency,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        Func<string, string, List<string>, bool>? shouldRetry = null,
        TimeSpan? retryDelay = null)
    {
        if (retryDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryDelay));
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<DictionaryLookupResult>>();
        int next = 0;
        try
        {
            while (next < characters.Count && pending.Count < maxConcurrency)
                pending.Add(LookupOneAsync(characters[next++], lookup, linked.Token, shouldRetry, retryDelay ?? DefaultRetryDelay));
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).WaitAsync(cancellationToken);
                var result = await finished;
                pending.Remove(finished);
                cancellationToken.ThrowIfCancellationRequested();
                if (next < characters.Count) pending.Add(LookupOneAsync(characters[next++], lookup, linked.Token, shouldRetry, retryDelay ?? DefaultRetryDelay));
                yield return result;
            }
        }
        finally
        {
            linked.Cancel();
            await Task.WhenAll(pending);
        }
    }

    private static async Task<DictionaryLookupResult> LookupOneAsync(string character, Func<string, CancellationToken, Task<(string Pinyin, List<string> Words)>> lookup, CancellationToken cancellationToken, Func<string, string, List<string>, bool>? shouldRetry, TimeSpan retryDelay)
    {
        string pinyin = "";
        List<string> words = [];
        Exception? error = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) return new(character, pinyin, words, new OperationCanceledException(cancellationToken));
            try
            {
                if (attempt > 0) await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                var result = await lookup(character, cancellationToken);
                if (string.IsNullOrWhiteSpace(pinyin) && !string.IsNullOrWhiteSpace(result.Pinyin)) pinyin = result.Pinyin;
                if (words.Count == 0 && result.Words.Count > 0) words = result.Words;
                error = null;
                if (shouldRetry == null || !shouldRetry(character, pinyin, words)) break;
            }
            catch (Exception ex)
            {
                error = ex;
                if (cancellationToken.IsCancellationRequested) break;
            }
        }
        if (error != null && (pinyin.Length > 0 || words.Count > 0)) error = null;
        return new(character, pinyin, words, error);
    }
}
