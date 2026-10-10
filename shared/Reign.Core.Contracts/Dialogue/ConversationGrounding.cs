using System;
using System.IO;

namespace Reign.Core.Contracts.Dialogue
{
    /// <summary>Durable replacement used by the client's original-input journal.</summary>
    public static class ConversationInputJournalStore
    {
        public static void WriteAtomic(string file, byte[] bytes, Action<string>? checkpoint = null)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    checkpoint?.Invoke("before_flush");
                    stream.Flush(true);
                }
                checkpoint?.Invoke("before_publish");
                if (File.Exists(file)) File.Replace(temporary, file, null);
                else File.Move(temporary, file);
            }
            finally
            {
                // Only this call's unique temporary file is eligible for cleanup.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
