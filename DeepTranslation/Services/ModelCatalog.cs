using System.IO;

namespace DeepTranslation.Services;

/// <summary>내장 번역 엔진에서 선택할 수 있는 GGUF 모델의 정적 카탈로그.</summary>
public static class ModelCatalog
{
    /// <summary>카탈로그 항목. Id는 llama-server의 모델 별칭(-a)으로도 사용된다.</summary>
    public sealed record ModelInfo(string Id, string DisplayName, string FileName,
        string Url, long SizeBytes, string LicenseNote)
    {
        public string FilePath => Path.Combine(ModelsDir, FileName);

        /// <summary>파일이 존재하고 크기가 정확히 일치하면 다운로드 완료로 본다.</summary>
        public bool IsDownloaded => File.Exists(FilePath) && new FileInfo(FilePath).Length == SizeBytes;

        /// <summary>UI 표시용 크기 (예: "2.5 GB").</summary>
        public string SizeText => $"{SizeBytes / 1073741824.0:0.0} GB";
    }

    /// <summary>모델 파일 저장 폴더 (%LOCALAPPDATA%\DeepTranslation\models).</summary>
    public static string ModelsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepTranslation", "models");

    public static readonly IReadOnlyList<ModelInfo> All = new[]
    {
        new ModelInfo("qwen3.5-4b", "Qwen3.5 4B (권장)",
            "Qwen3.5-4B-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf",
            2740937888, "Apache-2.0 · 상업 이용 자유"),
        new ModelInfo("qwen3-4b-instruct-2507", "Qwen3 4B Instruct",
            "Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            "https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            2497281120, "Apache-2.0 · 상업 이용 자유"),
        new ModelInfo("gemma-3-4b-it", "Gemma 3 4B",
            "gemma-3-4b-it-Q4_K_M.gguf",
            "https://huggingface.co/ggml-org/gemma-3-4b-it-GGUF/resolve/main/gemma-3-4b-it-Q4_K_M.gguf",
            2489757856, "Gemma 이용약관 적용"),
        new ModelInfo("exaone-3.5-2.4b", "EXAONE 3.5 2.4B (한국어 특화)",
            "EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf",
            "https://huggingface.co/LGAI-EXAONE/EXAONE-3.5-2.4B-Instruct-GGUF/resolve/main/EXAONE-3.5-2.4B-Instruct-Q4_K_M.gguf",
            1644918272, "비상업(연구) 용도 한정 라이선스"),
        new ModelInfo("hyperclovax-seed-1.5b", "HyperCLOVA X SEED 1.5B (경량)",
            "hyperclovax-seed-text-instruct-1.5b-q4_k_m.gguf",
            "https://huggingface.co/rippertnt/HyperCLOVAX-SEED-Text-Instruct-1.5B-Q4_K_M-GGUF/resolve/main/hyperclovax-seed-text-instruct-1.5b-q4_k_m.gguf",
            1133974368, "상업 이용 가능 (월 1천만 MAU 이하)"),
    };

    public static ModelInfo? Find(string id) => All.FirstOrDefault(m => m.Id == id);
}
