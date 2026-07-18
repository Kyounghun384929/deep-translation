using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DeepTranslation.Models;
using Microsoft.Win32;

namespace DeepTranslation.Services;

/// <summary>
/// llama.cpp의 llama-server.exe를 앱이 직접 관리하는 내장 번역 엔진.
/// 온디맨드로 기동해 OpenAI 호환 API 주소(baseUrl)를 제공하고,
/// 유휴 시간이 지나면 프로세스를 내려 RAM/VRAM을 완전히 회수한다.
/// </summary>
public static class EmbeddedEngine
{
    // llama.cpp 릴리스 태그 고정 — zip URL·폴더 이름에 함께 쓰인다
    private const string LlamaTag = "b10066";
    private const string VulkanZipUrl =
        $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaTag}/llama-{LlamaTag}-bin-win-vulkan-x64.zip";
    private const string CpuZipUrl =
        $"https://github.com/ggml-org/llama.cpp/releases/download/{LlamaTag}/llama-{LlamaTag}-bin-win-cpu-x64.zip";

    // 디스크에서 첫 로드는 느릴 수 있어 넉넉하게 잡는다
    private const int HealthTimeoutSeconds = 180;

    private static readonly object Sync = new();
    private static readonly HttpClient HealthHttp = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static Process? _proc;
    private static StreamWriter? _log;
    private static string _modelId = "";
    private static string _baseUrl = "";
    private static Timer? _idleTimer;
    private static int _idleMinutes = 5;
    private static long _lastActivityTick;

    // ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION — 앱 컨트롤 정책(SAC 등)이 실행 파일을 차단
    private const int ErrorSystemIntegrityPolicyViolation = 4551;

    private const string SacBlockedMessage =
        "Windows 스마트 앱 컨트롤이 번역 엔진(llama-server.exe)의 실행을 차단했습니다.\n" +
        "스마트 앱 컨트롤은 앱별 예외를 지원하지 않습니다.\n\n" +
        "해결 방법:\n" +
        "1. 설정 → 번역 엔진에서 'LM Studio 서버' 모드를 사용하세요 (서명된 앱이라 차단되지 않습니다).\n" +
        "2. 또는 Windows 설정 → 개인 정보 및 보안 → Windows 보안 → 앱 및 브라우저 컨트롤에서 " +
        "스마트 앱 컨트롤을 끄세요.";

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepTranslation");
    private static string LlamaDir => Path.Combine(AppDataDir, "llama");
    private static string LogPath => Path.Combine(AppDataDir, "llama-server.log");
    private static string CpuMarkerPath => Path.Combine(LlamaDir, "use-cpu.txt");

    /// <summary>현재 실행 중인 엔진의 모델 Id. 실행 중이 아니면 빈 문자열.</summary>
    public static string RunningModelId
    {
        get { lock (Sync) return _proc is { HasExited: false } ? _modelId : ""; }
    }

    /// <summary>
    /// Windows 스마트 앱 컨트롤(SAC)이 켜져 있는지 여부.
    /// 켜져 있으면 무서명인 llama-server.exe 실행이 차단된다 (앱별 예외 등록 불가).
    /// </summary>
    public static bool IsSmartAppControlOn
    {
        get
        {
            // VerifiedAndReputablePolicyState: 0=꺼짐, 1=켜짐(차단), 2=평가 모드(차단 안 함)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy");
                return key?.GetValue("VerifiedAndReputablePolicyState") is 1;
            }
            catch
            {
                return false; // 읽기 실패는 꺼짐으로 간주
            }
        }
    }

    /// <summary>
    /// 설정된 내장 모델로 llama-server가 떠 있도록 보장하고 baseUrl을 반환한다.
    /// 같은 모델로 이미 실행 중이면 즉시 반환. 바이너리는 최초 1회 자동 다운로드.
    /// 기동 실패 시 폴백: Vulkan(-ngl 99) → Vulkan(-ngl 0, VRAM 부족 대응) → CPU 빌드.
    /// </summary>
    public static async Task<string> EnsureRunningAsync(AppSettings settings, Action<string>? onStatus, CancellationToken ct)
    {
        var model = ModelCatalog.Find(settings.EmbeddedModelId)
            ?? throw new LmStudioException("알 수 없는 내장 모델입니다: " + settings.EmbeddedModelId);

        lock (Sync)
        {
            _idleMinutes = settings.IdleUnloadMinutes;
            if (_proc is { HasExited: false } && _modelId == model.Id)
            {
                ResetIdleTimerLocked();
                return _baseUrl;
            }
        }

        if (!model.IsDownloaded)
            throw new LmStudioException(
                "모델이 아직 다운로드되지 않았습니다.\n" +
                $"설정 → 번역 엔진에서 '{model.DisplayName}' 모델을 다운로드하세요.");

        Stop(); // 다른 모델이 떠 있으면 내리고 새로 기동

        // CPU 빌드로만 성공한 적이 있으면(use-cpu.txt 마커) Vulkan 시도를 건너뛴다
        bool cpuOnly = File.Exists(CpuMarkerPath);
        var attempts = cpuOnly
            ? new[] { (Cpu: true, Ngl: 0) }
            : new[] { (Cpu: false, Ngl: 99), (Cpu: false, Ngl: 0), (Cpu: true, Ngl: 0) };

        foreach (var (cpu, ngl) in attempts)
        {
            ct.ThrowIfCancellationRequested();
            string exe = await EnsureBinaryAsync(cpu, onStatus, ct);
            if (await TryStartAsync(model, exe, ngl, onStatus, ct))
            {
                if (cpu && !cpuOnly)
                {
                    // 다음 실행부터 바로 CPU 빌드를 쓰도록 마커를 남긴다
                    try { File.WriteAllText(CpuMarkerPath, "Vulkan 기동 실패로 CPU 빌드를 사용합니다."); } catch { }
                }
                lock (Sync) return _baseUrl;
            }
        }

        // SAC이 DLL만 차단하는 변형에서는 프로세스가 떴다가 즉사해 여기까지 온다 — 같은 안내를 앞에 붙인다
        string sacHint = IsSmartAppControlOn ? SacBlockedMessage + "\n\n" : "";
        throw new LmStudioException("내장 번역 엔진을 시작하지 못했습니다.\n\n" + sacHint + TailLog());
    }

    /// <summary>번역 활동이 있었음을 알린다 — 유휴 언로드 타이머를 리셋한다.</summary>
    public static void NotifyActivity()
    {
        lock (Sync)
        {
            if (_proc is { HasExited: false }) ResetIdleTimerLocked();
        }
    }

    /// <summary>설정 변경 반영 — LM Studio 모드로 바뀌었거나 선택 모델이 실행 중 모델과 다르면 엔진을 내린다.</summary>
    public static void ApplySettings(AppSettings settings)
    {
        lock (Sync)
        {
            if (_proc == null) return;
            _idleMinutes = settings.IdleUnloadMinutes;
            if (settings.EngineMode != "Embedded" || settings.EmbeddedModelId != _modelId)
                StopLocked();
            else
                ResetIdleTimerLocked(); // 유휴 시간 변경 즉시 반영
        }
    }

    /// <summary>엔진 프로세스를 종료하고 메모리를 회수한다. 앱 종료·모델 전환·유휴 언로드에서 호출.</summary>
    public static void Stop()
    {
        lock (Sync) StopLocked();
    }

    private static void StopLocked()
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (_proc != null)
        {
            try { if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); _proc.WaitForExit(3000); } } catch { }
            try { _proc.Dispose(); } catch { }
            _proc = null;
        }
        try { _log?.Dispose(); } catch { }
        _log = null;
        _modelId = "";
        _baseUrl = "";
    }

    // ---- 기동 ----

    private static async Task<bool> TryStartAsync(ModelCatalog.ModelInfo model, string exe, int ngl,
        Action<string>? onStatus, CancellationToken ct)
    {
        int port = GetFreePort();
        string args = $"-m \"{model.FilePath}\" --host 127.0.0.1 --port {port} " +
                      $"-ngl {ngl} --ctx-size 4096 -a {model.Id} --no-webui";

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            }
        };

        // stdout/stderr를 로그 파일에 기록 (기동마다 덮어쓰기)
        var log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        void WriteLog(string? line)
        {
            if (line == null) return;
            lock (log) { try { log.WriteLine(line); } catch { } } // 종료 후 늦게 도착한 줄은 무시
        }
        proc.OutputDataReceived += (_, e) => WriteLog(e.Data);
        proc.ErrorDataReceived += (_, e) => WriteLog(e.Data);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            log.Dispose();
            // SAC 차단이면 폴백(CPU 빌드)도 같은 무서명이라 소용없다 — 전용 안내로 즉시 실패
            if (ex is Win32Exception { NativeErrorCode: ErrorSystemIntegrityPolicyViolation } || IsSmartAppControlOn)
                throw new LmStudioException(SacBlockedMessage, ex);
            throw new LmStudioException("llama-server 실행에 실패했습니다: " + ex.Message, ex);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        onStatus?.Invoke($"모델 로딩 중… ({model.DisplayName})");
        string baseUrl = $"http://127.0.0.1:{port}";
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalSeconds < HealthTimeoutSeconds)
            {
                ct.ThrowIfCancellationRequested();
                if (proc.HasExited) break; // healthy 전에 종료 — 다음 폴백 단계로

                try
                {
                    using var resp = await HealthHttp.GetAsync($"{baseUrl}/health", ct);
                    if (resp.IsSuccessStatusCode &&
                        (await resp.Content.ReadAsStringAsync(ct)).Contains("\"ok\""))
                    {
                        lock (Sync)
                        {
                            _proc = proc;
                            _log = log;
                            _modelId = model.Id;
                            _baseUrl = baseUrl;
                            ResetIdleTimerLocked();
                        }
                        return true;
                    }
                }
                catch (HttpRequestException) { }                                       // 아직 리슨 전 — 재시도
                catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }   // 요청 타임아웃 — 재시도

                await Task.Delay(250, ct);
            }
        }
        catch
        {
            KillQuiet(proc, log); // 취소 등 — 뜨다 만 프로세스를 정리하고 전파
            throw;
        }

        KillQuiet(proc, log); // 타임아웃 또는 조기 종료 — 실패
        return false;
    }

    private static void KillQuiet(Process proc, StreamWriter log)
    {
        try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); } } catch { }
        try { proc.Dispose(); } catch { }
        lock (log) { try { log.Dispose(); } catch { } }
    }

    // ---- 바이너리 준비 ----

    /// <summary>llama-server.exe 경로를 반환한다. 없으면 릴리스 zip을 최초 1회 내려받아 푼다.</summary>
    private static async Task<string> EnsureBinaryAsync(bool cpu, Action<string>? onStatus, CancellationToken ct)
    {
        string dir = Path.Combine(LlamaDir, LlamaTag, cpu ? "cpu" : "vulkan");
        string? exe = FindServerExe(dir);
        if (exe != null) return exe;

        Directory.CreateDirectory(dir);
        string zipPath = dir + ".zip";
        onStatus?.Invoke("번역 엔진 다운로드 중… (1회)");
        await ModelDownloader.DownloadAsync(cpu ? CpuZipUrl : VulkanZipUrl, zipPath, 0,
            (received, _) => onStatus?.Invoke($"번역 엔진 다운로드 중… {received / 1048576} MB (1회)"), ct);

        // zip 루트 구조가 릴리스마다 다를 수 있어 해제 후 exe를 탐색해 경로를 확정한다
        ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true);
        try { File.Delete(zipPath); } catch { }

        return FindServerExe(dir)
            ?? throw new LmStudioException("다운로드한 번역 엔진에서 llama-server.exe를 찾지 못했습니다.");
    }

    private static string? FindServerExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    // ---- 유틸 ----

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // 유휴 타이머 리셋. 경과 시 프로세스를 내려 메모리를 회수한다.
    // (kill 후에도 모델 파일이 OS 페이지 캐시에 남아 재기동은 보통 1~2초로 빠르다.)
    private static void ResetIdleTimerLocked()
    {
        _lastActivityTick = Environment.TickCount64;
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (_idleMinutes <= 0) return; // 0 = 언로드 안 함
        _idleTimer = new Timer(OnIdleTimer, null, TimeSpan.FromMinutes(_idleMinutes), Timeout.InfiniteTimeSpan);
    }

    private static void OnIdleTimer(object? state)
    {
        lock (Sync)
        {
            if (_proc == null) return;
            // 리셋 직후 도착한 낡은 콜백이면 무시 (새 타이머가 이미 걸려 있다)
            if (Environment.TickCount64 - _lastActivityTick < (long)_idleMinutes * 60000 - 500) return;
            StopLocked();
        }
    }

    // 기동 실패 원인 표시용 — 로그 파일 마지막 몇 줄
    private static string TailLog()
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                if (line.Length > 0) lines.Add(line);
            return "로그 (마지막 부분, " + LogPath + "):\n" + string.Join("\n", lines.TakeLast(6));
        }
        catch
        {
            return "(로그를 읽을 수 없습니다: " + LogPath + ")";
        }
    }
}
