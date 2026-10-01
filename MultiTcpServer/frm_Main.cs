using System;
using System.Collections.Concurrent;
using System.Configuration;
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
        private volatile bool _isRunning = false;

        private const int MessageIdleTimeoutMs = 200;
        private const int ReceiveBufferSize = 1024;
        private const string FallbackTestClientHost = "127.0.0.1";
        private const int FallbackPort = 4488;

        // Connected clients, tracked so StopServer() can close them
        private readonly ConcurrentDictionary<TcpClient, byte> _activeClients = new ConcurrentDictionary<TcpClient, byte>();

        // ใช้ ConcurrentDictionary เพื่อเก็บข้อมูล client ตาม IP
        private ConcurrentDictionary<string, string> _clientData = new ConcurrentDictionary<string, string>();

        // Repository for data storage (Database or File-based)
        private volatile IDataRepository _dataRepository;

        public frm_Main()
        {
            InitializeComponent();
            txtPort.Text = GetDefaultPort().ToString();
            // Initialize with Database repository by default
            _dataRepository = new DatabaseRepository();
        }

        private static int GetDefaultPort()
        {
            int port;
            return int.TryParse(ConfigurationManager.AppSettings["DefaultPort"], out port) && port >= 1 && port <= 65535
                ? port
                : FallbackPort;
        }

        private static string GetTestClientHost()
        {
            string host = ConfigurationManager.AppSettings["TestClientHost"];
            return string.IsNullOrWhiteSpace(host) ? FallbackTestClientHost : host.Trim();
        }

        private void btnStart_Click(object sender, EventArgs e)
        {
            int port;
            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                AppendLog("⚠️ Invalid port. Enter a number between 1 and 65535.");
                return;
            }

            SetRunningUi(true);
            StartServer(port);
        }

        private void btnStop_Click(object sender, EventArgs e)
        {
            StopServer();
            SetRunningUi(false);
        }

        private void SetRunningUi(bool running)
        {
            txtPort.Enabled = !running;
            btnStart.Enabled = !running;
            btnStop.Enabled = running;
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
                SetRunningUi(false);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
            AppendLog($"✅ Client connected: {clientIP}");
            _activeClients.TryAdd(client, 0);

            try
            {
                await ReceiveLoopAsync(client, clientIP);
            }
            catch (IOException ioEx)
            {
                LogIoError(clientIP, ioEx);
            }
            catch (ObjectDisposedException)
            {
                // เกิดตอน server หยุดหรือ client ปิด — เงียบได้เลย
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Unexpected error ({clientIP}): {ex.Message}");
            }
            finally
            {
                byte ignored;
                _activeClients.TryRemove(client, out ignored);
                client.Close();
                AppendLog($"❎ Client disconnected: {clientIP}");
            }
        }

        // Reads until the client closes. Complete lines are handled immediately;
        // a partial line is handled once the line has been idle for MessageIdleTimeoutMs.
        private async Task ReceiveLoopAsync(TcpClient client, string clientIP)
        {
            NetworkStream stream = client.GetStream();
            var buffer = new byte[ReceiveBufferSize];
            var framer = new MessageFramer();
            Task<int> readTask = stream.ReadAsync(buffer, 0, buffer.Length);

            while (_isRunning)
            {
                if (framer.HasPending && await Task.WhenAny(readTask, Task.Delay(MessageIdleTimeoutMs)) != readTask)
                {
                    await HandleMessageAsync(clientIP, framer.Flush());
                    continue;
                }

                int bytesRead = await readTask;
                if (bytesRead == 0)
                {
                    await HandleMessageAsync(clientIP, framer.Flush());
                    return;
                }

                foreach (string message in framer.Append(buffer, bytesRead))
                    await HandleMessageAsync(clientIP, message);

                readTask = stream.ReadAsync(buffer, 0, buffer.Length);
            }
        }

        private async Task HandleMessageAsync(string clientIP, string message)
        {
            if (message == null) return;

            _clientData[clientIP] = message;
            AppendLog($"[{clientIP}] {message}");

            // Save off the UI thread so a slow database does not freeze the form
            await Task.Run(() => SaveDataFromClient(clientIP, message));
        }

        private void LogIoError(string clientIP, IOException ioEx)
        {
            if (!_isRunning) return; // socket was closed by StopServer()

            var socketEx = ioEx.InnerException as SocketException;
            if (socketEx == null)
            {
                AppendLog($"⚠️ IO error ({clientIP}): {ioEx.Message}");
                return;
            }

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

            foreach (TcpClient client in _activeClients.Keys)
                client.Close();

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
                string message = "Hello from Client - " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
                byte[] data = Encoding.UTF8.GetBytes(message);

                using (var client = new TcpClient(GetTestClientHost(), port))
                {
                    client.GetStream().Write(data, 0, data.Length);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"⚠️ Send error: {ex.Message}");
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
