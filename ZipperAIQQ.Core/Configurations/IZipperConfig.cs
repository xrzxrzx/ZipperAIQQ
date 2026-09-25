using ZipperAIQQ.Core.Configurations.Options;

namespace ZipperAIQQ.Core.Configurations;

public interface IZipperConfig
{
    void Load();
    void Save();

    T GetConfig<T>() where T : IZipperOptions;
    void SetConfig<T>(T option) where T : IZipperOptions;
}
