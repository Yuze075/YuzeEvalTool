#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.Agent
{
    internal readonly struct SseEvent
    {
        public SseEvent(string name, string data)
        {
            Name = name;
            Data = data;
        }

        public string Name { get; }

        public string Data { get; }
    }

    internal static class SseParser
    {
        private const int BufferSize = 8192;
        private const int MaxEventCharacters = 8 * 1024 * 1024;

        public static async Task ParseAsync(
            Stream stream,
            Action<SseEvent> onEvent,
            CancellationToken cancellationToken)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (onEvent == null) throw new ArgumentNullException(nameof(onEvent));

            var bytes = new byte[BufferSize];
            var characters = new char[BufferSize];
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            var line = new StringBuilder();
            var data = new StringBuilder();
            var eventName = string.Empty;
            var hasDataField = false;
            var isFirstCharacter = true;
            var previousWasCarriageReturn = false;

            void Dispatch()
            {
                if (!hasDataField)
                {
                    data.Clear();
                    eventName = string.Empty;
                    return;
                }
                if (data.Length > 0 && data[data.Length - 1] == '\n') data.Length--;
                onEvent(new SseEvent(eventName, data.ToString()));
                data.Clear();
                eventName = string.Empty;
                hasDataField = false;
            }

            void CompleteLine()
            {
                var value = line.ToString();
                line.Clear();
                if (value.Length == 0)
                {
                    Dispatch();
                    return;
                }

                if (value[0] == ':') return;
                var separator = value.IndexOf(':');
                var field = separator < 0 ? value : value.Substring(0, separator);
                var fieldValue = separator < 0 ? string.Empty : value.Substring(separator + 1);
                if (fieldValue.StartsWith(" ", StringComparison.Ordinal)) fieldValue = fieldValue.Substring(1);
                if (field == "event")
                    eventName = fieldValue;
                else if (field == "data")
                {
                    hasDataField = true;
                    data.Append(fieldValue).Append('\n');
                    if (data.Length > MaxEventCharacters)
                        throw new InvalidDataException("Provider SSE event exceeded the 8 MiB limit.");
                }
            }

            void ConsumeCharacters(char[] buffer, int count)
            {
                for (var index = 0; index < count; index++)
                {
                    var character = buffer[index];
                    if (isFirstCharacter)
                    {
                        isFirstCharacter = false;
                        if (character == '\uFEFF') continue;
                    }
                    if (character == '\r')
                    {
                        CompleteLine();
                        previousWasCarriageReturn = true;
                    }
                    else if (character == '\n')
                    {
                        if (!previousWasCarriageReturn) CompleteLine();
                        previousWasCarriageReturn = false;
                    }
                    else
                    {
                        previousWasCarriageReturn = false;
                        line.Append(character);
                        if (line.Length > MaxEventCharacters)
                            throw new InvalidDataException("Provider SSE line exceeded the 8 MiB limit.");
                    }
                }
            }

            while (true)
            {
                var count = await stream.ReadAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                var byteOffset = 0;
                while (byteOffset < count)
                {
                    decoder.Convert(bytes, byteOffset, count - byteOffset, characters, 0, characters.Length, false,
                        out var bytesUsed, out var charactersUsed, out _);
                    byteOffset += bytesUsed;
                    ConsumeCharacters(characters, charactersUsed);
                }
            }

            decoder.Convert(Array.Empty<byte>(), 0, 0, characters, 0, characters.Length, true,
                out _, out var finalCharacters, out _);
            ConsumeCharacters(characters, finalCharacters);
            if (line.Length > 0) CompleteLine();
            Dispatch();
        }
    }
}
