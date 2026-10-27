using System.IO;
using System.Security.Cryptography;
using System.Text;
using DeepTranslation.Models;

namespace DeepTranslation.Services;

/// <summary>내장 번역 엔진에서 선택할 수 있는 GGUF 모델의 정적 카탈로그.</summary>
public static class ModelCatalog
{
    /// <summary>카탈로그 항목. Id는 llama-server의 모델 별칭(-a)으로도 사용된다.
    /// FileName이 절대 경로면 사용자 GGUF(크기 검증 없음, SizeBytes=0).</summary>
    public sealed record ModelInfo(string Id, string DisplayName, string FileName,
        string Url, long SizeBytes, string LicenseKey, string Tag = "")
    {
        public string FilePath => Path.IsPathRooted(FileName) ? FileName : Path.Combine(ModelsDir, FileName);

        /// <summary>파일이 존재하고 크기가 정확히 일치하면 다운로드 완료로 본다 (사용자 GGUF는 존재만 확인).</summary>
        public bool IsDownloaded => File.Exists(FilePath) && (SizeBytes == 0 || new FileInfo(FilePath).Length == SizeBytes);

        /// <summary>사용자가 지정한 외부 GGUF인지 — 앱 소유 파일이 아니므로 삭제하지 않는다.</summary>
        public bool IsCustom => SizeBytes == 0;

        /// <summary>UI 표시용 크기 (예: "2.5 GB").</summary>
        public string SizeText =>
            $"{(SizeBytes > 0 ? SizeBytes : File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0) / 1073741824.0:0.0} GB";
    }

    /// <summary>설정의 EmbeddedModelId가 이 값이면 CustomModelPath의 GGUF를 쓴다.</summary>
    public const string CustomId = "custom";

    /// <summary>설정이 가리키는 내장 모델. 사용자 GGUF가 비어 있거나 알 수 없는 Id면 null.</summary>
    public static ModelInfo? Resolve(AppSettings s) =>
        s.EmbeddedModelId == CustomId ? Custom(s.CustomModelPath) : Find(s.EmbeddedModelId);

    /// <summary>
    /// 사용자 GGUF 항목. Id에 파일 이름(Hy-MT2·Qwen3.5 판별용)과 경로·크기 해시(파일 교체 시 재기동·Ollama 재등록용)를 넣는다.
    /// Ollama 모델명에도 쓰이므로 소문자·숫자·'.'·'-'만 남긴다.
    /// </summary>
    public static ModelInfo? Custom(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        long size = File.Exists(path) ? new FileInfo(path).Length : 0;
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{size}")))[..8];
        var stem = new string(Path.GetFileNameWithoutExtension(path).ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) || c == '.' ? c : '-').ToArray()).Trim('-', '.');
        if (stem.Length > 40) stem = stem[..40];
        return new ModelInfo($"custom-{stem}-{hash.ToLowerInvariant()}", Path.GetFileName(path), path, "", 0, "license.custom");
    }

    /// <summary>모델 파일 저장 폴더 (%LOCALAPPDATA%\DeepTranslation\models).</summary>
    public static string ModelsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepTranslation", "models");

    public static readonly IReadOnlyList<ModelInfo> All = new[]
    {
        new ModelInfo("qwen3.5-4b", "Qwen3.5 4B",
            "Qwen3.5-4B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf",
            2740937888, "license.apache", "recommended"),
        new ModelInfo("hy-mt2-7b", "Hy-MT2 7B",
            "Hy-MT2-7B-Q4_K_M.gguf",
            "https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/main/Hy-MT2-7B-Q4_K_M.gguf",
            4624648896, "license.apache", "mt"),
        // 7B와 같은 hunyuan-dense 구조 — 번역 전용 모델 중 메모리가 가장 적다 (실행 약 2GB)
        new ModelInfo("hy-mt2-1.8b", "Hy-MT2 1.8B",
            "Hy-MT2-1.8B-Q4_K_M.gguf",
            "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/Hy-MT2-1.8B-Q4_K_M.gguf",
            1133080448, "license.apache", "mt"),
        new ModelInfo("qwen3-4b-instruct-2507", "Qwen3 4B Instruct",
            "Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            2497281120, "license.apache"),
        new ModelInfo("gemma-4-e4b-it", "Gemma 4 E4B",
            "gemma-4-E4B-it-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf",
            4977171584, "license.apache"),
        new ModelInfo("exaone-3.5-2.4b", "EXAONE 3.5 2.4B",
            "EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf",
            "https://huggingface.co/LGAI-EXAONE/EXAONE-3.5-2.4B-Instruct-GGUF/resolve/main/EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf",
            1644918272, "license.nc", "korean"),
        new ModelInfo("hyperclovax-seed-1.5b", "HyperCLOVA X SEED 1.5B",
            "hyperclovax-seed-text-instruct-1.5b-q4_k_m.gguf",
            "https://huggingface.co/rippertnt/HyperCLOVAX-SEED-Text-Instruct-1.5B-Q4_K_M-GGUF/resolve/main/hyperclovax-seed-text-instruct-1.5b-q4_k_m.gguf",
            1133974368, "license.clova", "light"),
    };

    public static ModelInfo? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
