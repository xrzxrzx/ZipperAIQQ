using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZipperAIQQ.Core.Configurations.Options;

namespace ZipperAIQQ.Core.Configurations;

public class ZipperConfig : IZipperConfig
{
    const string ConfigFileName = "ZipperConfig.json";

    private static readonly IZipperOptions[] OptionTemplates =
    {
        new NapCatOptions(),
        new StorageOptions(),
        new AgentOptions(),
        new ServerOptions(),
        new ClientOptions(),
    };

    List<IZipperOptions> _options;

    public ZipperConfig()
    {
        _options = new();
    }

    public void Load()
    {
        string configFilePath = GetConfigFilePath();
        List<IZipperOptions> options = new();

        string jsonString = File.ReadAllText(configFilePath);
        JsonNode? json = JsonNode.Parse(jsonString);

        if (json == null)
        {
            throw new InvalidOperationException("配置文件解析失败");
        }

        foreach (var template in OptionTemplates)
        {
            string sectionName = template.SectionName;
            var sectionNode = json[sectionName];

            if (sectionNode == null)
            {
                //客户端不需要服务端配置，反之亦然，缺失可接受
                continue;
            }

            var optionType = template.GetType();
            var deserializedOption = sectionNode.Deserialize(optionType);

            if (deserializedOption == null)
            {
                throw new InvalidOperationException($"配置文件中 {sectionName} 节解析失败");
            }

            var option = (IZipperOptions)deserializedOption;
            options.Add(option);
        }

        _options = options;
    }

    public void Save()
    {
        string configFilePath = GetConfigFilePath();

        JsonObject newConfig = new JsonObject();

        foreach (var option in _options)
        {
            string sectionName = option.SectionName;

            JsonNode? sectionNode = JsonSerializer.SerializeToNode(option, option.GetType());
            if (sectionNode == null) continue;

            newConfig[sectionName] = sectionNode;
        }

        string jsonString = newConfig.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(configFilePath, jsonString);
    }

    public T GetConfig<T>() where T : IZipperOptions
    {
        foreach (var option in _options)
        {
            if (option is T typedOption)
            {
                return typedOption;
            }
        }
        throw new InvalidOperationException($"没有这种配置类型: {typeof(T).Name}");
    }

    private string GetConfigFilePath() => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

    public void SetConfig<T>(T option) where T : IZipperOptions
    {
        for (int i = 0; i < _options.Count; i++)
        {
            if (_options[i] is T)
            {
                _options[i] = option;
                return;
            }
        }

        throw new InvalidOperationException($"没有这种配置类型: {typeof(T).Name}");
    }
}
