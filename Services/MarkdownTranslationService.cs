using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MdTranslatorViewer.Services;

internal sealed class MarkdownTranslationService
{
    private const int CacheFormatVersion = 1;
    private const int MaxBatchLength = 12000;
    private const int MaxChunkLength = 8000;
    private const int MaxConcurrentRequests = 4;
    private const int SeparatorOverhead = 64;
    private const string TranslationEndpoint = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&dt=t";

    private static readonly Regex InlineCodeRegex = new("`[^`]+`", RegexOptions.Compiled);
    private static readonly Regex LinkTargetRegex = new(@"\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex RawUrlRegex = new(@"https?://[^\s)>]+", RegexOptions.Compiled);
    private static readonly Regex TableSeparatorRegex = new(@"^\s*\|?[\s:-]+(?:\|[\s:-]+)+\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex ReferenceLinkRegex = new(@"^(\s*\[[^\]]+\]:\s+)(\S+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex PrefixedLineRegex = new(
        @"^(\s*(?:>\s*)*)(#{1,6}\s+|[-+*]\s+|\d+\.\s+|[-+*]\s+\[[ xX]\]\s+)?(.*)$",
        RegexOptions.Compiled);
    private static readonly Regex ChunkSplitRegex = new(@"(?<=\n)|(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _documentCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _plainTextCache = new(StringComparer.Ordinal);
    private readonly string _cacheDirectory;

    public MarkdownTranslationService()
    {
        _cacheDirectory = AppStoragePaths.TranslationCacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<string> TranslateMarkdownAsync(string markdown, string targetLanguage, CancellationToken cancellationToken)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        var cacheKey = BuildDocumentCacheKey(normalized, targetLanguage);
        if (_documentCache.TryGetValue(cacheKey, out var cachedDocument))
        {
            return cachedDocument;
        }

        var diskCachedDocument = await TryReadDocumentCacheAsync(cacheKey, cancellationToken);
        if (diskCachedDocument is not null)
        {
            _documentCache[cacheKey] = diskCachedDocument;
            return diskCachedDocument;
        }

        var lines = normalized.Split('\n');
        var outputParts = new List<OutputPart>(lines.Length);
        var translationUnits = new List<string>();
        var paragraphBuffer = new List<string>();

        var inFence = false;
        var fenceMarker = string.Empty;
        var inFrontMatter = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();

            if (index == 0 && trimmed == "---")
            {
                inFrontMatter = true;
                outputParts.Add(new RawLinePart(line));
                continue;
            }

            if (inFrontMatter)
            {
                outputParts.Add(new RawLinePart(line));
                if (trimmed == "---")
                {
                    inFrontMatter = false;
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) ||
                trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);

                var marker = trimmed[..3];
                if (!inFence)
                {
                    inFence = true;
                    fenceMarker = marker;
                }
                else if (marker == fenceMarker)
                {
                    inFence = false;
                    fenceMarker = string.Empty;
                }

                outputParts.Add(new RawLinePart(line));
                continue;
            }

            if (inFence || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);
                outputParts.Add(new RawLinePart(line));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);
                outputParts.Add(new RawLinePart(line));
                continue;
            }

            if (TableSeparatorRegex.IsMatch(line))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);
                outputParts.Add(new RawLinePart(line));
                continue;
            }

            if (ReferenceLinkRegex.IsMatch(line))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);
                outputParts.Add(BuildReferenceLinePart(line, translationUnits));
                continue;
            }

            if (LooksLikeTableRow(line))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);
                outputParts.Add(BuildTableRowPart(line, translationUnits));
                continue;
            }

            if (IsSpecialPrefixedLine(line))
            {
                FlushParagraph(paragraphBuffer, outputParts, translationUnits);
                outputParts.Add(BuildPrefixedLinePart(line, translationUnits));
                continue;
            }

            paragraphBuffer.Add(line);
        }

        FlushParagraph(paragraphBuffer, outputParts, translationUnits);

        var translatedUnits = await TranslateUnitsAsync(translationUnits, targetLanguage, cancellationToken);
        var output = new List<string>(outputParts.Count);
        foreach (var part in outputParts)
        {
            part.Append(output, translatedUnits);
        }

        var translatedDocument = string.Join(Environment.NewLine, output);
        _documentCache[cacheKey] = translatedDocument;
        await WriteDocumentCacheAsync(cacheKey, translatedDocument, cancellationToken);
        return translatedDocument;
    }

    public async Task<string?> TryGetCachedTranslationAsync(
        string markdown,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        var cacheKey = BuildDocumentCacheKey(normalized, targetLanguage);
        if (_documentCache.TryGetValue(cacheKey, out var cachedDocument))
        {
            return cachedDocument;
        }

        var diskCachedDocument = await TryReadDocumentCacheAsync(cacheKey, cancellationToken);
        if (diskCachedDocument is null)
        {
            return null;
        }

        _documentCache[cacheKey] = diskCachedDocument;
        return diskCachedDocument;
    }

    public async Task<string> TranslatePlainTextAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        var cacheKey = BuildPlainTextCacheKey(normalized, targetLanguage);
        if (_plainTextCache.TryGetValue(cacheKey, out var cachedTranslation))
        {
            return cachedTranslation;
        }

        var chunks = SplitIntoChunks(normalized, MaxChunkLength).ToArray();
        var translatedChunks = new string[chunks.Length];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(MaxConcurrentRequests, chunks.Length),
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Length), parallelOptions, async (index, ct) =>
        {
            translatedChunks[index] = await TranslateChunkAsync(chunks[index], targetLanguage, ct);
        });

        var translatedText = string.Concat(translatedChunks);
        _plainTextCache[cacheKey] = translatedText;
        return translatedText;
    }

    private static bool IsSpecialPrefixedLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('#') ||
               trimmed.StartsWith('>') ||
               trimmed.StartsWith("- ") ||
               trimmed.StartsWith("* ") ||
               trimmed.StartsWith("+ ") ||
               Regex.IsMatch(trimmed, @"^\d+\.\s+") ||
               Regex.IsMatch(trimmed, @"^[-+*]\s+\[[ xX]\]\s+") ||
               trimmed.StartsWith("<!--", StringComparison.Ordinal);
    }

    private static bool LooksLikeTableRow(string line)
    {
        return line.Contains('|') &&
               !line.Contains("http://", StringComparison.OrdinalIgnoreCase) &&
               !line.Contains("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static void FlushParagraph(
        List<string> paragraphBuffer,
        List<OutputPart> outputParts,
        List<string> translationUnits)
    {
        if (paragraphBuffer.Count == 0)
        {
            return;
        }

        var translationIndex = translationUnits.Count;
        translationUnits.Add(string.Join('\n', paragraphBuffer));
        outputParts.Add(new ParagraphPart(translationIndex));
        paragraphBuffer.Clear();
    }

    private static OutputPart BuildReferenceLinePart(string line, List<string> translationUnits)
    {
        var match = ReferenceLinkRegex.Match(line);
        if (!match.Success)
        {
            return new RawLinePart(line);
        }

        var suffix = match.Groups[3].Value;
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return new RawLinePart(line);
        }

        var suffixIndex = translationUnits.Count;
        translationUnits.Add(suffix);
        return new ReferenceLinePart(match.Groups[1].Value, match.Groups[2].Value, suffixIndex);
    }

    private static OutputPart BuildTableRowPart(string line, List<string> translationUnits)
    {
        var trimmed = line.Trim();
        if (TableSeparatorRegex.IsMatch(trimmed))
        {
            return new RawLinePart(line);
        }

        var leadingPipe = trimmed.StartsWith('|');
        var trailingPipe = trimmed.EndsWith('|');
        var content = leadingPipe && trailingPipe ? trimmed[1..^1] : trimmed;
        var cells = content.Split('|');
        var cellIndices = new int[cells.Length];

        for (var index = 0; index < cells.Length; index++)
        {
            cellIndices[index] = translationUnits.Count;
            translationUnits.Add(cells[index]);
        }

        var leadingWhitespace = line[..(line.Length - line.TrimStart().Length)];
        var trailingWhitespace = line[(line.TrimEnd().Length)..];
        return new TableRowPart(leadingWhitespace, trailingWhitespace, leadingPipe, trailingPipe, cellIndices);
    }

    private static OutputPart BuildPrefixedLinePart(string line, List<string> translationUnits)
    {
        var match = PrefixedLineRegex.Match(line);
        if (!match.Success)
        {
            var fallbackIndex = translationUnits.Count;
            translationUnits.Add(line);
            return new PrefixedLinePart(string.Empty, fallbackIndex);
        }

        var prefix = match.Groups[1].Value + match.Groups[2].Value;
        var content = match.Groups[3].Value;
        if (string.IsNullOrWhiteSpace(content))
        {
            return new RawLinePart(line);
        }

        var translationIndex = translationUnits.Count;
        translationUnits.Add(content);
        return new PrefixedLinePart(prefix, translationIndex);
    }

    private async Task<string[]> TranslateUnitsAsync(
        IReadOnlyList<string> units,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var translatedUnits = new string[units.Count];
        var pendingUnits = new List<PendingTranslationUnit>();
        var firstIndexByText = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicateAssignments = new List<DuplicateAssignment>();

        for (var index = 0; index < units.Count; index++)
        {
            var text = units[index];
            if (string.IsNullOrWhiteSpace(text))
            {
                translatedUnits[index] = text;
                continue;
            }

            if (_cache.TryGetValue(text, out var cached))
            {
                translatedUnits[index] = cached;
                continue;
            }

            if (firstIndexByText.TryGetValue(text, out var firstIndex))
            {
                duplicateAssignments.Add(new DuplicateAssignment(index, firstIndex));
                continue;
            }

            firstIndexByText[text] = index;
            var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
            var protectedText = ProtectSegments(text, placeholders);
            pendingUnits.Add(new PendingTranslationUnit(index, text, protectedText, placeholders));
        }

        var batches = CreateTranslationBatches(pendingUnits).ToArray();
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = MaxConcurrentRequests,
        };

        await Parallel.ForEachAsync(batches, parallelOptions, async (batch, ct) =>
        {
            await TranslateBatchAsync(batch, translatedUnits, targetLanguage, ct);
        });

        foreach (var assignment in duplicateAssignments)
        {
            translatedUnits[assignment.Index] = translatedUnits[assignment.SourceIndex];
        }

        return translatedUnits;
    }

    private async Task<string> TranslateProtectedTextAsync(
        string originalText,
        string protectedText,
        IReadOnlyDictionary<string, string> placeholders,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var chunks = SplitIntoChunks(protectedText, MaxChunkLength).ToArray();
        var translatedChunks = new string[chunks.Length];
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(MaxConcurrentRequests, chunks.Length),
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, chunks.Length), parallelOptions, async (index, ct) =>
        {
            translatedChunks[index] = await TranslateChunkAsync(chunks[index], targetLanguage, ct);
        });

        var translatedBuilder = new StringBuilder();
        for (var index = 0; index < translatedChunks.Length; index++)
        {
            translatedBuilder.Append(translatedChunks[index]);
        }

        var translated = RestoreSegments(translatedBuilder.ToString(), placeholders);
        _cache[originalText] = translated;
        return translated;
    }

    private async Task TranslateBatchAsync(
        IReadOnlyList<PendingTranslationUnit> batch,
        string[] translatedUnits,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        if (batch.Count == 1)
        {
            var unit = batch[0];
            translatedUnits[unit.Index] = await TranslateProtectedTextAsync(
                unit.OriginalText,
                unit.ProtectedText,
                unit.Placeholders,
                targetLanguage,
                cancellationToken);
            return;
        }

        var separator = $"\n@@MDVSEP_{Guid.NewGuid():N}@@\n";
        var joinedText = string.Join(separator, batch.Select(unit => unit.ProtectedText));
        var translatedJoined = await TranslateChunkAsync(joinedText, targetLanguage, cancellationToken);
        var translatedParts = translatedJoined.Split(separator, StringSplitOptions.None);

        if (translatedParts.Length != batch.Count)
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(MaxConcurrentRequests, batch.Count),
            };

            await Parallel.ForEachAsync(batch, parallelOptions, async (unit, ct) =>
            {
                translatedUnits[unit.Index] = await TranslateProtectedTextAsync(
                    unit.OriginalText,
                    unit.ProtectedText,
                    unit.Placeholders,
                    targetLanguage,
                    ct);
            });

            return;
        }

        for (var index = 0; index < batch.Count; index++)
        {
            var unit = batch[index];
            var translated = RestoreSegments(translatedParts[index], unit.Placeholders);
            _cache[unit.OriginalText] = translated;
            translatedUnits[unit.Index] = translated;
        }
    }

    private static IEnumerable<List<PendingTranslationUnit>> CreateTranslationBatches(
        IReadOnlyList<PendingTranslationUnit> units)
    {
        var batch = new List<PendingTranslationUnit>();
        var batchLength = 0;

        foreach (var unit in units)
        {
            var candidateLength = batchLength + unit.ProtectedText.Length + (batch.Count == 0 ? 0 : SeparatorOverhead);
            if (batch.Count > 0 && candidateLength > MaxBatchLength)
            {
                yield return batch;
                batch = new List<PendingTranslationUnit>();
                batchLength = 0;
            }

            batch.Add(unit);
            batchLength += unit.ProtectedText.Length + (batch.Count == 1 ? 0 : SeparatorOverhead);
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static string ProtectSegments(string text, Dictionary<string, string> placeholders)
    {
        var counter = 0;

        text = ProtectWithRegex(text, InlineCodeRegex, placeholders, ref counter);
        text = ProtectWithRegex(text, LinkTargetRegex, placeholders, ref counter);
        text = ProtectWithRegex(text, RawUrlRegex, placeholders, ref counter);
        return text;
    }

    private static string ProtectWithRegex(
        string text,
        Regex regex,
        Dictionary<string, string> placeholders,
        ref int counter)
    {
        var localCounter = counter;
        var replaced = regex.Replace(text, match =>
        {
            var key = $"@@KEEP_{localCounter++}@@";
            placeholders[key] = match.Value;
            return key;
        });

        counter = localCounter;
        return replaced;
    }

    private static string RestoreSegments(string text, IReadOnlyDictionary<string, string> placeholders)
    {
        var restored = text;
        foreach (var pair in placeholders)
        {
            restored = restored.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
        }

        return restored;
    }

    private static IEnumerable<string> SplitIntoChunks(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (var piece in ChunkSplitRegex.Split(text))
        {
            if (string.IsNullOrEmpty(piece))
            {
                continue;
            }

            var remaining = piece;
            while (remaining.Length > 0)
            {
                if (current.Length == maxLength)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                var availableLength = maxLength - current.Length;
                var takeLength = Math.Min(availableLength, remaining.Length);
                current.Append(remaining, 0, takeLength);
                remaining = remaining[takeLength..];

                if (current.Length == maxLength)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static async Task<string> TranslateChunkAsync(string text, string targetLanguage, CancellationToken cancellationToken)
    {
        try
        {
            return await TranslateChunkWithPostAsync(text, targetLanguage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return await TranslateChunkWithGetAsync(text, targetLanguage, cancellationToken);
        }
    }

    private static async Task<string> TranslateChunkWithPostAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var url = $"{TranslationEndpoint}&tl={WebUtility.UrlEncode(targetLanguage)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("q", text),
            ]),
        };

        using var response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await ReadTranslatedTextAsync(response, cancellationToken);
    }

    private static async Task<string> TranslateChunkWithGetAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var url = $"{TranslationEndpoint}&tl={WebUtility.UrlEncode(targetLanguage)}&q={WebUtility.UrlEncode(text)}";
        using var response = await HttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadTranslatedTextAsync(response, cancellationToken);
    }

    private static async Task<string> ReadTranslatedTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var builder = new StringBuilder();
        foreach (var item in document.RootElement[0].EnumerateArray())
        {
            if (item.GetArrayLength() > 0)
            {
                builder.Append(item[0].GetString());
            }
        }

        return builder.ToString();
    }

    private string GetCacheFilePath(string cacheKey)
    {
        return Path.Combine(_cacheDirectory, $"{cacheKey}.json");
    }

    private static string BuildDocumentCacheKey(string normalizedMarkdown, string targetLanguage)
    {
        var payload = Encoding.UTF8.GetBytes($"{CacheFormatVersion}\n{targetLanguage}\n{normalizedMarkdown}");
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }

    private static string BuildPlainTextCacheKey(string normalizedText, string targetLanguage)
    {
        var payload = Encoding.UTF8.GetBytes($"plain\n{targetLanguage}\n{normalizedText}");
        var hash = SHA256.HashData(payload);
        return Convert.ToHexString(hash);
    }

    private async Task<string?> TryReadDocumentCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(cacheKey);
        if (!File.Exists(cachePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var entry = await JsonSerializer.DeserializeAsync<DocumentTranslationCacheEntry>(
                stream,
                cancellationToken: cancellationToken);

            if (entry is null ||
                entry.Version != CacheFormatVersion ||
                string.IsNullOrWhiteSpace(entry.TranslatedMarkdown))
            {
                return null;
            }

            return entry.TranslatedMarkdown;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteDocumentCacheAsync(string cacheKey, string translatedMarkdown, CancellationToken cancellationToken)
    {
        var cachePath = GetCacheFilePath(cacheKey);
        var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        var entry = new DocumentTranslationCacheEntry
        {
            Version = CacheFormatVersion,
            TranslatedMarkdown = translatedMarkdown,
            SavedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            var json = JsonSerializer.Serialize(entry);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class DocumentTranslationCacheEntry
    {
        public int Version { get; init; }

        public string TranslatedMarkdown { get; init; } = string.Empty;

        public DateTimeOffset SavedAtUtc { get; init; }
    }

    private sealed record PendingTranslationUnit(
        int Index,
        string OriginalText,
        string ProtectedText,
        IReadOnlyDictionary<string, string> Placeholders);

    private sealed record DuplicateAssignment(int Index, int SourceIndex);

    private abstract record OutputPart
    {
        public abstract void Append(List<string> output, IReadOnlyList<string> translatedUnits);
    }

    private sealed record RawLinePart(string Line) : OutputPart
    {
        public override void Append(List<string> output, IReadOnlyList<string> translatedUnits)
        {
            output.Add(Line);
        }
    }

    private sealed record ParagraphPart(int TranslationIndex) : OutputPart
    {
        public override void Append(List<string> output, IReadOnlyList<string> translatedUnits)
        {
            output.AddRange(translatedUnits[TranslationIndex].Split('\n'));
        }
    }

    private sealed record ReferenceLinePart(string Prefix, string Target, int SuffixIndex) : OutputPart
    {
        public override void Append(List<string> output, IReadOnlyList<string> translatedUnits)
        {
            output.Add(Prefix + Target + translatedUnits[SuffixIndex]);
        }
    }

    private sealed record TableRowPart(
        string LeadingWhitespace,
        string TrailingWhitespace,
        bool LeadingPipe,
        bool TrailingPipe,
        IReadOnlyList<int> CellIndices) : OutputPart
    {
        public override void Append(List<string> output, IReadOnlyList<string> translatedUnits)
        {
            var cells = CellIndices.Select(index => translatedUnits[index]);
            var rebuilt = string.Join('|', cells);
            if (LeadingPipe)
            {
                rebuilt = "|" + rebuilt;
            }

            if (TrailingPipe)
            {
                rebuilt += "|";
            }

            output.Add(LeadingWhitespace + rebuilt + TrailingWhitespace);
        }
    }

    private sealed record PrefixedLinePart(string Prefix, int TranslationIndex) : OutputPart
    {
        public override void Append(List<string> output, IReadOnlyList<string> translatedUnits)
        {
            output.Add(Prefix + translatedUnits[TranslationIndex]);
        }
    }
}
