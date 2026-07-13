namespace enterprise_d365_gateway.Functions
{
    /// <summary>
    /// Enforces the request body size limit even when the transport stream is
    /// not seekable (the production case under ASP.NET Core integration, where
    /// Body.Length is unavailable and a Length check alone is dead code).
    /// </summary>
    internal static class RequestBodyGuard
    {
        /// <summary>
        /// Fast path: true when the declared or known body size already exceeds the limit.
        /// </summary>
        internal static bool ExceedsDeclaredLength(Microsoft.Azure.Functions.Worker.Http.HttpRequestData req, long maxBytes)
        {
            if (req.Body.CanSeek && req.Body.Length > maxBytes)
                return true;

            if (req.Headers.TryGetValues("Content-Length", out var values)
                && long.TryParse(values.FirstOrDefault(), out var declared)
                && declared > maxBytes)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Hard enforcement: wraps the body so that reading past
        /// <paramref name="maxBytes"/> throws <see cref="RequestBodyTooLargeException"/>.
        /// </summary>
        internal static Stream LimitBody(Stream body, long maxBytes) => new LengthLimitedReadStream(body, maxBytes);

        private sealed class LengthLimitedReadStream : Stream
        {
            private readonly Stream _inner;
            private readonly long _maxBytes;
            private long _read;

            public LengthLimitedReadStream(Stream inner, long maxBytes)
            {
                _inner = inner;
                _maxBytes = maxBytes;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var n = _inner.Read(buffer, offset, count);
                Account(n);
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var n = await _inner.ReadAsync(buffer, cancellationToken);
                Account(n);
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            private void Account(int bytesRead)
            {
                _read += bytesRead;
                if (_read > _maxBytes)
                    throw new RequestBodyTooLargeException(_maxBytes);
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    internal sealed class RequestBodyTooLargeException : Exception
    {
        public RequestBodyTooLargeException(long maxBytes)
            : base($"Request body exceeds the maximum allowed size of {maxBytes} bytes.")
        {
        }
    }
}
