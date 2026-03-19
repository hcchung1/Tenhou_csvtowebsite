using System.Buffers;
using System.IO;
using System.Text;

namespace TenhouCsvReader.Services;

internal readonly record struct CsvIndexProgress(long IndexedRows, bool IsCompleted);

internal sealed record CsvPageData(
    int PageIndex,
    long StartRowNumber,
    long EndRowNumber,
    IReadOnlyList<string[]> Rows,
    bool HasPreviousPage,
    bool HasNextPage,
    long? TotalRows);

internal sealed class LargeCsvPagedReader : IAsyncDisposable
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _path;
    private readonly int _pageSize;
    private readonly List<long> _pageStartOffsets = new();
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private volatile bool _scanComplete;
    private long _fileLength;
    private long _dataStartOffset;
    private long _indexScanOffset;
    private long _scannedRows;
    private bool _lastScannedByteWasNewLine = true;
    private bool _initialized;

    public LargeCsvPagedReader(string path, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        if (pageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
        }

        _path = path;
        _pageSize = pageSize;
    }

    public int PageSize => _pageSize;

    public IReadOnlyList<string> Headers { get; private set; } = Array.Empty<string>();

    public long IndexedRows => Interlocked.Read(ref _scannedRows);

    public bool IsIndexComplete => _scanComplete;

    public long? TotalRows => _scanComplete ? IndexedRows : null;

    public CsvFilteredPagedReader CreateFilteredReader(IReadOnlyList<CsvFilterCondition> conditions)
    {
        EnsureInitialized();

        if (conditions.Count == 0)
        {
            throw new ArgumentException("At least one filter condition is required.", nameof(conditions));
        }

        return new CsvFilteredPagedReader(
            path: _path,
            encoding: _encoding,
            dataStartOffset: _dataStartOffset,
            fileLength: _fileLength,
            pageSize: _pageSize,
            conditions: conditions);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _indexGate.WaitAsync(cancellationToken);

        try
        {
            if (_initialized)
            {
                return;
            }

            if (!File.Exists(_path))
            {
                throw new FileNotFoundException("CSV file does not exist.", _path);
            }

            _fileLength = new FileInfo(_path).Length;

            var (headerLine, dataStartOffset) = await ReadHeaderAndOffsetAsync(cancellationToken);
            var parsedHeader = CsvLineParser.Parse(headerLine);

            if (parsedHeader.Count == 0)
            {
                throw new InvalidDataException("CSV header cannot be empty.");
            }

            Headers = parsedHeader;
            _dataStartOffset = dataStartOffset;
            _indexScanOffset = dataStartOffset;
            _scannedRows = 0;
            _scanComplete = dataStartOffset >= _fileLength;
            _lastScannedByteWasNewLine = true;
            _pageStartOffsets.Clear();
            _pageStartOffsets.Add(dataStartOffset);
            _initialized = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async Task<CsvPageData> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        EnsureInitialized();

        await EnsurePageIndexedAsync(pageIndex, cancellationToken);

        long startOffset;

        await _indexGate.WaitAsync(cancellationToken);

        try
        {
            if (_scanComplete && pageIndex >= CalculateTotalPages())
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page index is out of range.");
            }

            if (pageIndex >= _pageStartOffsets.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "The page index is out of range.");
            }

            startOffset = _pageStartOffsets[pageIndex];
        }
        finally
        {
            _indexGate.Release();
        }

        var rows = await ReadPageRowsAsync(startOffset, cancellationToken);

        await EnsurePageIndexedAsync(pageIndex + 1, cancellationToken);

        bool hasNextPage;
        long? totalRows;

        await _indexGate.WaitAsync(cancellationToken);

        try
        {
            if (_scanComplete)
            {
                var totalPages = CalculateTotalPages();
                hasNextPage = pageIndex + 1 < totalPages;
                totalRows = _scannedRows;
            }
            else
            {
                hasNextPage = pageIndex + 1 < _pageStartOffsets.Count;
                totalRows = null;
            }
        }
        finally
        {
            _indexGate.Release();
        }

        var startRow = (long)pageIndex * _pageSize + 1;
        var endRow = rows.Count == 0 ? startRow : startRow + rows.Count - 1;

        return new CsvPageData(
            PageIndex: pageIndex,
            StartRowNumber: startRow,
            EndRowNumber: endRow,
            Rows: rows,
            HasPreviousPage: pageIndex > 0,
            HasNextPage: hasNextPage,
            TotalRows: totalRows);
    }

    public async Task BuildFullIndexAsync(IProgress<CsvIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_scanComplete)
            {
                progress?.Report(new CsvIndexProgress(IndexedRows, true));
                return;
            }

            await IndexUntilNextPageBoundaryAsync(cancellationToken);
            progress?.Report(new CsvIndexProgress(IndexedRows, _scanComplete));

            if (_scanComplete)
            {
                return;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _indexGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsurePageIndexedAsync(int pageIndex, CancellationToken cancellationToken)
    {
        if (pageIndex < 0)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool alreadyAvailable;

            await _indexGate.WaitAsync(cancellationToken);

            try
            {
                alreadyAvailable = pageIndex < _pageStartOffsets.Count;

                if (_scanComplete || alreadyAvailable)
                {
                    return;
                }
            }
            finally
            {
                _indexGate.Release();
            }

            await IndexUntilNextPageBoundaryAsync(cancellationToken);
        }
    }

    private async Task IndexUntilNextPageBoundaryAsync(CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken);

        try
        {
            if (_scanComplete)
            {
                return;
            }

            await using var stream = OpenReadStream(FileOptions.SequentialScan);
            stream.Seek(_indexScanOffset, SeekOrigin.Begin);

            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);

            try
            {
                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                    if (bytesRead == 0)
                    {
                        _indexScanOffset = stream.Position;
                        FinalizeIndexAtEndOfFile();
                        return;
                    }

                    var chunkStart = stream.Position - bytesRead;

                    for (var offset = 0; offset < bytesRead; offset++)
                    {
                        var currentByte = buffer[offset];

                        if (currentByte == (byte)'\n')
                        {
                            _scannedRows++;
                            _lastScannedByteWasNewLine = true;

                            if (_scannedRows % _pageSize == 0)
                            {
                                var nextPageOffset = chunkStart + offset + 1;

                                if (nextPageOffset < _fileLength)
                                {
                                    _pageStartOffsets.Add(nextPageOffset);
                                }

                                _indexScanOffset = nextPageOffset;
                                return;
                            }
                        }
                        else
                        {
                            _lastScannedByteWasNewLine = false;
                        }
                    }

                    _indexScanOffset = stream.Position;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private void FinalizeIndexAtEndOfFile()
    {
        if (_fileLength <= _dataStartOffset)
        {
            _scannedRows = 0;
            _scanComplete = true;
            return;
        }

        if (!_lastScannedByteWasNewLine)
        {
            _scannedRows++;
            _lastScannedByteWasNewLine = true;
        }

        _scanComplete = true;
    }

    private long CalculateTotalPages()
    {
        if (_scannedRows <= 0)
        {
            return 0;
        }

        return (_scannedRows + _pageSize - 1) / _pageSize;
    }

    private async Task<IReadOnlyList<string[]>> ReadPageRowsAsync(long startOffset, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>(_pageSize);

        await using var stream = OpenReadStream(FileOptions.SequentialScan);
        stream.Seek(startOffset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, _encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1024 * 1024);

        for (var rowIndex = 0; rowIndex < _pageSize; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            rows.Add(CsvLineParser.Parse(line).ToArray());
        }

        return rows;
    }

    private async Task<(string HeaderLine, long DataStartOffset)> ReadHeaderAndOffsetAsync(CancellationToken cancellationToken)
    {
        await using var stream = OpenReadStream(FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var headerBytes = new ArrayBufferWriter<byte>(1024);

        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                if (bytesRead == 0)
                {
                    if (headerBytes.WrittenCount == 0)
                    {
                        throw new InvalidDataException("CSV file is empty.");
                    }

                    return (DecodeHeader(headerBytes.WrittenSpan), stream.Position);
                }

                var span = buffer.AsSpan(0, bytesRead);
                var newLineIndex = span.IndexOf((byte)'\n');

                if (newLineIndex >= 0)
                {
                    headerBytes.Write(span[..newLineIndex]);
                    var dataStartOffset = stream.Position - bytesRead + newLineIndex + 1;
                    return (DecodeHeader(headerBytes.WrittenSpan), dataStartOffset);
                }

                headerBytes.Write(span);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string DecodeHeader(ReadOnlySpan<byte> rawHeaderBytes)
    {
        if (rawHeaderBytes.Length > 0 && rawHeaderBytes[^1] == (byte)'\r')
        {
            rawHeaderBytes = rawHeaderBytes[..^1];
        }

        if (rawHeaderBytes.StartsWith(Utf8Bom))
        {
            rawHeaderBytes = rawHeaderBytes[Utf8Bom.Length..];
        }

        return _encoding.GetString(rawHeaderBytes);
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

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("InitializeAsync must be called first.");
        }
    }
}
