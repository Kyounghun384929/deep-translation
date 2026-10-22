using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
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
    // 앱에 고정된 llama.cpp 릴리스 태그 — 사용자가 업데이트하면 settings.LlamaVersion이 우선한다
    public const string DefaultTag = "b10066";
    private const string LatestReleaseApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest";

    /// <summary>현재 사용할 llama.cpp 태그 — zip URL·설치 폴더 이름에 함께 쓰인다.</summary>
    public static string Tag => App.Settings.LlamaVersion is { Length: > 0 } v ? v : DefaultTag;

    private static string ZipName(string tag, bool cpu) => $"llama-{tag}-bin-win-{(cpu ? "cpu" : "vulkan")}-x64.zip";
    private static string ZipUrl(string tag, bool cpu) =>
        $"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{ZipName(tag, cpu)}";

    // 디스크에서 첫 로드는 느릴 수 있어 넉넉하게 잡는다
    private const int HealthTimeoutSeconds = 180;

    private static readonly object Sync = new();
    private static readonly HttpClient HealthHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    // GitHub API는 User-Agent가 없으면 403을 반환한다
    private static readonly HttpClient ApiHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", "DeepTranslation" } },
    };

    private static Process? _proc;
    private static StreamWriter? _log;
    private static string _modelId = "";
    private static int _ctxSize;
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
        "1. 설정 → 번역 엔진에서 '외부 서버' 모드로 LM Studio에 연결하세요 (서명된 앱이라 차단되지 않습니다).\n" +
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
        var model = ResolveModel(settings);

        lock (Sync)
        {
            _idleMinutes = settings.IdleUnloadMinutes;
            if (_proc is { HasExited: false } && _modelId == model.Id && _ctxSize == settings.ContextSize)
            {
                ResetIdleTimerLocked();
                return _baseUrl;
            }
        }

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
            if (await TryStartAsync(model, exe, ngl, settings.ContextSize, onStatus, ct))
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

    /// <summary>설정의 내장 모델을 찾고 파일이 준비됐는지 확인한다 (OllamaEngine과 공용).</summary>
    internal static ModelCatalog.ModelInfo ResolveModel(AppSettings settings)
    {
        var model = ModelCatalog.Resolve(settings) ?? throw new LmStudioException(
            settings.EmbeddedModelId == ModelCatalog.CustomId
                ? "사용할 GGUF 파일이 지정되지 않았습니다.\n설정 → 번역 엔진에서 파일을 선택하세요."
                : "알 수 없는 내장 모델입니다: " + settings.EmbeddedModelId);
        if (!model.IsDownloaded)
            throw new LmStudioException(model.IsCustom
                ? $"GGUF 파일을 찾을 수 없습니다:\n{model.FilePath}"
                : "모델이 아직 다운로드되지 않았습니다.\n" +
                  $"설정 → 번역 엔진에서 '{model.DisplayName}' 모델을 다운로드하세요.");
        return model;
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
            if (settings.EngineMode != "Embedded" || ModelCatalog.Resolve(settings)?.Id != _modelId || settings.ContextSize != _ctxSize)
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

    private static async Task<bool> TryStartAsync(ModelCatalog.ModelInfo model, string exe, int ngl, int ctxSize,
        Action<string>? onStatus, CancellationToken ct)
    {
        int port = GetFreePort();
        // -np 1: 사용자 1명·요청 직렬화(LlmGuard) — 슬롯 하나가 컨텍스트 전체를 쓰고 같은 시스템 프롬프트 KV를 재사용한다
        // --cache-ram 0: 기본값은 지난 프롬프트를 RAM에 최대 8GB까지 쌓는다 — 상주 앱에는 낭비
        string args = $"-m \"{model.FilePath}\" --host 127.0.0.1 --port {port} " +
                      $"-ngl {ngl} --ctx-size {ctxSize} -np 1 --cache-ram 0 -a {model.Id} --no-webui";
        // qwen3.5는 thinking이 기본 켜져 컨텍스트를 소진할 때까지 생각만 한다 — 템플릿에서 꺼야 즉답한다
        // (--reasoning-budget 0만으로는 안 멈춤, llama-server는 chat-template-kwargs가 정상 동작)
        // 사용자 GGUF도 잡히도록 파일 이름으로 판별한다
        if (Path.GetFileName(model.FilePath).Contains("qwen3.5", StringComparison.OrdinalIgnoreCase))
            args += " --jinja --chat-template-kwargs \"{\\\"enable_thinking\\\":false}\"";

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
            ChildJob.Attach(proc); // 앱이 죽으면 엔진도 함께 종료
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
                            _ctxSize = ctxSize;
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

    private static string BinDir(bool cpu, string? tag = null) => Path.Combine(LlamaDir, tag ?? Tag, cpu ? "cpu" : "vulkan");

    /// <summary>엔진 바이너리 설치 여부 (Vulkan 빌드 기준 — CPU 폴백 빌드는 필요 시 자동 다운로드).</summary>
    public static bool IsBinaryInstalled => FindServerExe(BinDir(cpu: false)) != null;

    /// <summary>
    /// 엔진 바이너리(Vulkan 빌드, 약 33MB)를 내려받아 설치한다
    /// (설정 창의 사전 다운로드용, 이미 설치돼 있으면 즉시 반환).
    /// onProgress에는 ModelDownloader의 (받은 바이트, 전체 바이트)가 그대로 전달된다.
    /// </summary>
    public static async Task DownloadBinaryAsync(Action<long, long>? onProgress, CancellationToken ct)
    {
        if (!IsBinaryInstalled) await DownloadBuildAsync(cpu: false, onProgress, ct);
    }

    // 지정 빌드의 zip을 내려받아 풀고 llama-server.exe 경로를 반환한다
    private static async Task<string> DownloadBuildAsync(bool cpu, Action<long, long>? onProgress, CancellationToken ct,
        string? tag = null)
    {
        tag ??= Tag;
        string dir = BinDir(cpu, tag);
        Directory.CreateDirectory(dir);
        string zipPath = dir + ".zip";
        await ModelDownloader.DownloadAsync(ZipUrl(tag, cpu), zipPath, 0, onProgress, ct);

        // zip 루트 구조가 릴리스마다 다를 수 있어 해제 후 exe를 탐색해 경로를 확정한다.
        // 추출은 CPU/IO 작업 — UI 스레드에서 호출돼도 창이 멎지 않게 스레드 풀에서 수행한다.
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, dir, overwriteFiles: true), ct);
        try { File.Delete(zipPath); } catch { }

        return FindServerExe(dir)
            ?? throw new LmStudioException("다운로드한 번역 엔진에서 llama-server.exe를 찾지 못했습니다.");
    }

    /// <summary>llama-server.exe 경로를 반환한다. 없으면 릴리스 zip을 최초 1회 내려받아 푼다.</summary>
    private static async Task<string> EnsureBinaryAsync(bool cpu, Action<string>? onStatus, CancellationToken ct)
    {
        string? exe = FindServerExe(BinDir(cpu));
        if (exe != null) return exe;

        onStatus?.Invoke("번역 엔진 다운로드 중… (1회)");
        return await DownloadBuildAsync(cpu,
            (received, _) => onStatus?.Invoke($"번역 엔진 다운로드 중… {received / 1048576} MB (1회)"), ct);
    }

    private static string? FindServerExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    // ---- 엔진 업데이트 ----

    /// <summary>GPU 기동에 실패해 CPU 빌드로 고정된 상태인지 (use-cpu.txt 마커).</summary>
    public static bool IsCpuFallback => File.Exists(CpuMarkerPath);

    /// <summary>CPU 고정을 풀어 다음 기동 때 GPU(Vulkan)부터 다시 시도한다 (드라이버 업데이트 후 등).</summary>
    public static void RetryGpu()
    {
        Stop();
        try { File.Delete(CpuMarkerPath); } catch { }
    }

    internal static int TagNumber(string tag) => int.TryParse(tag.TrimStart('b'), out int n) ? n : 0;

    /// <summary>
    /// GitHub의 최신 llama.cpp 릴리스를 확인한다. 현재보다 새롭고 Windows Vulkan 빌드가 올라와 있으면 그 태그, 아니면 null.
    /// 네트워크 오류는 호출부로 전파한다.
    /// </summary>
    public static async Task<string?> CheckUpdateAsync(CancellationToken ct)
    {
        var release = JsonNode.Parse(await ApiHttp.GetStringAsync(LatestReleaseApi, ct));
        string tag = release?["tag_name"]?.GetValue<string>() ?? "";
        if (TagNumber(tag) <= TagNumber(Tag)) return null;
        bool hasBuild = release?["assets"] is JsonArray assets &&
                        assets.Any(a => a?["name"]?.GetValue<string>() == ZipName(tag, cpu: false));
        return hasBuild ? tag : null;
    }

    /// <summary>
    /// 지정 태그의 Vulkan 빌드를 받아 설치하고 즉시 전환한다. 실행 중 엔진은 내리고, 이전 버전 폴더는 지운다.
    /// 새 버전이면 GPU가 다시 될 수 있으므로 CPU 고정도 해제한다.
    /// </summary>
    public static async Task InstallVersionAsync(string tag, Action<long, long>? onProgress, CancellationToken ct)
    {
        await DownloadBuildAsync(cpu: false, onProgress, ct, tag);
        Stop();
        App.Settings.LlamaVersion = tag == DefaultTag ? "" : tag;
        App.Settings.Save();
        try { File.Delete(CpuMarkerPath); } catch { }
        foreach (var dir in Directory.GetDirectories(LlamaDir))
            if (!string.Equals(Path.GetFileName(dir), tag, StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(dir, recursive: true); } catch { } // 정리 실패는 디스크만 덜 회수된다
    }

    /// <summary>앱 기본 버전으로 되돌린다. 기본 빌드가 없으면 다음 번역 때(또는 설정 창에서) 다시 받는다.</summary>
    public static void ResetToDefaultVersion()
    {
        Stop();
        App.Settings.LlamaVersion = "";
        App.Settings.Save();
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
