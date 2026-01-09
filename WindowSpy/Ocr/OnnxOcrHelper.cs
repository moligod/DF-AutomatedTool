using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace WindowSpy.Ocr;

public class OnnxOcrHelper : IDisposable
{
    private readonly string _pythonExe;
    private readonly string _onnxRoot;
    private readonly string _cliPath;
    
    private readonly string? _detModelDir;
    private readonly string? _recModelDir;
    private readonly string? _recCharDictPath;
    private readonly bool _useAngleCls;
    private bool _useGpu;

    private Process? _process;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public Action<string>? Logger { get; set; }

    public bool UseGpu
    {
        get => _useGpu;
        set
        {
            if (_useGpu != value)
            {
                _useGpu = value;
                // 如果进程已启动，需要重启以应用更改
                if (_process != null)
                {
                    try { _process.Kill(); } catch { }
                    _process.Dispose();
                    _process = null;
                }
            }
        }
    }

    public OnnxOcrHelper(string? pythonExe = null, string? onnxRoot = null, string? cliPath = null, 
        string? detModelDir = null, string? recModelDir = null, string? recCharDictPath = null, bool useAngleCls = false)
    {
        _pythonExe = "python"; // 默认尝试系统PATH中的python
        // 如果系统PATH里没有，或者用户在本地有嵌入式python，可以在这里指定
        // 但最常见的问题是用户根本没装Python，或者装了但没加到PATH
        
        _onnxRoot = onnxRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts");
        _cliPath = cliPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scripts", "onnx_ocr_cli.py");
        
        _detModelDir = detModelDir ?? Path.Combine(_onnxRoot, "onnxocr", "models", "ppocrv5", "det.onnx");
        _recModelDir = recModelDir ?? Path.Combine(_onnxRoot, "onnxocr", "models", "ppocrv5", "rec.onnx");
        _recCharDictPath = recCharDictPath ?? Path.Combine(_onnxRoot, "onnxocr", "models", "ppocrv5", "ppocrv5_dict.txt");
        _useAngleCls = useAngleCls;
    }

    private void Log(string message)
    {
        Logger?.Invoke(message);
    }

    private async Task EnsureProcessStartedAsync()
    {
        if (_process != null && !_process.HasExited) return;

        if (_process != null)
        {
            try { _process.Kill(); } catch { }
            _process.Dispose();
        }

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        
        // 检查是否存在打包后的独立exe (dist/onnx_ocr_cli/onnx_ocr_cli.exe)
        string bundledExe = Path.Combine(_onnxRoot, "dist", "onnx_ocr_cli", "onnx_ocr_cli.exe");
        if (File.Exists(bundledExe))
        {
            psi.FileName = bundledExe;
            psi.ArgumentList.Add("--onnx_root");
            psi.ArgumentList.Add(_onnxRoot);
            psi.ArgumentList.Add("--interactive");
            Log($"[C# Info] Using bundled OCR engine: {bundledExe}");
        }
        else
        {
            psi.FileName = _pythonExe;
            psi.ArgumentList.Add(_cliPath);
            psi.ArgumentList.Add("--onnx_root");
            psi.ArgumentList.Add(_onnxRoot);
            psi.ArgumentList.Add("--interactive");
            Log($"[C# Info] Using system Python: {_pythonExe} {_cliPath}");
        }
        
        // 设置 Python 不缓冲输出
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        
        psi.ArgumentList.Add("--use_angle_cls");
        psi.ArgumentList.Add(_useAngleCls ? "true" : "false");

        if (!string.IsNullOrEmpty(_detModelDir))
        {
            psi.ArgumentList.Add("--det_model_dir");
            psi.ArgumentList.Add(_detModelDir);
        }
        if (!string.IsNullOrEmpty(_recModelDir))
        {
            psi.ArgumentList.Add("--rec_model_dir");
            psi.ArgumentList.Add(_recModelDir);
        }
        if (!string.IsNullOrEmpty(_recCharDictPath))
        {
            psi.ArgumentList.Add("--rec_char_dict_path");
            psi.ArgumentList.Add(_recCharDictPath);
        }

        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["ONNX_USE_GPU"] = _useGpu ? "1" : "0"; 
        
        // 关键修复：将程序根目录添加到 PATH，确保 bundled DLLs 能被找到
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string scriptsDir = Path.Combine(baseDir, "Scripts");
        // 针对 PyInstaller _internal 目录的特殊处理 (如果存在)
        // PyInstaller 解压后的依赖通常在 _internal 目录下，也可能在 dist/onnx_ocr_cli/_internal
        // 我们尝试将可能的 _internal 路径也加入 PATH
        string internalDir = Path.Combine(scriptsDir, "dist", "onnx_ocr_cli", "_internal");
        string internalDir2 = Path.Combine(scriptsDir, "_internal");

        string currentPath = psi.EnvironmentVariables.ContainsKey("PATH") ? psi.EnvironmentVariables["PATH"]! : "";
        
        // 将根目录、Scripts目录和可能的 _internal 目录都加入PATH
        var paths = new List<string> { baseDir, scriptsDir, internalDir, internalDir2, currentPath };
        psi.EnvironmentVariables["PATH"] = string.Join(Path.PathSeparator.ToString(), paths);
        
        _process = Process.Start(psi);
        if (_process == null) throw new Exception("Failed to start python process");

        var stderrBuilder = new StringBuilder();
        _process.ErrorDataReceived += (sender, args) => 
        { 
            if (!string.IsNullOrEmpty(args.Data)) 
            {
                // 1. 移除空字符
                string cleanLog = args.Data.Replace("\0", "");
                // 2. 过滤 ANSI 颜色代码
                cleanLog = System.Text.RegularExpressions.Regex.Replace(cleanLog, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");
                // 3. 移除其他不可见控制字符 (保留换行、制表符、可打印字符和扩展字符)
                cleanLog = new string(cleanLog.Where(c => (c >= 32 && c != 127) || c == '\n' || c == '\r' || c == '\t' || c > 127).ToArray());
                
                lock(stderrBuilder) stderrBuilder.AppendLine(cleanLog); 
                Log($"[Python Stderr] {cleanLog}");
            }
        };
        _process.BeginErrorReadLine();

        // 异步等待 READY 信号
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        bool ready = false;
        Task<string?>? pendingReadTask = null;
        
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (_process.HasExited) throw new Exception("Python process exited unexpectedly during startup.");

                // 使用 ReadLineAsync 读取 stdout，确保复用未完成的任务
                if (pendingReadTask == null)
                {
                    pendingReadTask = _process.StandardOutput.ReadLineAsync();
                }

                var completedTask = await Task.WhenAny(pendingReadTask, Task.Delay(1000, cts.Token)); // 每秒检查一次超时或取消

                if (completedTask == pendingReadTask)
                {
                    var line = await pendingReadTask;
                    pendingReadTask = null; // 任务已完成，置空以便下次读取

                    if (line == null) break; //流结束

                    // 过滤 ANSI 颜色代码和控制字符
                    line = line.Replace("\0", "");
                    line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");
                    line = new string(line.Where(c => (c >= 32 && c != 127) || c == '\n' || c == '\r' || c == '\t' || c > 127).ToArray());
                    
                    var trimmed = line.Trim();
                    if (trimmed == "READY")
                    {
                        ready = true;
                        break;
                    }
                    Log($"[Python Init] {trimmed}"); 
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout
        }

        if (!ready)
        {
            try { _process.Kill(); } catch {}
            string err;
            lock(stderrBuilder) err = stderrBuilder.ToString();
            throw new Exception($"Python process startup timed out (60s) or failed to send READY. Last Stderr: {err}");
        }
    }

    public async Task<List<OcrRegion>> OcrAsync(Mat src)
    {
        byte[] imgBytes;
        // Use .bmp for faster encoding (no compression) compared to .png
        Cv2.ImEncode(".bmp", src, out imgBytes);
        string base64Str = Convert.ToBase64String(imgBytes);

        await _semaphore.WaitAsync();
        try
        {
            // 如果进程不存在，先启动并等待预热完成
            if (_process == null || _process.HasExited)
            {
                Log("[C# Info] Starting OCR engine (this may take a few seconds for GPU warmup)...");
                await EnsureProcessStartedAsync();
            }
            
            if (_process == null) throw new Exception("Process is null");

            // 增加日志：发送图像路径给Python
            Log($"[C# Info] Sending image to Python via Memory (Base64 len: {base64Str.Length})");

            await _process.StandardInput.WriteLineAsync("base64:" + base64Str);
            await _process.StandardInput.FlushAsync();
                
                var readTask = _process.StandardOutput.ReadLineAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120)); 
                
                var completedTask = await Task.WhenAny(readTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    Log("[C# Error] OCR timeout triggered.");
                    try { _process.Kill(); } catch {} 
                    _process.Dispose(); 
                    _process = null;
                    throw new Exception("OCR inference timed out");
                }

                var line = await readTask;
                if (string.IsNullOrEmpty(line))
                {
                     try { _process.Kill(); } catch {} 
                    _process.Dispose(); 
                    _process = null;
                    throw new Exception("Python process returned empty line (crash?)");
                }

                // 增加日志清洗 (以防 stdout 也包含不可见字符)
                line = line.Replace("\0", "");
                line = System.Text.RegularExpressions.Regex.Replace(line, @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", "");
                line = new string(line.Where(c => (c >= 32 && c != 127) || c == '\n' || c == '\r' || c == '\t' || c > 127).ToArray());

                if (line.StartsWith("ERROR:"))
                {
                    throw new Exception($"Python error: {line}");
                }
                
                // 处理可能包含错误信息的JSON响应
                if (line.Contains("\"error\":"))
                {
                    try 
                    {
                         using var doc = JsonDocument.Parse(line);
                         if (doc.RootElement.TryGetProperty("error", out var errEl))
                         {
                             throw new Exception($"Python error: {errEl.GetString()}");
                         }
                    }
                    catch (JsonException) { } // 如果不是JSON或解析失败，继续后面的处理
                }

                try 
                {
                    var result = JsonSerializer.Deserialize<List<PythonOcrResult>>(line);
                    var regions = new List<OcrRegion>();
                    if (result != null)
                    {
                        foreach (var item in result)
                        {
                            int x = int.MaxValue, y = int.MaxValue, w = 0, h = 0;
                            int maxX = 0, maxY = 0;
                            
                            foreach(var pt in item.box)
                            {
                                int px = (int)pt[0];
                                int py = (int)pt[1];
                                if (px < x) x = px;
                                if (py < y) y = py;
                                if (px > maxX) maxX = px;
                                if (py > maxY) maxY = py;
                            }
                            w = maxX - x;
                            h = maxY - y;

                            regions.Add(new OcrRegion
                            {
                                Rect = new Rect(x, y, w, h),
                                Text = item.text,
                                Score = item.score
                            });
                        }
                    }
                    return regions;
                }
                catch (JsonException ex)
                {
                     throw new Exception($"Failed to parse OCR result: {line}", ex);
                }
            }
            finally
            {
                _semaphore.Release();
            }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _semaphore.Dispose();
        
        if (_process != null)
        {
            try { _process.Kill(); } catch { }
            _process.Dispose();
        }
    }

    private class PythonOcrResult
    {
        [JsonPropertyName("box")]
        public List<List<float>> box { get; set; } = new();
        [JsonPropertyName("text")]
        public string text { get; set; } = "";
        [JsonPropertyName("score")]
        public float score { get; set; }
    }
}

public class OcrRegion
{
    public Rect Rect { get; set; }
    public string Text { get; set; } = "";
    public float Score { get; set; }
}
