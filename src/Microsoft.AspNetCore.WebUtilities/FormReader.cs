// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.WebUtilities
{
    /// <summary>
    /// Used to read an 'application/x-www-form-urlencoded' form.
    /// </summary>
    public class FormReader : IDisposable
    {
        private const int _rentedCharPoolLength = 8192;
        private readonly TextReader _reader;
        private readonly char[] _buffer;
        private readonly ArrayPool<byte> _bytePool;
        private readonly ArrayPool<char> _charPool;
        private readonly StringBuilder _builder = new StringBuilder();
        private int _bufferOffset;
        private int _bufferCount;
        private bool _disposed;

        public FormReader(string data)
            : this(data, ArrayPool<byte>.Shared, ArrayPool<char>.Shared)
        {
        }

        public FormReader(string data, ArrayPool<byte> bytePool, ArrayPool<char> charPool)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            _buffer = charPool.Rent(_rentedCharPoolLength);
            _charPool = charPool;
            _bytePool = bytePool;
            _reader = new StringReader(data);
        }

        public FormReader(Stream stream, Encoding encoding)
            : this(stream, encoding, ArrayPool<byte>.Shared, ArrayPool<char>.Shared)
        {
        }

        public FormReader(Stream stream, Encoding encoding, ArrayPool<byte> bytePool, ArrayPool<char> charPool)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (encoding == null)
            {
                throw new ArgumentNullException(nameof(encoding));
            }

            _buffer = charPool.Rent(_rentedCharPoolLength);
            _charPool = charPool;
            _bytePool = bytePool;
            _reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 2, leaveOpen: true);
        }

        // Format: key1=value1&key2=value2
        /// <summary>
        /// Reads the next key value pair from the form.
        /// For unbuffered data use the async overload instead.
        /// </summary>
        /// <returns>The next key value pair, or null when the end of the form is reached.</returns>
        public KeyValuePair<string, string>? ReadNextPair()
        {
            var key = ReadWord('=');
            if (string.IsNullOrEmpty(key) && _bufferCount == 0)
            {
                return null;
            }
            var value = ReadWord('&');
            return new KeyValuePair<string, string>(key, value);
        }

        // Format: key1=value1&key2=value2
        /// <summary>
        /// Asynchronously reads the next key value pair from the form.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>The next key value pair, or null when the end of the form is reached.</returns>
        public ValueTask<KeyValuePair<string, string>?> ReadNextPairAsync(CancellationToken cancellationToken)
        {
            var keyTask = ReadWordAsync('=', cancellationToken);
            if (!keyTask.IsCompletedSuccessfully)
            {
                return ReadNextPairAsyncKeyAwaited(keyTask.AsTask(), cancellationToken);
            }
            var key = keyTask.Result;
            if (string.IsNullOrEmpty(key) && _bufferCount == 0)
            {
                return (KeyValuePair<string, string>?)null;
            }

            var valueTask = ReadWordAsync('&', cancellationToken);
            if (!valueTask.IsCompletedSuccessfully)
            {
                return ReadNextPairAsyncValueAwaited(key, valueTask.AsTask(), cancellationToken);
            }

            var value = valueTask.Result;
            return new KeyValuePair<string, string>(key, value);
        }

        private async Task<KeyValuePair<string, string>?> ReadNextPairAsyncKeyAwaited(Task<string> keyTask, CancellationToken cancellationToken)
        {
            var key = await keyTask;
            if (string.IsNullOrEmpty(key) && _bufferCount == 0)
            {
                return null;
            }

            var valueTask = ReadWordAsync('&', cancellationToken);
            if (!valueTask.IsCompletedSuccessfully)
            {
                var value = await valueTask;
                return new KeyValuePair<string, string>(key, value);
            }

            return new KeyValuePair<string, string>(key, valueTask.Result);
        }

        private async Task<KeyValuePair<string, string>?> ReadNextPairAsyncValueAwaited(string key, Task<string> valueTask, CancellationToken cancellationToken)
        {
            var value = await valueTask;
            return new KeyValuePair<string, string>(key, value);
        }

        private string ReadWord(char seperator)
        {
            // TODO: Configurable value size limit
            while (true)
            {
                // Empty
                if (_bufferCount == 0)
                {
                    Buffer();
                }

                var word = ReadWordImpl(seperator);
                if (word != null)
                {
                    return word;
                }
            }
        }

        private ValueTask<string> ReadWordAsync(char seperator, CancellationToken cancellationToken)
        {
            // TODO: Configurable value size limit
            while (true)
            {
                // Empty
                if (_bufferCount == 0)
                {
                    return ReadWordAsyncAwaited(seperator, cancellationToken);
                }

                var word = ReadWordImpl(seperator);
                if (word != null)
                {
                    return word;
                }
            }
        }
        private async Task<string> ReadWordAsyncAwaited(char seperator, CancellationToken cancellationToken)
        {
            // TODO: Configurable value size limit
            while (true)
            {
                // Empty
                if (_bufferCount == 0)
                {
                    await BufferAsync(cancellationToken);
                }

                var word = ReadWordImpl(seperator);
                if (word != null)
                {
                    return word;
                }
            }
        }

        private string ReadWordImpl(char seperator)
        {
            // End
            if (_bufferCount == 0)
            {
                return BuildWord();
            }

            var c = _buffer[_bufferOffset++];
            _bufferCount--;

            if (c == seperator)
            {
                return BuildWord();
            }
            _builder.Append(c);

            return null;
        }

        // '+' un-escapes to ' ', %HH un-escapes as ASCII (or utf-8?)
        private string BuildWord()
        {
            _builder.Replace('+', ' ');
            var result = _builder.ToString();
            _builder.Clear();
            return Uri.UnescapeDataString(result); // TODO: Replace this, it's not completely accurate.
        }

        private void Buffer()
        {
            _bufferOffset = 0;
            _bufferCount = _reader.Read(_buffer, 0, _buffer.Length);
        }

        private async Task BufferAsync(CancellationToken cancellationToken)
        {
            // TODO: StreamReader doesn't support cancellation?
            cancellationToken.ThrowIfCancellationRequested();
            _bufferOffset = 0;
            _bufferCount = await _reader.ReadAsync(_buffer, 0, _buffer.Length);
        }

        /// <summary>
        /// Parses text from an HTTP form body.
        /// </summary>
        /// <param name="text">The HTTP form body to parse.</param>
        /// <returns>The collection containing the parsed HTTP form body.</returns>
        public static Dictionary<string, StringValues> ReadForm(string text)
        {
            using (var reader = new FormReader(text))
            {
                var accumulator = new KeyValueAccumulator();
                var pair = reader.ReadNextPair();
                while (pair.HasValue)
                {
                    accumulator.Append(pair.Value.Key, pair.Value.Value);
                    pair = reader.ReadNextPair();
                }

                return accumulator.GetResults();
            }
        }

        /// <summary>
        /// Parses an HTTP form body.
        /// </summary>
        /// <param name="stream">The HTTP form body to parse.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <returns>The collection containing the parsed HTTP form body.</returns>
        public static ValueTask<Dictionary<string, StringValues>> ReadFormAsync(Stream stream, CancellationToken cancellationToken = default(CancellationToken))
        {
            return ReadFormAsync(stream, Encoding.UTF8, cancellationToken);
        }

        /// <summary>
        /// Parses an HTTP form body.
        /// </summary>
        /// <param name="stream">The HTTP form body to parse.</param>
        /// <param name="encoding">The character encoding to use.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <returns>The collection containing the parsed HTTP form body.</returns>
        public static ValueTask<Dictionary<string, StringValues>> ReadFormAsync(Stream stream, Encoding encoding, CancellationToken cancellationToken = default(CancellationToken))
        {
            var isAsync = false;
            var reader = new FormReader(stream, encoding);
            try
            {
                var accumulator = new KeyValueAccumulator();

                var pairTask = reader.ReadNextPairAsync(cancellationToken);
                if (!pairTask.IsCompletedSuccessfully)
                {
                    isAsync = true;
                    return ReadFormAsyncAwaited(pairTask.AsTask(), reader, accumulator, cancellationToken);
                }

                var pair = pairTask.Result;
                while (pair.HasValue)
                {
                    accumulator.Append(pair.Value.Key, pair.Value.Value);

                    pairTask = reader.ReadNextPairAsync(cancellationToken);
                    if (!pairTask.IsCompletedSuccessfully)
                    {
                        isAsync = true;
                        return ReadFormAsyncAwaited(pairTask.AsTask(), reader, accumulator, cancellationToken);
                    }

                    pair = pairTask.Result;
                }

                return accumulator.GetResults();
            }
            finally
            {
                if (!isAsync)
                {
                    reader.Dispose();
                }
            }
        }

        private static async Task<Dictionary<string, StringValues>> ReadFormAsyncAwaited(Task<KeyValuePair<string, string>?> pairTask, FormReader reader, KeyValueAccumulator accumulator, CancellationToken cancellationToken)
        {
            using (reader)
            {
                var pair = await pairTask;
                while (pair.HasValue)
                {
                    accumulator.Append(pair.Value.Key, pair.Value.Value);
                    pair = await reader.ReadNextPairAsync(cancellationToken);
                }

                return accumulator.GetResults();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _charPool.Return(_buffer);
            }
        }
    }
}