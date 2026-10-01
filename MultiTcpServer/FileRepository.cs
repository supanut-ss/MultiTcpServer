using System;
using System.IO;
using System.Text;

namespace MultiTcpServer
{
    /// <summary>
    /// File-based implementation of IDataRepository
    /// Stores client data to CSV files in disconnected mode (no database connection required)
    /// </summary>
    public class FileRepository : IDataRepository
    {
        private readonly string _dataDir;
        private readonly object _lockObj = new object();

        public FileRepository()
        {
            _dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            if (!Directory.Exists(_dataDir))
            {
                Directory.CreateDirectory(_dataDir);
            }
        }

        /// <summary>
        /// Saves client data to a CSV file
        /// </summary>
        /// <param name="clientIp">IP address of the client</param>
        /// <param name="message">Data message from the client</param>
        public void SaveData(string clientIp, string message)
        {
            try
            {
                lock (_lockObj)
                {
                    string dataFile = Path.Combine(_dataDir, $"ClientData_{DateTime.Now:yyyy-MM-dd}.csv");
                    bool fileExists = File.Exists(dataFile);

                    using (var fs = new FileStream(dataFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs, Encoding.UTF8))
                    {
                        // Write CSV header if file is new
                        if (!fileExists)
                        {
                            sw.WriteLine("data_id,client_ip,data_time,data_message");
                        }

                        // Write data row (auto-increment ID based on line count)
                        int recordId = CountRecords(dataFile) + 1;
                        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        string escapedMessage = EscapeCsvField(message);

                        sw.WriteLine($"{recordId},\"{clientIp}\",\"{timestamp}\",\"{escapedMessage}\"");
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"File repository error: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Counts the number of data records in the file (excluding header)
        /// </summary>
        private int CountRecords(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return 0;

                int count = 0;
                using (var reader = new StreamReader(filePath, Encoding.UTF8))
                {
                    string line;
                    bool isFirstLine = true;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (isFirstLine)
                        {
                            isFirstLine = false;
                            continue; // Skip header
                        }
                        count++;
                    }
                }
                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Escapes special characters in CSV fields
        /// </summary>
        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return string.Empty;

            // Replace quotes with double quotes for CSV escaping
            return field.Replace("\"", "\"\"");
        }
    }
}
