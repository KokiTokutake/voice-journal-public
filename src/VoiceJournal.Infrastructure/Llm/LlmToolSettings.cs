using System.Collections.Concurrent;
using System.Text.Json;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.IO;
using VoiceJournal.Core.Serialization;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.Infrastructure.Llm;

/// <summary>
/// The bundled llama.cpp server (<c>docs/phase-d-engine-plan.md</c>): the folder it runs from and the SHA-256 of every
/// executable file in it. <c>llama-server.exe</c> is a small launcher; the code is in the DLLs beside it
/// (<c>llama-server-impl.dll</c>, <c>ggml-vulkan.dll</c>, ...), so all of them are checked, and a DLL that is not
/// registered must not be there (it would be loaded too).
/// </summary>
public sealed record LlamaServerTool
{
    public required string Directory { get; init; }

    /// <summary>The llama.cpp build, as <c>llama-server --version</c> reports it (b12121).</summary>
    public required string Version { get; init; }

    /// <summary>File name → SHA-256, for <c>llama-server.exe</c> and every <c>*.dll</c> in <see cref="Directory"/>.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    public string ServerPath => Path.Combine(Directory, "llama-server.exe");
}

/// <summary>A GGUF model the bundled server may load, with its optional MTP head for speculative decoding.</summary>
public sealed record LlmModelFiles
{
    public required RegisteredModel Model { get; init; }

    /// <summary>The separate MTP head (<c>--spec-type draft-mtp -md</c>): about 1.75 times faster on Gemma 4 E2B (E1).</summary>
    public RegisteredFile? Mtp { get; init; }
}

/// <summary>
/// The inference engine and models the app may run (<c>%LOCALAPPDATA%\VoiceJournal\config\llm-tools.json</c>). The defaults
/// are the versions fixed in <c>docs/environment-lock.md</c> chapter 6 (同梱の推論エンジン).
/// </summary>
public sealed record LlmToolSettings
{
    public const string FileName = "llm-tools.json";

    public const string DefaultModelId = "gemma-4-e2b-it";

    public required LlamaServerTool LlamaServer { get; init; }

    /// <summary>Models by id (the model id of the LLM settings and of the requests).</summary>
    public required IReadOnlyDictionary<string, LlmModelFiles> Models { get; init; }

    public static LlmToolSettings Defaults(string appRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(appRoot);
        string P(params string[] parts) => Path.Combine([appRoot, .. parts]);
        return new LlmToolSettings
        {
            LlamaServer = new LlamaServerTool
            {
                Directory = P("tools", "llama.cpp", "b12121", "bin"),
                Version = "b12121",
                Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["llama-server.exe"] = "21b9b7c1e0c429d653d7431a80a2af0e58a9624fa5109068d7980d0e324fe03c",
                    ["llama-server-impl.dll"] = "c5bfa0deeb65558a28cec60272a68e8ae13f56aa5e9905bdec553ea0d047ad78",
                    ["llama.dll"] = "8be1441a1ab20f55465d8d78ecc7dc1da24cec52c7f8e38859cd51ab22286086",
                    ["llama-common.dll"] = "69b36b8f4a3b06e85568d72ae2034f30c36b82bf0974bd458af33a9f94a3a0ed",
                    ["mtmd.dll"] = "51a291a367b7f2a897c5942ff5f6a733b648f00f373ee5555cc102bd76b78177",
                    ["ggml.dll"] = "39cb63ac227d286ee835d9c9192aaf6df4b9db78bf76507a671aaaabb5907041",
                    ["ggml-base.dll"] = "1773a737f7e4d17c8d847c1be169b0db198d8d3e76ed78797ca1872c288d1f3b",
                    ["ggml-vulkan.dll"] = "bea12e4528d90c03abdc7c16f698bbd4b4e16995f2fa30e4b88112d802ef1e21",
                    ["ggml-rpc.dll"] = "6ed8a76a0702dc995ca826f8b7b95575a1d54910ff703a68038bb4109ca45502",
                    ["libomp.dll"] = "a12116ba72d1d6820407cf30be23da04ce79d6bb8a71a5ee71759c5a1faa6f1c",
                    ["ggml-cpu-alderlake.dll"] = "c126471052c97064d377aade08fe0f929682f7271e801dfeda24c3db1a9c3fbc",
                    ["ggml-cpu-cannonlake.dll"] = "e5bc10bf6ced445a120cb2dde22d5c2ba5464ccf921f51b8aac6e588076fec36",
                    ["ggml-cpu-cascadelake.dll"] = "78f856edf13a0cf6c6ff076a92598fd2df780b56919b1b277dcda2407b5cab79",
                    ["ggml-cpu-cooperlake.dll"] = "5165fe948354ab08ce0869dc05de1c6e77958836216de0f2310581a4f51687cc",
                    ["ggml-cpu-haswell.dll"] = "0a762c7e959d4a47b2e4792db4c88b36e141ee49cee9a117b06431e5f4d718dc",
                    ["ggml-cpu-icelake.dll"] = "2b15dd97b9577abe3ad927dc76bb0dfb2ecae1781d298b2ccf492d1267b004d5",
                    ["ggml-cpu-ivybridge.dll"] = "e4d0fa087a40c07ba2d7a3b50f4cbca3bf13e3ef7c00f03cac4922fa51655b1d",
                    ["ggml-cpu-piledriver.dll"] = "80d5d54b75664cbc8fc7042cbdca5243011efe21a20ea71a7558bee93817c188",
                    ["ggml-cpu-sandybridge.dll"] = "afb8a3b9e98a62593cdc4c4a956ca629f74ac8404cbff33c31c9667a76c5e02f",
                    ["ggml-cpu-sapphirerapids.dll"] = "c312e18861874332b68a0e4b77cdfbb2a36c68ed9e3a1fffa40ed74480a3e336",
                    ["ggml-cpu-skylakex.dll"] = "95df5facf68329a095b9dfe68895c26898de65106431eb441b23a86a9041ada5",
                    ["ggml-cpu-sse42.dll"] = "64206b30324e5ba38cf94367b85bc8e60fec4da9152d46115da220ed5d26df93",
                    ["ggml-cpu-x64.dll"] = "de5b48a5866081696381c0a9aeaeed39a6da23ce5a2484d45ac64139304b2e52",
                    ["ggml-cpu-zen4.dll"] = "73fa8cf05805d585c5f7672148908cc085e4a59de92ba83e3cf23dcee7d225c1",
                    ["llama-batched-bench-impl.dll"] = "4acda0f05229e413c4a6fe2f9b5fe97decd9f7c0926028cbcfbb1312ec1d2248",
                    ["llama-bench-impl.dll"] = "9fda3f5b789add99a80010df3dc0ffc25e00d329a879ff9311ad06ee08b93f4e",
                    ["llama-cli-impl.dll"] = "785765d23405b240306aeaa97994741d5200f75a09aa1c0d54bdfaa9745f4cdc",
                    ["llama-completion-impl.dll"] = "0643dfee171f834fce52208a629bea54dece9f23b3400138ea6d6bc72f9664ce",
                    ["llama-fit-params-impl.dll"] = "f6d651bd429e0226e187dd3609c73b3ed64b3590493f007afba3bb9e5b73994c",
                    ["llama-perplexity-impl.dll"] = "0d23bcc2979b8445af09615b0362ea5f70e0e0d99c3fc3c3518439e094da9134",
                    ["llama-quantize-impl.dll"] = "457b04ea26f42693d5e8ff4c93d26c4139fcc63e293cd408eb72a4353c40fbf3",
                },
            },
            Models = new Dictionary<string, LlmModelFiles>(StringComparer.Ordinal)
            {
                [DefaultModelId] = new()
                {
                    Model = new RegisteredModel
                    {
                        Path = P("models", "llm", "gemma-4-E2B-it-Q4_0.gguf"),
                        Sha256 = "8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52",
                        // llama-server measured 3.6-3.9 GB with C=16384 (E1); some room for the compute buffers.
                        RequiredMemoryMb = 4608,
                    },
                    Mtp = new RegisteredFile
                    {
                        Path = P("models", "llm", "mtp-gemma-4-E2B-it-Q4_0.gguf"),
                        Sha256 = "718d3a44057924d5840c65a0aeedf7929366adeeda92a2006955ccabaf645ef4",
                    },
                },
            },
        };
    }

    /// <summary>Reads <c>config/llm-tools.json</c> under <paramref name="appRoot"/>, writing the defaults when it does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but cannot be read.</exception>
    public static LlmToolSettings LoadOrCreate(string appRoot)
    {
        var path = Path.Combine(appRoot, "config", FileName);
        if (!File.Exists(path))
        {
            var defaults = Defaults(appRoot);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(defaults, VoiceJournalJson.Options));
            return defaults;
        }
        try
        {
            return JsonSerializer.Deserialize<LlmToolSettings>(AtomicFile.ReadAllBytes(path), VoiceJournalJson.Options)
                ?? throw new InvalidDataException($"'{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{path}' is not valid.", ex);
        }
    }
}

/// <summary>SHA-256 checks of the registered engine and model files (detail spec 8.1: only registered executables run).</summary>
public static class LlmToolCheck
{
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Modified), string> HashCache = new();

    /// <exception cref="LlmUnavailableException">A file is missing, differs from its registered SHA-256, or an unregistered DLL is present.</exception>
    public static void Verify(LlamaServerTool tool, LlmModelFiles model)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(model);
        if (!System.IO.Directory.Exists(tool.Directory))
            throw new LlmUnavailableException($"推論エンジン（llama.cpp {tool.Version}）が見つかりません: {tool.Directory}");
        foreach (var (name, sha) in tool.Files)
            Check(Path.Combine(tool.Directory, name), sha, $"推論エンジンのファイル {name}");
        var extra = System.IO.Directory.EnumerateFiles(tool.Directory, "*.dll")
            .Select(Path.GetFileName)
            .Where(n => n is not null && !tool.Files.ContainsKey(n))
            .ToList();
        if (extra.Count > 0)
            throw new LlmUnavailableException($"推論エンジンのフォルダーに、登録していない DLL があります: {string.Join("、", extra)}");
        Check(model.Model.Path, model.Model.Sha256, "モデル");
        if (model.Mtp is { } mtp) Check(mtp.Path, mtp.Sha256, "モデルの MTP");
    }

    private static void Check(string path, string sha256, string what)
    {
        if (!File.Exists(path)) throw new LlmUnavailableException($"{what}が見つかりません: {path}");
        var info = new FileInfo(path);
        var actual = HashCache.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), _ => SttInputBuilder.HashFile(path));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new LlmUnavailableException($"{what}が登録した SHA-256 と一致しません: {path}");
    }
}
