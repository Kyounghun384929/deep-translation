using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using DeepTranslation.Models;

namespace DeepTranslation.Services;

/// <summary>
/// 서명된 Ollama를 슬림하게 임베드한 대체 내장 엔진.
/// 공식 standalone zip에서 Vulkan+CPU 추론에 필요한 최소 파일만 추출해(약 119MB) ollama.exe를
/// 자식 프로세스로 기동하고, OpenAI 호환 API 주소(baseUrl)를 제공한다.
/// 스마트 앱 컨트롤(SAC)이 켜져 무서명 llama.cpp가 차단되는 PC의 대안이다.
/// 기존 EmbeddedEngine과 동일한 온디맨드 기동·유휴 언로드 수명주기를 따른다.
/// </summary>
public static class OllamaEngine
{
    // Ollama 릴리스 버전 고정 — 자산 URL·설치 폴더 이름에 함께 쓰인다
    private const string OllamaVersion = "v0.32.1";
    private const string OllamaZipUrl =
        $"https://github.com/ollama/ollama/releases/download/{OllamaVersion}/ollama-windows-amd64.zip";

    // 서버는 /api/version에 즉시 응답한다(모델 로드는 첫 추론 때). 넉넉히 잡는다.
    private const int HealthTimeoutSeconds = 60;

    private static readonly object Sync = new();
    private static readonly HttpClient HealthHttp = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static Process? _proc;
    private static StreamWriter? _log;
    private static string _modelId = "";   // 카탈로그 Id (settings.EmbeddedModelId와 비교)
    private static string _baseUrl = "";
    private static Timer? _idleTimer;
    private static int _idleMinutes = 5;
    private static long _lastActivityTick;

    private static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeepTranslation");
    private static string OllamaRootDir => Path.Combine(AppDataDir, "ollama");
    private static string BinDir => Path.Combine(OllamaRootDir, OllamaVersion);
    private static string ModelsDir => Path.Combine(AppDataDir, "ollama-models");
    private static string HomeDir => Path.Combine(AppDataDir, "ollama-home");
    private static string LogPath => Path.Combine(AppDataDir, "ollama.log");

    /// <summary>Ollama 모델명은 카탈로그 Id 앞에 "dt-"를 붙인다 (사용자의 다른 모델과 충돌 방지).</summary>
    private static string OllamaModelName(string catalogId) => "dt-" + catalogId;

    /// <summary>현재 실행 중인 엔진의 모델 Id. 실행 중이 아니면 빈 문자열.</summary>
    public static string RunningModelId
    {
        get { lock (Sync) return _proc is { HasExited: false } ? _modelId : ""; }
    }

    /// <summary>
    /// 내장 엔진으로 Ollama를 써야 하는지 여부(사용자 선택 없이 자동 결정).
    /// 스마트 앱 컨트롤(SAC)이 켜져 무서명 llama.cpp가 차단되는 PC에서만 서명된 Ollama로 전환한다.
    /// </summary>
    public static bool UseOllamaBackend => EmbeddedEngine.IsSmartAppControlOn;

    /// <summary>
    /// 설정된 내장 모델로 ollama serve가 떠 있도록 보장하고 baseUrl(스킴+호스트, /v1 없음)을 반환한다.
    /// 같은 모델로 이미 실행 중이면 즉시 반환. 바이너리·모델 등록은 최초 1회 자동 수행.
    /// 반환한 baseUrl에 LmStudioClient가 "/v1/chat/completions"를 붙여 그대로 호출한다.
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

        string exe = await EnsureBinaryAsync(onStatus, ct);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(HomeDir);

        int port = GetFreePort();
        string baseUrl = $"http://127.0.0.1:{port}";
        onStatus?.Invoke("번역 엔진 준비 중…");

        var (proc, log) = StartServe(exe, port);
        try
        {
            await WaitForServerAsync(proc, baseUrl, ct);            // /api/version 폴링으로 기동 확인
            await EnsureModelAsync(exe, model, port, onStatus, ct); // dt-<id> 등록 + 미참조 blob 정리 (최초 1회)
        }
        catch
        {
            KillQuiet(proc, log); // 기동/등록 실패 — 뜨다 만 프로세스를 정리하고 전파
            throw;
        }

        lock (Sync)
        {
            _proc = proc;
            _log = log;
            _modelId = model.Id;
            _baseUrl = baseUrl;
            ResetIdleTimerLocked();
            return _baseUrl;
        }
    }

    /// <summary>번역 활동이 있었음을 알린다 — 유휴 언로드 타이머를 리셋한다.</summary>
    public static void NotifyActivity()
    {
        lock (Sync)
        {
            if (_proc is { HasExited: false }) ResetIdleTimerLocked();
        }
    }

    /// <summary>설정 변경 반영 — 내장 모드가 아니거나 선택 모델이 실행 중 모델과 다르면 엔진을 내린다.</summary>
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

    /// <summary>엔진 프로세스를 종료하고 메모리를 회수한다. 앱 종료·모델 전환·백엔드 전환·유휴 언로드에서 호출.</summary>
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
            // 트리 kill 필수 — serve만 죽이면 자식 러너(llama-server.exe)가 VRAM을 계속 점유한다
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

    private static (Process proc, StreamWriter log) StartServe(string exe, int port)
    {
        var psi = new ProcessStartInfo(exe, "serve")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        ApplyEnv(psi, port);

        var proc = new Process { StartInfo = psi };
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
            throw new LmStudioException("Ollama 실행에 실패했습니다: " + ex.Message, ex);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return (proc, log);
    }

    private static async Task WaitForServerAsync(Process proc, string baseUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < HealthTimeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();
            if (proc.HasExited)
                throw new LmStudioException("Ollama 서버가 기동 중 종료되었습니다.\n\n" + TailLog());

            try
            {
                using var resp = await HealthHttp.GetAsync($"{baseUrl}/api/version", ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }                                       // 아직 리슨 전 — 재시도
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }   // 요청 타임아웃 — 재시도

            await Task.Delay(250, ct);
        }
        throw new LmStudioException("Ollama 서버 기동이 시간 내에 완료되지 않았습니다.\n\n" + TailLog());
    }

    // ---- 모델 프로비저닝 (기존 GGUF 재활용) ----

    private static async Task EnsureModelAsync(string exe, ModelCatalog.ModelInfo model, int port,
        Action<string>? onStatus, CancellationToken ct)
    {
        string ollamaModel = OllamaModelName(model.Id);
        if (File.Exists(ManifestPath(ollamaModel))) return; // 이미 등록됨 — 재생성 불필요

        onStatus?.Invoke("모델 등록 중…");
        // 임시 Modelfile: 기존에 내려받은 GGUF를 그대로 가져온다(create가 blob 저장소로 복사)
        string modelfile = Path.Combine(OllamaRootDir, "Modelfile.tmp");
        await File.WriteAllTextAsync(modelfile, $"FROM \"{model.FilePath}\"\n", ct);
        try
        {
            await RunClientAsync(exe, $"create {ollamaModel} -f \"{modelfile}\"", port, ct);
        }
        finally
        {
            try { File.Delete(modelfile); } catch { }
        }
        PruneUnreferencedBlobs();
    }

    // create/list 같은 클라이언트 명령을 우리 서버(전용 포트)에 대해 실행하고 종료를 기다린다.
    private static async Task RunClientAsync(string exe, string args, int port, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        ApplyEnv(psi, port);

        using var proc = Process.Start(psi)
            ?? throw new LmStudioException("Ollama 클라이언트를 실행하지 못했습니다.");
        // 두 스트림을 동시에 드레인한다 — 순차로 읽으면 한쪽 파이프 버퍼가 찰 때 교착될 수 있다
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        string stderr = await stderrTask;
        string stdout = await stdoutTask;
        if (proc.ExitCode != 0)
        {
            string detail = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();
            throw new LmStudioException("Ollama 모델 등록에 실패했습니다:\n" + detail);
        }
    }

    // create는 (원본 GGUF 바이트 사본 blob + 메타 재작성 변환 레이어 blob) 두 개를 만들고,
    // 매니페스트는 변환 레이어만 참조한다. 어떤 매니페스트도 참조하지 않는 blob을 지워 중복(약 2.5GB)을
    // 회수한다. 저장소의 모든 매니페스트를 훑으므로 다른 dt-모델이 참조하는 blob은 보존된다.
    private static void PruneUnreferencedBlobs()
    {
        try
        {
            string manifestsRoot = Path.Combine(ModelsDir, "manifests");
            string blobsDir = Path.Combine(ModelsDir, "blobs");
            if (!Directory.Exists(blobsDir) || !Directory.Exists(manifestsRoot)) return;

            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mf in Directory.GetFiles(manifestsRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(mf));
                    AddDigest(referenced, node?["config"]);
                    if (node?["layers"] is JsonArray layers)
                        foreach (var layer in layers) AddDigest(referenced, layer);
                }
                catch { /* 매니페스트가 아니거나 파싱 실패한 파일은 건너뛴다 */ }
            }

            foreach (var blob in Directory.GetFiles(blobsDir))
                if (!referenced.Contains(Path.GetFileName(blob)))
                    try { File.Delete(blob); } catch { }
        }
        catch { /* prune 실패는 치명적이지 않음 — 디스크 여유만 덜 회수된다 */ }
    }

    private static void AddDigest(HashSet<string> set, JsonNode? node)
    {
        var digest = node?["digest"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(digest)) set.Add(digest.Replace(':', '-')); // sha256:xxx -> sha256-xxx (파일명)
    }

    private static string ManifestPath(string ollamaModel) => Path.Combine(
        ModelsDir, "manifests", "registry.ollama.ai", "library", ollamaModel, "latest");

    // ---- 바이너리 준비 (슬림 추출) ----

    /// <summary>엔진 바이너리(ollama.exe)가 이미 설치돼 있는지 여부.</summary>
    public static bool IsBinaryInstalled => FindOllamaExe(BinDir) != null;

    /// <summary>
    /// 엔진 바이너리를 내려받아 설치한다 (설정 창의 사전 다운로드용, 이미 설치돼 있으면 즉시 반환).
    /// 공식 zip(약 1.4GB)을 받아 슬림 집합(약 119MB)만 풀고 zip은 삭제한다.
    /// onProgress에는 ModelDownloader의 (받은 바이트, 전체 바이트)가 그대로 전달된다.
    /// </summary>
    public static async Task DownloadBinaryAsync(Action<long, long>? onProgress, CancellationToken ct)
    {
        if (IsBinaryInstalled) return;

        Directory.CreateDirectory(BinDir);
        string zipPath = Path.Combine(OllamaRootDir, "ollama-windows-amd64.zip");
        await ModelDownloader.DownloadAsync(OllamaZipUrl, zipPath, 0, onProgress, ct);

        // 추출은 CPU/IO 작업 — UI 스레드에서 호출돼도 창이 멎지 않게 스레드 풀에서 수행한다
        await Task.Run(() => ExtractSlim(zipPath, BinDir), ct);
        try { File.Delete(zipPath); } catch { }

        if (!IsBinaryInstalled)
            throw new LmStudioException("다운로드한 번역 엔진에서 ollama.exe를 찾지 못했습니다.");
    }

    /// <summary>ollama.exe 경로를 반환한다. 없으면 최초 1회 내려받는다 (번역 경로의 문자열 상태 어댑터).</summary>
    private static async Task<string> EnsureBinaryAsync(Action<string>? onStatus, CancellationToken ct)
    {
        string? exe = FindOllamaExe(BinDir);
        if (exe != null) return exe;

        onStatus?.Invoke("번역 엔진 다운로드 중… (1회)");
        await DownloadBinaryAsync(
            (received, _) => onStatus?.Invoke($"번역 엔진 다운로드 중… {received / 1048576} MB (1회)"), ct);
        onStatus?.Invoke("번역 엔진 준비 중…");
        return FindOllamaExe(BinDir)!; // DownloadBinaryAsync가 존재를 보장한다
    }

    // GPU 벤더별 대용량 백엔드(cuda_v*·rocm·mlx)는 건너뛰고 Vulkan+CPU 추론에 필요한 파일만 푼다.
    // CUDA 약 1.28GB를 애초에 디스크에 쓰지 않아 결과는 약 119MB(39개 파일)가 된다.
    private static void ExtractSlim(string zipPath, string destDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0) continue; // 디렉터리 엔트리
            string rel = entry.FullName.Replace('\\', '/');
            if (IsExcludedBackend(rel)) continue;

            string target = Path.Combine(destDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static bool IsExcludedBackend(string relPath) =>
        relPath.Contains("/cuda_", StringComparison.OrdinalIgnoreCase) ||
        relPath.Contains("/rocm", StringComparison.OrdinalIgnoreCase) ||
        relPath.Contains("/mlx", StringComparison.OrdinalIgnoreCase);

    private static string? FindOllamaExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    // ---- 유틸 ----

    private static void ApplyEnv(ProcessStartInfo psi, int port)
    {
        // 전용 포트·격리된 모델 저장소, ollama.com 조회 억제, 홈(키·캐시)을 앱 데이터 하위로 격리
        psi.Environment["OLLAMA_HOST"] = $"127.0.0.1:{port}";
        psi.Environment["OLLAMA_MODELS"] = ModelsDir;
        psi.Environment["OLLAMA_NO_CLOUD"] = "1";
        psi.Environment["USERPROFILE"] = HomeDir;
        psi.Environment["HOME"] = HomeDir;
    }

    private static void KillQuiet(Process proc, StreamWriter log)
    {
        try { if (!proc.HasExited) { proc.Kill(entireProcessTree: true); proc.WaitForExit(3000); } } catch { }
        try { proc.Dispose(); } catch { }
        lock (log) { try { log.Dispose(); } catch { } }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // 유휴 타이머 리셋. 경과 시 프로세스 트리를 내려 RAM/VRAM을 회수한다.
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

    // 기동/등록 실패 원인 표시용 — 로그 파일 마지막 몇 줄
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
