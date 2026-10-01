using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MultiTcpServer
{
    /// <summary>
    /// Reassembles a raw TCP byte stream into text messages.
    /// A message ends at '\n', or when the caller flushes after the line goes idle.
    /// </summary>
    public sealed class MessageFramer
    {
        public const int MaxPendingBytes = 64 * 1024;

        private readonly MemoryStream _pending = new MemoryStream();

        public bool HasPending
        {
            get { return _pending.Length > 0; }
        }

        /// <summary>
        /// Adds received bytes and returns every message completed by them.
        /// </summary>
        public List<string> Append(byte[] data, int count)
        {
            _pending.Write(data, 0, count);

            var messages = new List<string>();
            ExtractLines(messages);

            if (_pending.Length >= MaxPendingBytes)
                AddIfNotEmpty(messages, Flush());

            return messages;
        }

        /// <summary>
        /// Returns whatever is buffered as one message (null if it is blank) and clears the buffer.
        /// </summary>
        public string Flush()
        {
            string message = Decode(_pending.GetBuffer(), 0, (int)_pending.Length);
            _pending.SetLength(0);
            return message.Length == 0 ? null : message;
        }

        // Emits every complete line and keeps the partial remainder buffered.
        private void ExtractLines(List<string> messages)
        {
            byte[] buf = _pending.GetBuffer();
            int len = (int)_pending.Length;
            int last = Array.LastIndexOf(buf, (byte)'\n', len - 1);
            if (last < 0) return;

            int start = 0;
            for (int i = 0; i <= last; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                AddIfNotEmpty(messages, Decode(buf, start, i - start));
                start = i + 1;
            }

            int remaining = len - last - 1;
            Buffer.BlockCopy(buf, last + 1, buf, 0, remaining);
            _pending.SetLength(remaining);
        }

        private static string Decode(byte[] data, int offset, int count)
        {
            return Encoding.UTF8.GetString(data, offset, count).Trim();
        }

        private static void AddIfNotEmpty(List<string> messages, string message)
        {
            if (!string.IsNullOrEmpty(message))
                messages.Add(message);
        }
    }
}
