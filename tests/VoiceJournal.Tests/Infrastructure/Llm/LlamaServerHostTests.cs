using System.Security.Cryptography;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Infrastructure.Llm;
using VoiceJournal.Infrastructure.Stt;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Llm;

public sealed class LlamaServerHostTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-llama-");

    public void Dispose() => _dir.Dispose();

    private string Write(string name, string content)
    {
        var path = _dir.File(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private (LlamaServerTool Tool, LlmModelFiles Model) Registered()
    {
        var server = Write(Path.Combine("bin", "llama-server.exe"), "launcher");
        var dll = Write(Path.Combine("bin", "llama-server-impl.dll"), "code");
        var gguf = Write("model.gguf", "weights");
        var mtp = Write("mtp.gguf", "head");
        var tool = new LlamaServerTool
        {
            Directory = Path.GetDirectoryName(server)!,
            Version = "b0",
            Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["llama-server.exe"] = Sha(server), ["llama-server-impl.dll"] = Sha(dll) },
        };
        var model = new LlmModelFiles
        {
            Model = new RegisteredModel { Path = gguf, Sha256 = Sha(gguf), RequiredMemoryMb = 1 },
            Mtp = new RegisteredFile { Path = mtp, Sha256 = Sha(mtp) },
        };
        return (tool, model);
    }

    [Fact]
    public void Arguments_ListenOnLoopbackOnly_WithOneSlotNoMmapNoThinking_AndMtp_AndNoKey()
    {
        var (_, model) = Registered();

        var args = LlamaServerHost.Arguments(new LlamaServerLaunch("gemma-4-e2b-it", 16384), model, 18990);

        Assert.Equal("127.0.0.1", args[args.ToList().IndexOf("--host") + 1]);
        Assert.Equal("18990", args[args.ToList().IndexOf("--port") + 1]);
        Assert.Equal("16384", args[args.ToList().IndexOf("-c") + 1]);
        Assert.Equal("1", args[args.ToList().IndexOf("-np") + 1]);
        Assert.Equal("none", args[args.ToList().IndexOf("-lm") + 1]);
        Assert.Equal("off", args[args.ToList().IndexOf("--reasoning") + 1]);
        Assert.Equal("draft-mtp", args[args.ToList().IndexOf("--spec-type") + 1]);
        Assert.Contains("--no-webui", args);
        Assert.DoesNotContain(args, a => a.Contains("api-key", StringComparison.Ordinal) || a.Contains("log-prompts", StringComparison.Ordinal));

        var noMtp = LlamaServerHost.Arguments(new LlamaServerLaunch("m", 8192), model with { Mtp = null }, 1);
        Assert.DoesNotContain("--spec-type", noMtp);
        Assert.DoesNotContain("-kvu", args);
    }

    [Fact]
    public void TwoSlots_ShareOneKvBuffer_AndEachKeepsTheWholeContext()
    {
        var (_, model) = Registered();

        var args = LlamaServerHost.Arguments(new LlamaServerLaunch("gemma-4-e2b-it", 16384, 2), model, 18990).ToList();

        Assert.Equal("2", args[args.IndexOf("-np") + 1]);
        Assert.Equal("32768", args[args.IndexOf("-c") + 1]);
        Assert.Contains("-kvu", args);
        Assert.Equal("16384", args[args.IndexOf("--kv-unified-per-slot") + 1]);
    }

    [Fact]
    public void Check_PassesForTheRegisteredFiles()
    {
        var (tool, model) = Registered();

        LlmToolCheck.Verify(tool, model);
    }

    [Fact]
    public void Check_RefusesAMissingFile_AChangedFile_AndAnUnregisteredDll()
    {
        var (tool, model) = Registered();

        var missing = Assert.Throws<LlmUnavailableException>(() => LlmToolCheck.Verify(tool, model with { Model = model.Model with { Path = _dir.File("none.gguf") } }));
        Assert.Contains("見つかりません", missing.Message, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(tool.Directory, "llama-server-impl.dll"), "changed");
        var changed = Assert.Throws<LlmUnavailableException>(() => LlmToolCheck.Verify(tool, model));
        Assert.Contains("SHA-256", changed.Message, StringComparison.Ordinal);

        var (tool2, model2) = Registered(); // rewrites the registered content
        File.WriteAllText(Path.Combine(tool2.Directory, "planted.dll"), "x");
        var extra = Assert.Throws<LlmUnavailableException>(() => LlmToolCheck.Verify(tool2, model2));
        Assert.Contains("planted.dll", extra.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnregisteredModel_OrShortMemory_IsUnavailable_WithoutStartingAnything()
    {
        var (tool, model) = Registered();
        var tools = new LlmToolSettings { LlamaServer = tool, Models = new Dictionary<string, LlmModelFiles> { ["m"] = model with { Model = model.Model with { RequiredMemoryMb = 4096 } } } };
        using var host = new LlamaServerHost(() => tools, availableMemoryMb: () => 1000);

        var unknown = await Assert.ThrowsAsync<LlmUnavailableException>(() => host.AcquireAsync(new LlamaServerLaunch("other", 8192), CancellationToken.None));
        Assert.Contains("登録されていません", unknown.Message, StringComparison.Ordinal);
        var memory = await Assert.ThrowsAsync<LlmUnavailableException>(() => host.AcquireAsync(new LlamaServerLaunch("m", 8192), CancellationToken.None));
        Assert.Contains("空きメモリ", memory.Message, StringComparison.Ordinal);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public void Defaults_RegisterTheEngineOfEnvironmentLock_AndTheE2bModelWithMtp()
    {
        var tools = LlmToolSettings.Defaults(@"C:\vj");

        Assert.Equal("b12121", tools.LlamaServer.Version);
        Assert.Equal("21b9b7c1e0c429d653d7431a80a2af0e58a9624fa5109068d7980d0e324fe03c", tools.LlamaServer.Files["llama-server.exe"]);
        var e2b = tools.Models[LlmToolSettings.DefaultModelId];
        Assert.Equal("8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52", e2b.Model.Sha256);
        Assert.NotNull(e2b.Mtp);
    }
}
