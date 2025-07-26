using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NucleusAPI;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace SynapseUI
{
    public partial class MainForm : Form
    {
        private const int WM_NCHITTEST = 0x84;
        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_NCHITTEST)
            {
                Point pos = new Point(m.LParam.ToInt32());
                pos = this.PointToClient(pos);

                if (pos.X <= 1)
                {
                    if (pos.Y <= 1)
                        m.Result = (IntPtr)HTTOPLEFT;
                    else if (pos.Y >= this.ClientSize.Height - 1)
                        m.Result = (IntPtr)HTBOTTOMLEFT;
                    else
                        m.Result = (IntPtr)HTLEFT;
                }
                else if (pos.X >= this.ClientSize.Width - 1)
                {
                    if (pos.Y <= 1)
                        m.Result = (IntPtr)HTTOPRIGHT;
                    else if (pos.Y >= this.ClientSize.Height - 1)
                        m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else
                        m.Result = (IntPtr)HTRIGHT;
                }
                else if (pos.Y <= 1)
                {
                    m.Result = (IntPtr)HTTOP;
                }
                else if (pos.Y >= this.ClientSize.Height - 1)
                {
                    m.Result = (IntPtr)HTBOTTOM;
                }
                else
                {
                    m.Result = (IntPtr)HTCAPTION;
                }
            }
        }

        private void Seliware_MessageReceived(object sender, MessageEventArgs e, string messageJson)
        {
            try
            {
                var message = JsonConvert.DeserializeObject<Dictionary<string, object>>(messageJson);
                if (message.ContainsKey("type") && message["type"].ToString() == "console_message")
                {
                    SendMessageToUI(new
                    {
                        type = "console_message",
                        text = message["text"].ToString(),
                        messageType = message["messageType"].ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling Lua message: {ex.Message}");
            }
        }
        
        private class LogHandler
        {
            private readonly MainForm _form;

            public LogHandler(MainForm form)
            {
                _form = form;
            }

            public void SetupLogService()
            {
            }
        }

        private void mf()
        {
            string luaCode = @"
local ws = WebSocket.connect('ws://localhost:8765/ui')
local logserv = game:GetService('LogService')
logserv.MessageOut:Connect(function(output, OutputType)
    local messageType = 'info'
    
    if OutputType == Enum.MessageType.MessageError then
        messageType = 'error'
    elseif OutputType == Enum.MessageType.MessageWarning then
        messageType = 'warning'
    end
    local jdata = game:GetService('HttpService'):JSONEncode({type = 'console_message',text = output,messageType = messageType})
    ws:Send(jdata)
end)
        ";
            string un = System.Environment.UserName;
            string pt = "C:\\Users\\" + un + "\\AppData\\Local\\Nucleus\\autoexec\\AAA_console.lua";
            if (!File.Exists(pt))
            {
                File.WriteAllText(pt, luaCode);
                return;
            }
            File.Delete(pt);
            File.WriteAllText(pt, luaCode);
        }
        private WebView2 _webView;
        private string _scriptsFolder;
        private bool _isMaximized = false;
        private Point _originalLocation;
        private Size _originalSize;
        private WebSocketServer _webSocketServer;
        private const int WEBSOCKET_PORT = 8765;
        
        private bool _autoInjectEnabled = false;
        private Thread _autoInjectThread;
        private bool _isRunning = true;
        private string _settingsPath;
        private string _currentFolder = "/";
        private HashSet<int> _injectedProcessIds = new HashSet<int>();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
        
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HT_CAPTION = 0x2;
        
        public static void MoveWindow(IntPtr handle)
        {
            ReleaseCapture();
            SendMessage(handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }
        private Nucleus nucleus;
        public MainForm()
        {
            nucleus = new();
            InitializeComponent();
            InitializeWebSocketServer();
            InitializeAsync();
            InitializeWindow();
            InitializeMain();
            LoadSettings();
            SendCurrentSettings();
            mf();
            removeupdatescript();
            
            this.FormBorderStyle = FormBorderStyle.None;
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;

        }

        private void removeupdatescript()
        {
            if (File.Exists("GetSynUI.bat"))
            {
                File.Delete("GetSynUI.bat");
            }
        }

        private void InitializeWebSocketServer()
        {
            _webSocketServer = new WebSocketServer(WEBSOCKET_PORT);
            _webSocketServer.AddWebSocketService<UIWebSocket>("/ui", () => new UIWebSocket(this));
            _webSocketServer.Start();
            Debug.WriteLine($"WebSocket server started on port {WEBSOCKET_PORT}");
        }

        private void InitializeWindow()
        {
            _originalLocation = this.Location;
            this.Size = new Size(1280, 720);
            _originalSize = this.Size;
        }
        

        private async void InitializeAsync()
        {
            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            this.Controls.Add(_webView);
            
            var env = await CoreWebView2Environment.CreateAsync();
            await _webView.EnsureCoreWebView2Async(env);
            
            _webView.CoreWebView2.WebMessageReceived += WebView_WebMessageReceived;
            
            string htmlPath = Path.Combine(Application.StartupPath, "core", "mainsite.html");
            if (File.Exists(htmlPath))
            {
                string webSocketScript = $@"<script>
                    const WS_PORT = {WEBSOCKET_PORT};
                    const socket = new WebSocket(`ws://localhost:${{WS_PORT}}/ui`);
                    
                    socket.addEventListener('open', (event) => {{
                        console.log('WebSocket connected');
                        window.postMessageToServer({{ type: 'get_scripts_list', path: '/' }});
                    }});
                    
                    socket.addEventListener('message', (event) => {{
                        const message = JSON.parse(event.data);
                        console.log('Received from server:', message);
                        handleWebViewMessage(message);
                    }});
                    
                    window.postMessageToServer = function(message) {{
                        console.log('Sending to server:', message);
                        socket.send(JSON.stringify(message));
                    }};
                </script>";
                
                string htmlContent = File.ReadAllText(htmlPath);
                htmlContent = htmlContent.Replace("</body>", $"{webSocketScript}</body>");
                _webView.CoreWebView2.NavigateToString(htmlContent);
            }
            else
            {
                MessageBox.Show("UI file not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public void SendMessageToUI(object message)
        {
            if (_webSocketServer != null && _webSocketServer.IsListening)
            {
                _webSocketServer.WebSocketServices["/ui"].Sessions.Broadcast(JsonConvert.SerializeObject(message));
            }
        }

        private void InitializeMain()
        {
            try
            {
                _scriptsFolder = Path.Combine(Application.StartupPath, "scripts");
                if (!Directory.Exists(_scriptsFolder))
                {
                    Directory.CreateDirectory(_scriptsFolder);
                }
                
                CreateSampleScripts();
                SendScriptsList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            
        }

        private void CreateSampleScripts()
        {
            string[] sampleScripts = {
                "Aimbot.lua",
                "ESP.lua",
                "AutoFarm.lua",
                "Teleport.lua",
                "SpeedHack.lua",
                "PlayerFly.lua",
                "NoClip.lua",
                "AutoCollect.lua"
            };

            foreach (var script in sampleScripts)
            {
                string filePath = Path.Combine(_scriptsFolder, script);
                if (!File.Exists(filePath))
                {
                    File.WriteAllText(filePath, $"-- {script}\n-- Auto-generated sample script\n\nprint('Running {script}')\n");
                }
            }
        }

        private void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Debug.WriteLine($"WebView2 message: {e.WebMessageAsJson}");
        }

        public void HandleMessageFromUI(string messageJson)
        {
            try
            {
                var message = JsonConvert.DeserializeObject<Dictionary<string, object>>(messageJson);
                string type = message["type"].ToString();

                this.Invoke((MethodInvoker)delegate
                {
                    switch (type)
                    {
                        case "console_message":
                            Seliware_MessageReceived(null, null, messageJson);
                            break;

                        case "window_action":
                            HandleWindowAction(message["action"].ToString());
                            break;

                        case "get_scripts_list":
                            if (message.ContainsKey("path"))
                            {
                                _currentFolder = message["path"].ToString();
                            }

                            SendScriptsList();
                            break;

                        case "execute":
                            string code = message.ContainsKey("code") ? message["code"].ToString() : "";
                            string tabId = message.ContainsKey("tabId") ? message["tabId"].ToString() : "";
                            ExecuteCode(code, tabId);
                            break;

                        case "open_file":
                            string openTabId = message.ContainsKey("tabId") ? message["tabId"].ToString() : "";
                            OpenFile(openTabId);
                            break;

                        case "save_file":
                            string saveCode = message.ContainsKey("code") ? message["code"].ToString() : "";
                            string saveTabId = message.ContainsKey("tabId") ? message["tabId"].ToString() : "";
                            SaveFile(saveCode, saveTabId);
                            break;

                        case "injectroblox":
                            InjectSeliware();
                            break;

                        case "load_file":
                            string loadPath = message.ContainsKey("path") ? message["path"].ToString() : "";
                            string loadTabId = message.ContainsKey("tabId") ? message["tabId"].ToString() : "";
                            LoadFile(loadPath, loadTabId);
                            break;

                        case "execute_file":
                            string executePath = message.ContainsKey("path") ? message["path"].ToString() : "";
                            ExecuteFile(executePath);
                            break;

                        case "delete_file":
                            string deletePath = message.ContainsKey("path") ? message["path"].ToString() : "";
                            DeleteFile(deletePath);
                            break;

                        case "drag_window":
                            if (!_isMaximized)
                            {
                                MoveWindow(this.Handle);
                            }
                            break;

                        case "set_topmost":
                            bool topmost = Convert.ToBoolean(message["value"]);
                            this.TopMost = topmost;
                            SaveSettings();
                            SendCurrentSettings();
                            break;

                        case "set_autoinject":
                            _autoInjectEnabled = Convert.ToBoolean(message["value"]);
                            SaveSettings();

                            if (_autoInjectEnabled)
                            {
                                StartAutoInjectLoop();
                            }
                            else
                            {
                                StopAutoInjectLoop();
                            }

                            SendCurrentSettings();
                            break;

                        case "get_settings":
                            SendCurrentSettings();
                            break;
                        case "update":
                            Updater();
                            break;
                        case "ivar":
                            bool isi = Convert.ToBoolean(message["val"]);
                            if (isi)
                            {
                                SendInjectionResult(false,"Process Exited");
                            }
                            break;
                        case "open_cloud":
                            SendMessageToUI(new { type = "open_cloud" });
                            break;
                        case "create_folder":
                            string folderName = message["name"].ToString();
                            CreateFolder(folderName);
                            break;
                        case "create_file":
                            string fileName = message["name"].ToString();
                            CreateFile(fileName);
                            break;
                        case "rename_tab":
                            string renameTabId = message["tabId"].ToString();
                            string newName = message["name"].ToString();
                            RenameTab(renameTabId, newName);
                            break;
                        case "rename_file":
                            string oldPath = message["oldPath"].ToString();
                            string newNameFile = message["newName"].ToString();
                            RenameFile(oldPath, newNameFile);
                            break;
                        case "copy_file":
                            string sourcePath = message["sourcePath"].ToString();
                            string targetPath = message["targetPath"].ToString();
                            CopyFile(sourcePath, targetPath);
                            break;
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error handling message: {ex.Message}");
            }
        }

        private void CopyFile(string sourcePath, string targetPath)
        {
            try
            {
                string fullSourcePath = Path.Combine(_scriptsFolder, sourcePath.TrimStart('/').Replace("/", "\\"));
                string fullTargetPath = Path.Combine(_scriptsFolder, targetPath.TrimStart('/').Replace("/", "\\"));
                
                if (File.Exists(fullSourcePath))
                {
                    File.Copy(fullSourcePath, fullTargetPath, true);
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = $"Copied file to {Path.GetFileName(targetPath)}",
                        success = true
                    });
                }
                else if (Directory.Exists(fullSourcePath))
                {
                    CopyDirectory(fullSourcePath, fullTargetPath);
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = $"Copied folder to {Path.GetFileName(targetPath)}",
                        success = true
                    });
                }
                else
                {
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = "Source not found",
                        success = false
                    });
                }
                
                SendScriptsList();
            }
            catch (Exception ex)
            {
                SendMessageToUI(new
                {
                    type = "notification",
                    message = $"Copy failed: {ex.Message}",
                    success = false
                });
            }
        }

        private void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDir))
            {
                string destDir = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, destDir);
            }
        }

        private void Updater()
        {
            string upbat = @"@echo off
setlocal enabledelayedexpansion

set ""github_url=https://github.com/REKMS-cttub/SynUI-Nucleus/releases/latest/download/Release.zip""
set ""temp_dir=%TEMP%\SynUI_Download""
set ""zip_file=Release.zip""
set ""max_retries=3""
set ""retry_delay=5""

if not exist ""!temp_dir!"" mkdir ""!temp_dir!""

:download_retry
set /a attempt+=1
echo Downloading Release.zip (Attempt !attempt! of %max_retries%)...

powershell -Command ""$webclient = New-Object System.Net.WebClient; $webclient.DownloadFile('!github_url!', '!cd!\!zip_file!')""

if %errorlevel% neq 0 (
    echo ERROR: Download failed!
    if !attempt! lss %max_retries% (
        echo Retrying in %retry_delay% seconds...
        timeout /t %retry_delay% /nobreak >nul
        goto download_retry
    )
    echo ERROR: Maximum download attempts reached!
    pause
    exit /b 1
)

echo Extracting files...
powershell -Command ""Expand-Archive -Path '!cd!\!zip_file!' -DestinationPath '!cd!' -Force"" >nul 2>&1

if %errorlevel% neq 0 (
    echo ERROR: Extraction failed!
    pause
    exit /b 1
)

echo Cleaning up...
rmdir /s /q ""!temp_dir!"" 2>nul

echo Operation completed successfully!
del ""!cd!\!zip_file!"" >nul
start """" synapse_x_v3.exe
";
            File.WriteAllText("GetSynUI.bat",upbat);
            Process process = new Process();

            process.StartInfo.FileName = "GetSynUI.bat";
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName("GetSynUI.bat");
            process.StartInfo.CreateNoWindow = false;
            process.StartInfo.UseShellExecute = false;
            process.Start();
            Environment.Exit(0000001);
        }
        private void HandleWindowAction(string action)
        {
            switch (action)
            {
                case "minimize":
                    this.WindowState = FormWindowState.Minimized;
                    break;
                    
                case "maximize":
                    ToggleMaximize();
                    break;
                    
                case "close":
                    this.Close();
                    break;
            }
        }

        private void ToggleMaximize()
        {
            if (_isMaximized)
            {
                this.Location = _originalLocation;
                this.Size = _originalSize;
                _isMaximized = false;
            }
            else
            {
                _originalLocation = this.Location;
                _originalSize = this.Size;
                this.Location = new Point(0, 0);
                this.Size = new Size(
                    Screen.PrimaryScreen.WorkingArea.Width,
                    Screen.PrimaryScreen.WorkingArea.Height
                );
                _isMaximized = true;
            }
        }

        private void SendScriptsList()
        {
            try
            {
                var files = new List<object>();
        
                if (_currentFolder != "/")
                {
                    string parentPath = _currentFolder.Substring(0, _currentFolder.LastIndexOf('/'));
                    if (string.IsNullOrEmpty(parentPath)) parentPath = "/";
            
                    files.Add(new {
                        name = ".. (Back)",
                        path = parentPath,
                        type = "folder-up"
                    });
                }
        
                string fullPath = Path.Combine(_scriptsFolder, _currentFolder.TrimStart('/').Replace("/", "\\"));
        
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                }
        
                var directories = Directory.GetDirectories(fullPath);
                foreach (var dir in directories)
                {
                    string dirName = Path.GetFileName(dir);
                    files.Add(new { 
                        name = dirName, 
                        path = $"{_currentFolder}/{dirName}",
                        type = "folder" 
                    });
                }
        
                var scriptFiles = Directory.GetFiles(fullPath, "*.lua");
                foreach (var file in scriptFiles)
                {
                    string fileName = Path.GetFileName(file);
                    files.Add(new { 
                        name = fileName, 
                        path = $"{_currentFolder}/{fileName}",
                        type = "file" 
                    });
                }
        
                var message = new { 
                    type = "file_list", 
                    files = files,
                    currentPath = _currentFolder
                };
        
                SendMessageToUI(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending file list: {ex.Message}");
            }
        }

        private async Task DetectProcess()
        {
            Debug.WriteLine("Detecting Process");
            string[] processNames = { "RobloxPlayerBeta", "Roblox", "RobloxGameClient" };
    
            while (true)
            {
                bool processFound = false;
                
                foreach (var name in processNames)
                {
                    Process[] processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0)
                    {
                        Debug.WriteLine("Process Found");
                        processFound = true;
                        break;
                    }
                }

                if (!processFound)
                {
                    SendInjectionResult(false, "Process Not Found");
                    return;
                }
                await Task.Delay(100);
            }
        }

        private void ExecuteCode(string code, string tabId)
        {
            try
            {
                string[] processNames = { "RobloxPlayerBeta", "Roblox", "RobloxGameClient" };
                bool processFound = false;
                
                foreach (var name in processNames)
                {
                    Process[] processes = Process.GetProcessesByName(name);
                    if (processes.Length > 0)
                    {
                        processFound = true;
                        break;
                    }
                }
                
                if (!processFound)
                {
                    SendMessageToUI(new
                    {
                        type = "execute_result",
                        success = false,
                        reason = "not injected",
                        tabId = tabId
                    });
                    SendInjectionResult(false, "");
                    return;
                }
                
                if (!nucleus.IsInjected())
                {
                    SendMessageToUI(new
                    {
                        type = "execute_result",
                        success = false,
                        reason = "Not injected",
                        tabId = tabId
                    });
                    return;
                }
                
                Debug.WriteLine($"Executing code in tab {tabId}:\n{code}");
                nucleus.Execute(code);
                SendMessageToUI(new
                {
                    type = "execute_result",
                    success = true,
                    tabId = tabId
                });
            }
            catch (Exception ex)
            {
                SendMessageToUI(new
                {
                    type = "execute_result",
                    success = false,
                    reason = ex.Message,
                    tabId = tabId
                });
            }
        }

        private void OpenFile(string tabId)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = "Lua Files (*.lua)|*.lua|All Files (*.*)|*.*";
                dialog.InitialDirectory = _scriptsFolder;
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    LoadFileToUI(dialog.FileName, tabId);
                }
            }
        }

        private void LoadFileToUI(string filePath, string tabId)
        {
            try
            {
                string content = File.ReadAllText(filePath);
                string fileName = Path.GetFileName(filePath);
        
                var message = new
                {
                    type = "file_loaded",
                    name = fileName,
                    content = content,
                    path = filePath.Replace(_scriptsFolder, "").Replace("\\", "/").TrimStart('/'),
                    tabId = tabId
                };
        
                SendMessageToUI(message);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading file: {ex.Message}");
            }
        }

        private void SaveFile(string content, string tabId)
        {
            if (string.IsNullOrEmpty(content))
            {
                MessageBox.Show("Editor content is empty!", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "Lua Files (*.lua)|*.lua|All Files (*.*)|*.*";
                dialog.InitialDirectory = _scriptsFolder;
                dialog.DefaultExt = "lua";
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        File.WriteAllText(dialog.FileName, content);
                        SendScriptsList();
                        
                        SendMessageToUI(new
                        {
                            type = "update_title",
                            title = Path.GetFileName(dialog.FileName),
                            path = dialog.FileName.Replace(_scriptsFolder, "").Replace("\\", "/").TrimStart('/'),
                            tabId = tabId
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving file: {ex.Message}", "Error", 
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void InjectSeliware()
        {               
            string[] processNames = { "RobloxPlayerBeta", "Roblox", "RobloxGameClient" };
            bool processFound = false;
                
            foreach (var name in processNames)
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    processFound = true;
                    break;
                }
            }
            
            if (!processFound)  
            {
                SendInjectionResult(false, "Failed to Inject : No process found");
                return;
            }

            if (processFound)
            {
                try
                {

                    nucleus.Inject();
                    nucleus.OnInjected += delegate
                    {
                        DetectProcess();
                        new LogHandler(this).SetupLogService();
                        SendInjectionResult(true, "Injected successfully");
                    };

                }
                catch (Exception ex)
                {
                    SendInjectionResult(false, ex.Message);
                }
            }
        }
        

        
        private void OnInjectionFailed(object sender, string error)
        {
            SendInjectionResult(false, error);
        }
        
        private void SendInjectionResult(bool success, string message)
        {
            this.Invoke((MethodInvoker)delegate {
                var result = new {
                    type = "inject_result",
                    success = success,
                    error = message
                };
                
                SendMessageToUI(result);
            });
        }

        private void LoadFile(string filePath, string tabId)
        {
            string fullPath = Path.Combine(_scriptsFolder, filePath.TrimStart('/').Replace("/", "\\"));
            LoadFileToUI(fullPath, tabId);
        }

        private void ExecuteFile(string filePath)
        {
            try
            {
                if (!nucleus.IsInjected())
                {
                    MessageBox.Show("Please inject first!", "Error", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                string fullPath = Path.Combine(_scriptsFolder, filePath.TrimStart('/').Replace("/", "\\"));
                string content = File.ReadAllText(fullPath);
                nucleus.Execute(content);
                
                Debug.WriteLine($"Executing file: {fullPath}\n{content}");
                MessageBox.Show($"Executed file: {Path.GetFileName(fullPath)}", "Success", 
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error executing file: {ex.Message}", "Error", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteFile(string filePath)
        {
            try
            {
                string fullPath = Path.Combine(_scriptsFolder, filePath.TrimStart('/').Replace("/", "\\"));
                
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    SendScriptsList();
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = $"Deleted file: {Path.GetFileName(fullPath)}",
                        success = true
                    });
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, true);
                    SendScriptsList();
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = $"Deleted folder: {Path.GetFileName(fullPath)}",
                        success = true
                    });
                }
            }
            catch (Exception ex)
            {
                SendMessageToUI(new
                {
                    type = "notification",
                    message = $"Error deleting: {ex.Message}",
                    success = false
                });
            }
        }
        
        private void CreateFolder(string folderName)
        {
            try
            {
                string fullPath = Path.Combine(_scriptsFolder, _currentFolder.TrimStart('/').Replace("/", "\\"), folderName);
                if (!Directory.Exists(fullPath))
                {
                    Directory.CreateDirectory(fullPath);
                    SendScriptsList();
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = $"Folder '{folderName}' created",
                        success = true
                    });
                }
            }
            catch (Exception ex)
            {
                SendMessageToUI(new
                {
                    type = "notification",
                    message = $"Error creating folder: {ex.Message}",
                    success = false
                });
            }
        }

        private void CreateFile(string fileName)
        {
            try
            {
                if (!fileName.EndsWith(".lua")) fileName += ".lua";
                string fullPath = Path.Combine(_scriptsFolder, _currentFolder.TrimStart('/').Replace("/", "\\"), fileName);
                if (!File.Exists(fullPath))
                {
                    File.WriteAllText(fullPath, $"-- {fileName}\n-- Created at {DateTime.Now}\n\n");
                    SendScriptsList();
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = $"File '{fileName}' created",
                        success = true
                    });
                }
            }
            catch (Exception ex)
            {
                SendMessageToUI(new
                {
                    type = "notification",
                    message = $"Error creating file: {ex.Message}",
                    success = false
                });
            }
        }

        private void RenameTab(string tabId, string newName)
        {
            SendMessageToUI(new
            {
                type = "tab_renamed",
                tabId = tabId,
                name = newName
            });
        }
        
        private void RenameFile(string oldPath, string newName)
        {
            try
            {
                string fullOldPath = Path.Combine(_scriptsFolder, oldPath.TrimStart('/').Replace("/", "\\"));
                string newPath = Path.Combine(Path.GetDirectoryName(fullOldPath), newName);
                
                if (File.Exists(fullOldPath))
                {
                    File.Move(fullOldPath, newPath);
                }
                else if (Directory.Exists(fullOldPath))
                {
                    Directory.Move(fullOldPath, newPath);
                }
                else
                {
                    SendMessageToUI(new
                    {
                        type = "notification",
                        message = "File or folder not found",
                        success = false
                    });
                    return;
                }
                
                SendScriptsList();
                SendMessageToUI(new
                {
                    type = "notification",
                    message = $"Renamed to {newName}",
                    success = true
                });
            }
            catch (Exception ex)
            {
                SendMessageToUI(new
                {
                    type = "notification",
                    message = $"Error renaming: {ex.Message}",
                    success = false
                });
            }
        }
        
        public class Settings
        {
            public bool TopMost { get; set; }
            public bool AutoInject { get; set; }
        }
        
        private void LoadSettings()
        {
            _settingsPath = Path.Combine(Application.UserAppDataPath, "synapse_settings.json");
            if (File.Exists(_settingsPath))
            {
                try
                {
                    string json = File.ReadAllText(_settingsPath);
                    var settings = JsonConvert.DeserializeObject<Settings>(json);
                    
                    if (settings != null)
                    {
                        this.TopMost = settings.TopMost;
                        _autoInjectEnabled = settings.AutoInject;
                        if (_autoInjectEnabled)
                        {
                            StartAutoInjectLoop();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error loading settings: {ex.Message}");
                }
            }
        }
        
        private void SaveSettings()
        {
            try
            {
                var settings = new Settings
                {
                    TopMost = this.TopMost,
                    AutoInject = _autoInjectEnabled
                };
                
                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_settingsPath, json);
                Debug.WriteLine("Settings saved successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }
        
        private void StartAutoInjectLoop()
        {
            if (_autoInjectThread != null && _autoInjectThread.IsAlive)
                return;
        
            _isRunning = true;
    
            _autoInjectThread = new Thread(() => 
            {
                while (_isRunning && _autoInjectEnabled)
                {
                    try
                    {
                        string[] processNames = { "RobloxPlayerBeta", "Roblox", "RobloxGameClient" };
                        List<Process> targetProcesses = new List<Process>();
                        
                        foreach (var name in processNames)
                        {
                            var processes = Process.GetProcessesByName(name);
                            foreach (var process in processes)
                            {
                                if (!_injectedProcessIds.Contains(process.Id))
                                {
                                    targetProcesses.Add(process);
                                }
                            }
                        }
                        
                        if (targetProcesses.Count > 0)
                        {
                            Debug.WriteLine("Auto-injecting into Roblox process...");
                            
                            this.Invoke((MethodInvoker)delegate {
                                try
                                {
                                    nucleus.Inject();
                                    foreach (var process in targetProcesses)
                                    {
                                        _injectedProcessIds.Add(process.Id);
                                    }
                                    nucleus.OnInjected += delegate
                                    {
                                        SendInjectionResult(true, "Auto-injected successfully");
                                    };
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"Auto-inject failed: {ex.Message}");
                                    SendInjectionResult(false, ex.Message);
                                }
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"AutoInject error: {ex.Message}");
                    }
            
                    Thread.Sleep(5000);
                }
            });
    
            _autoInjectThread.IsBackground = true;
            _autoInjectThread.Start();
        }
        
        private void StopAutoInjectLoop()
        {
            _isRunning = false;
        }
        
        private void SendCurrentSettings()
        {
            var settings = new
            {
                type = "settings",
                topmost = this.TopMost,
                autoinject = _autoInjectEnabled
            };
            
            SendMessageToUI(settings);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            _isRunning = false;
            SaveSettings();
            
            if (_webSocketServer != null && _webSocketServer.IsListening)
            {
                _webSocketServer.Stop();
            }
        }
    }

    public class UIWebSocket : WebSocketBehavior
    {
        private MainForm _mainForm;

        public UIWebSocket()
        {
        }

        public UIWebSocket(MainForm mainForm)
        {
            _mainForm = mainForm;
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (_mainForm != null && e.IsText)
            {
                _mainForm.HandleMessageFromUI(e.Data);
            }
        }
    }
}