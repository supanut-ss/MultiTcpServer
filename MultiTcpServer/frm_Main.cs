using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiTcpServer
{
    public partial class frm_Main : Form
    {

        private TcpListener _server;
        private bool _isRunning = false;

        // ใช้ ConcurrentDictionary เพื่อเก็บข้อมูล client ตาม IP
        private ConcurrentDictionary<string, string> _clientData = new ConcurrentDictionary<string, string>();

        // Repository for data storage (Database or File-based)
        private IDataRepository _dataRepository;

        public frm_Main()
        {
            InitializeComponent();
            // Initialize with Database repository by default
            _dataRepository = new DatabaseRepository();
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            int port = int.Parse(txtPort.Text.Trim());
            StartServer(port);
            txtPort.Enabled = false;
            btnStart.Enabled = false;
            btnStop.Enabled = true;
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            StopServer();
            txtPort.Enabled = true;
            btnStart.Enabled = true;
            btnStop.Enabled = false;
        }

        private async void StartServer(int port)
        {
            try
            {
                _server = new TcpListener(IPAddress.Any, port);

                // Enable SO_REUSEADDR to allow port reuse after restart
                _server.Server.SetSocketOption(
                    System.Net.Sockets.SocketOptionLevel.Socket,
                    System.Net.Sockets.SocketOptionName.ReuseAddress,
                    true);

                _server.Start();
                _isRunning = true;

                AppendLog($"🚀 Server started on port {port}");

                while (_isRunning)
                {
                    try
                    {
                        var client = await _server.AcceptTcpClientAsync();
                        _ = HandleClientAsync(client);
                    }
                    catch (ObjectDisposedException)
                    {
                        // เกิดขึ้นตอน StopServer() — ไม่ต้องแจ้ง error
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"❌ Error (accept): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Error (start): {ex.Message}");
                _isRunning = false;
            }
        }

        private const int MessageIdleTimeoutMs = 200;
        private const int MaxMessageBytes = 64 * 1024;

        private void ProcessClientMessage(string clientIP, MemoryStream pending)
        {
            EmitMessage(clientIP, pending.GetBuffer(), 0, (int)pending.Length);
            pending.SetLength(0);
        }

        // Emit every complete line (ending in '\n') right away; keep any partial remainder
        // in the buffer to be completed by later data or flushed by the idle timeout.
        private void ProcessCompleteLines(string clientIP, MemoryStream pending)
        {
            byte[] buf = pending.GetBuffer();
            int len = (int)pending.Length;
            int last = Array.LastIndexOf(buf, (byte)'\n', len - 1);
            if (last < 0) return;

            int start = 0;
            for (int i = 0; i <= last; i++)
            {
                if (buf[i] != (byte)'\n') continue;
                EmitMessage(clientIP, buf, start, i - start);
                start = i + 1;
            }

            int remaining = len - last - 1;
            Buffer.BlockCopy(buf, last + 1, buf, 0, remaining);
            pending.SetLength(remaining);
        }

        private void EmitMessage(string clientIP, byte[] data, int offset, int count)
        {
            string message = Encoding.UTF8.GetString(data, offset, count).Trim();
            if (message.Length == 0) return;

            _clientData[clientIP] = message;
            AppendLog($"[{clientIP}] {message}");

            // บันทึกข้อมูลลงฐานข้อมูล
            SaveDataFromClient(clientIP, message);
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            AppendLog($"✅ Client connected: {clientIP}");

            try
            {
                using (var stream = client.GetStream())
                {
                    byte[] buffer = new byte[1024];
                    int bytesRead;

                    // Accumulate bytes until the line goes idle, so one logical message
                    // split across several TCP reads is handled as a single message.
                    var pending = new MemoryStream();
                    Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length);

                    while (_isRunning && client.Connected)
                    {
                        try
                        {
                            if (pending.Length > 0)
                            {
                                var winner = await Task.WhenAny(readTask, Task.Delay(MessageIdleTimeoutMs));
                                if (winner != readTask)
                                {
                                    // No new data within the idle window: message is complete
                                    ProcessClientMessage(clientIP, pending);
                                    continue;
                                }
                            }

                            bytesRead = await readTask;
                            if (bytesRead == 0)
                            {
                                ProcessClientMessage(clientIP, pending);
                                break;
                            }

                            pending.Write(buffer, 0, bytesRead);
                            ProcessCompleteLines(clientIP, pending);
                            if (pending.Length >= MaxMessageBytes)
                                ProcessClientMessage(clientIP, pending);

                            readTask = stream.ReadAsync(buffer, 0, buffer.Length);
                        }
                        catch (IOException ioEx)
                        {
                            if (ioEx.InnerException is SocketException socketEx)
                            {
                                switch (socketEx.SocketErrorCode)
                                {
                                    case SocketError.ConnectionReset:
                                        AppendLog($"❎ {clientIP} disconnected.");
                                        break;

                                    case SocketError.TimedOut:
                                        AppendLog($"⚠️ {clientIP} timed out.");
                                        break;

                                    default:
                                        AppendLog($"⚠️ Socket error ({clientIP}): {socketEx.SocketErrorCode}");
                                        break;
                                }
                            }
                            else
                            {
                                AppendLog($"⚠️ IO error ({clientIP}): {ioEx.Message}");
                            }
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            // เกิดตอน server หยุดหรือ client ปิด — เงียบได้เลย
                            break;
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"⚠️ Unexpected error ({clientIP}): {ex.Message}");
                            break;
                        }

                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Client error ({clientIP}): {ex.Message}");
            }
            finally
            {
                client.Close();
                AppendLog($"❎ Client disconnected: {clientIP}");
            }
        }


        private void StopServer()
        {
            _isRunning = false;

            try
            {
                if (_server != null)
                {
                    _server.Stop();
                    _server.Server.Close();
                    _server.Server.Dispose();
                }
            }
            catch { }

            AppendLog("🛑 Server stopped.");
        }


        private void AppendLog(string text)
        {
            if (InvokeRequired)
            {
                Invoke(new Action(() => AppendLog(text)));
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string logLine = $"{timestamp} - {text}{Environment.NewLine}";

            // 🧹 ตรวจจำนวนบรรทัด ถ้าเกิน 200 ให้เคลียร์
            if (txtLog.Lines.Length > 200)
            {
                txtLog.Clear();
                txtLog.AppendText($"{DateTime.Now:HH:mm:ss} - 🧹 Log cleared automatically{Environment.NewLine}");
            }

            // แสดงบนหน้าจอ
            txtLog.AppendText(logLine);

            // ✍️ เขียนลงไฟล์ log ตามวัน
            WriteLogToFile(logLine);
        }


        private void txtSendData_Click(object sender, EventArgs e)
        {
            try
            {
                int port = int.Parse(txtPort.Text.Trim());
                // ส่งข้อความไปยัง Server
                TcpClient client = new TcpClient("192.168.11.9", port);
                NetworkStream stream = client.GetStream();

                string message = "Hello from Client - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
                byte[] data = Encoding.UTF8.GetBytes(message);
                stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ DB Error: {ex.Message}");
            }
        }

        private void SaveDataFromClient(string ip, string message)
        {
            try
            {
                _dataRepository.SaveData(ip, message);
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Error saving data: {ex.Message}");
            }
        }

        /// <summary>
        /// Switches the data storage mode between Database and File-based
        /// </summary>
        /// <param name="useFileMode">True for file-based storage, False for database</param>
        public void SwitchStorageMode(bool useFileMode)
        {
            try
            {
                if (useFileMode)
                {
                    _dataRepository = new FileRepository();
                    AppendLog("📁 Switched to File-based storage mode (Disconnected)");
                }
                else
                {
                    _dataRepository = new DatabaseRepository();
                    AppendLog("🗄️ Switched to Database storage mode");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Error switching storage mode: {ex.Message}");
                // Fallback to database mode
                _dataRepository = new DatabaseRepository();
            }
        }

        /// <summary>
        /// Event handler for switching to Database mode
        /// </summary>
        private void rdoDatabaseMode_CheckedChanged(object sender, EventArgs e)
        {
            if (rdoDatabaseMode.Checked)
            {
                SwitchStorageMode(false);
            }
        }

        /// <summary>
        /// Event handler for switching to File-based mode (Offline)
        /// </summary>
        private void rdoFileMode_CheckedChanged(object sender, EventArgs e)
        {
            if (rdoFileMode.Checked)
            {
                SwitchStorageMode(true);
            }
        }

        private void WriteLogToFile(string logLine)
        {
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"Log_{DateTime.Now:yyyy-MM-dd}.log");

                // เปิดไฟล์แบบอนุญาตให้คนอื่นเปิดอ่าน/เขียนได้ด้วย
                using (var fs = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs, Encoding.UTF8))
                {
                    sw.WriteLine(logLine.TrimEnd());
                }
            }
            catch (Exception ex)
            {
                txtLog.AppendText($"{DateTime.Now:HH:mm:ss} - ⚠️ Write log file error: {ex.Message}{Environment.NewLine}");
            }
        }

    }
}
