using System.Buffers;
using System.IO;
using System.Text;

namespace TenhouCsvReader.Services;

internal readonly record struct CsvFilterIndexProgress(long ScannedRows, long MatchedRows, bool IsCompleted);

internal sealed class CsvFilteredPagedReader : IAsyncDisposable
{
    private readonly string _path;
    private readonly int _pageSize;
    private readonly long _dataStartOffset;
    private readonly long _fileLength;
    private readonly Encoding _encoding;
    private readonly IReadOnlyList<CsvFilterCondition> _conditions;
    private readonly List<long> _matchedRowOffsets = new();
    private readonly Dictionary<long, string[]> _rowCache = new();
    private readonly Queue<long> _rowCacheOrder = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private long _scanOffset;
    private long _scannedRows;
    private long _matchedRows;
    private bool _scanComplete;

    public CsvFilteredPagedReader(
        string path,
        Encoding encoding,
        long dataStartOffset,
        long fileLength,
        int pageSize,
        IReadOnlyList<CsvFilterCondition> conditions)
    {
        _path = path;
        _encoding = encoding;
        _dataStartOffset = dataStartOffset;
        _fileLength = fileLength;
        _pageSize = pageSize;
        _conditions = conditions;

        _scanOffset = dataStartOffset;
        _scanComplete = dataStartOffset >= fileLength;
    }

    public long ScannedRows => Interlocked.Read(ref _scannedRows);

    public long MatchedRows => Interlocked.Read(ref _matchedRows);

    public bool IsScanComplete => _scanComplete;

    public async Task<CsvPageData> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        var startMatchIndex = (long)pageIndex * _pageSize;
        var requiredMatches = startMatchIndex + _pageSize + 1;

        await EnsureMatchesAsync(requiredMatches, cancellationToken);

        List<long> offsets;
        bool hasNextPage;
        long? totalMatches;

        await _scanGate.WaitAsync(cancellationToken);

        try
        {
            if (_scanComplete && _matchedRowOffsets.Count == 0 && pageIndex == 0)
            {
                offsets = new List<long>(0);
                hasNextPage = false;
                totalMatches = 0;

                return new CsvPageData(
                    PageIndex: 0,
                    StartRowNumber: 0,
                    EndRowNumber: 0,
                    Rows: Array.Empty<string[]>(),
                    HasPreviousPage: false,
                    HasNextPage: false,
                    TotalRows: 0);
            }

            if (_scanComplete && startMatchIndex >= _matchedRowOffsets.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page index is out of range.");
            }

            var rowsInPage = (int)Math.Min(_pageSize, Math.Max(0, _matchedRowOffsets.Count - startMatchIndex));
            offsets = new List<long>(rowsInPage);

            for (var index = 0; index < rowsInPage; index++)
            {
                offsets.Add(_matchedRowOffsets[(int)(startMatchIndex + index)]);
            }

            if (_scanComplete)
            {
                hasNextPage = startMatchIndex + rowsInPage < _matchedRowOffsets.Count;
                totalMatches = _matchedRowOffsets.Count;
            }
            else
            {
                hasNextPage = true;
                totalMatches = null;
            }
        }
        finally
        {
            _scanGate.Release();
        }

        var rows = await ReadRowsByOffsetsAsync(offsets, cancellationToken);

        var startRowNumber = offsets.Count == 0 ? 0 : startMatchIndex + 1;
        var endRowNumber = offsets.Count == 0 ? 0 : startMatchIndex + offsets.Count;

        return new CsvPageData(
            PageIndex: pageIndex,
            StartRowNumber: startRowNumber,
            EndRowNumber: endRowNumber,
            Rows: rows,
            HasPreviousPage: pageIndex > 0,
            HasNextPage: hasNextPage,
            TotalRows: totalMatches);
    }

    public async Task BuildFullIndexAsync(IProgress<CsvFilterIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_scanComplete)
            {
                progress?.Report(new CsvFilterIndexProgress(ScannedRows, MatchedRows, true));
                return;
            }

            await ScanToNextPageBoundaryAsync(cancellationToken);
            progress?.Report(new CsvFilterIndexProgress(ScannedRows, MatchedRows, _scanComplete));

            if (_scanComplete)
            {
                return;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _scanGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureMatchesAsync(long requiredMatches, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasEnoughMatches = false;
            var completed = false;

            await _scanGate.WaitAsync(cancellationToken);

            try
            {
                hasEnoughMatches = _matchedRowOffsets.Count >= requiredMatches;
                completed = _scanComplete;

                if (hasEnoughMatches || completed)
                {
                    return;
                }
            }
            finally
            {
                _scanGate.Release();
            }

            await ScanToMatchCountAsync(requiredMatches, cancellationToken);
        }
    }

    private async Task ScanToNextPageBoundaryAsync(CancellationToken cancellationToken)
    {
        var nextBoundary = ((MatchedRows / _pageSize) + 1) * _pageSize;
        var required = nextBoundary + 1;
        await ScanToMatchCountAsync(required, cancellationToken);
    }

    private async Task ScanToMatchCountAsync(long requiredMatches, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);

        try
        {
            if (_scanComplete || _matchedRowOffsets.Count >= requiredMatches)
            {
                return;
            }

            await using var stream = OpenReadStream(FileOptions.SequentialScan);
            stream.Seek(_scanOffset, SeekOrigin.Begin);

            var chunk = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            var lineBuffer = new ArrayBufferWriter<byte>(1024);
            var currentLineStartOffset = _scanOffset;

            try
            {
                while (true)
                {
                    var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken);

                    if (bytesRead == 0)
                    {
                        if (lineBuffer.WrittenCount > 0)
                        {
                            ProcessLine(lineBuffer.WrittenSpan, currentLineStartOffset);
                            lineBuffer.Clear();
                        }

                        _scanOffset = stream.Position;
                        _scanComplete = true;
                        return;
                    }

                    var span = chunk.AsSpan(0, bytesRead);
                    var chunkStartOffset = stream.Position - bytesRead;
                    var segmentStart = 0;

                    for (var offset = 0; offset < bytesRead; offset++)
                    {
                        if (span[offset] != (byte)'\n')
                        {
                            continue;
                        }

                        var sliceLength = offset - segmentStart;
                        if (sliceLength > 0)
                        {
                            lineBuffer.Write(span.Slice(segmentStart, sliceLength));
                        }

                        ProcessLine(lineBuffer.WrittenSpan, currentLineStartOffset);
                        lineBuffer.Clear();

                        currentLineStartOffset = chunkStartOffset + offset + 1;
                        segmentStart = offset + 1;

                        if (_matchedRowOffsets.Count >= requiredMatches)
                        {
                            _scanOffset = currentLineStartOffset;
                            return;
                        }
                    }

                    if (segmentStart < bytesRead)
                    {
                        lineBuffer.Write(span[segmentStart..bytesRead]);
                    }

                    _scanOffset = stream.Position;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private void ProcessLine(ReadOnlySpan<byte> lineBytes, long rowOffset)
    {
        Interlocked.Increment(ref _scannedRows);

        var parsedRow = ParseLine(lineBytes);

        if (!CsvFilterParser.IsMatch(parsedRow, _conditions))
        {
            return;
        }

        _matchedRowOffsets.Add(rowOffset);
        Interlocked.Increment(ref _matchedRows);

        // Keep a bounded hot cache so page transitions around the current area stay snappy.
        AddToCache(rowOffset, parsedRow.ToArray());
    }

    private async Task<IReadOnlyList<string[]>> ReadRowsByOffsetsAsync(IReadOnlyList<long> offsets, CancellationToken cancellationToken)
    {
        if (offsets.Count == 0)
        {
            return Array.Empty<string[]>();
        }

        var result = new List<string[]>(offsets.Count);
        var missingOffsets = new List<long>();

        await _scanGate.WaitAsync(cancellationToken);

        try
        {
            foreach (var offset in offsets)
            {
                if (_rowCache.TryGetValue(offset, out var row))
                {
                    result.Add(row);
                }
                else
                {
                    missingOffsets.Add(offset);
                }
            }
        }
        finally
        {
            _scanGate.Release();
        }

        Dictionary<long, string[]>? loadedRows = null;

        if (missingOffsets.Count > 0)
        {
            loadedRows = await LoadMissingRowsAsync(missingOffsets, cancellationToken);

            await _scanGate.WaitAsync(cancellationToken);

            try
            {
                foreach (var pair in loadedRows)
                {
                    AddToCache(pair.Key, pair.Value);
                }
            }
            finally
            {
                _scanGate.Release();
            }
        }

        result.Clear();

        await _scanGate.WaitAsync(cancellationToken);

        try
        {
            foreach (var offset in offsets)
            {
                if (_rowCache.TryGetValue(offset, out var row))
                {
                    result.Add(row);
                }
            }
        }
        finally
        {
            _scanGate.Release();
        }

        return result;
    }

    private async Task<Dictionary<long, string[]>> LoadMissingRowsAsync(IReadOnlyList<long> offsets, CancellationToken cancellationToken)
    {
        var rowsByOffset = new Dictionary<long, string[]>(offsets.Count);

        await using var stream = OpenReadStream(FileOptions.RandomAccess);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            foreach (var offset in offsets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                stream.Seek(offset, SeekOrigin.Begin);
                var lineBytes = await ReadLineBytesAsync(stream, buffer, cancellationToken);
                rowsByOffset[offset] = ParseLine(lineBytes.Span).ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return rowsByOffset;
    }

    private async Task<ReadOnlyMemory<byte>> ReadLineBytesAsync(FileStream stream, byte[] tempBuffer, CancellationToken cancellationToken)
    {
        var lineBuffer = new ArrayBufferWriter<byte>(512);

        while (true)
        {
            var bytesRead = await stream.ReadAsync(tempBuffer.AsMemory(0, tempBuffer.Length), cancellationToken);

            if (bytesRead == 0)
            {
                return lineBuffer.WrittenMemory;
            }

            var span = tempBuffer.AsSpan(0, bytesRead);
            var newLineIndex = span.IndexOf((byte)'\n');

            if (newLineIndex >= 0)
            {
                if (newLineIndex > 0)
                {
                    lineBuffer.Write(span[..newLineIndex]);
                }

                return lineBuffer.WrittenMemory;
            }

            lineBuffer.Write(span);
        }
    }

    private IReadOnlyList<string> ParseLine(ReadOnlySpan<byte> lineBytes)
    {
        if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
        {
            lineBytes = lineBytes[..^1];
        }

        var line = _encoding.GetString(lineBytes);
        return CsvLineParser.Parse(line);
    }

    private void AddToCache(long offset, string[] row)
    {
        if (_rowCache.ContainsKey(offset))
        {
            return;
        }

        _rowCache[offset] = row;
        _rowCacheOrder.Enqueue(offset);

        const int cacheLimit = 20_000;

        while (_rowCacheOrder.Count > cacheLimit)
        {
            var victimOffset = _rowCacheOrder.Dequeue();
            _rowCache.Remove(victimOffset);
        }
    }

    private FileStream OpenReadStream(FileOptions options)
    {
        return new FileStream(
            path: _path,
            mode: FileMode.Open,
            access: FileAccess.Read,
            share: FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            options: options);
    }
}
