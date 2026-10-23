using ZipperAIQQ.Storage.Personas;

namespace ZipperAIQQ.Tests.Storage;

/// <summary>
/// 人设的「形象 / 性格」拆分。
/// </summary>
/// <remarks>
/// 用固定小节标头而不是给数据库加两列，所以这套解析必须稳：
/// 老数据（没有标头）要能整体生效，新数据要能拆开显示。
/// </remarks>
public sealed class PersonaTests
{
    [Fact]
    public void FromParts_with_both_sections()
    {
        var p = Persona.FromParts("小衣", "蓝发猫耳少女", "表面嫌麻烦其实细心");

        Assert.Equal("小衣", p.Name);
        Assert.Contains("【形象】", p.Content);
        Assert.Contains("【性格】", p.Content);
        Assert.Equal("蓝发猫耳少女", p.Image);
        Assert.Equal("表面嫌麻烦其实细心", p.Personality);
    }

    [Fact]
    public void FromParts_with_image_only()
    {
        var p = Persona.FromParts("小衣", "蓝发猫耳少女", null);

        Assert.Contains("【形象】", p.Content);
        Assert.DoesNotContain("【性格】", p.Content);
        Assert.Equal("蓝发猫耳少女", p.Image);
        Assert.Null(p.Personality);
    }

    [Fact]
    public void FromParts_with_personality_only()
    {
        var p = Persona.FromParts("小衣", null, "表面嫌麻烦");

        Assert.DoesNotContain("【形象】", p.Content);
        Assert.Contains("【性格】", p.Content);
        Assert.Null(p.Image);
        Assert.Equal("表面嫌麻烦", p.Personality);
    }

    [Fact]
    public void FromParts_with_neither_gives_empty_content()
    {
        var p = Persona.FromParts("小衣", null, null);

        Assert.Equal(string.Empty, p.Content);
        Assert.Null(p.Image);
        Assert.Null(p.Personality);
    }

    [Fact]
    public void FromParts_trims_whitespace()
    {
        var p = Persona.FromParts("小衣", "  蓝发  ", "  嫌麻烦  ");

        Assert.Equal("蓝发", p.Image);
        Assert.Equal("嫌麻烦", p.Personality);
    }

    [Fact]
    public void Legacy_persona_without_headers_has_null_sections()
    {
        // 老数据：整段文本、没有小节标头 → Image/Personality 都是 null，
        // 但 Content 仍然整体生效（向后兼容）
        var legacy = new Persona("persona", "你是一个角色扮演助手，说话要温柔");

        Assert.Null(legacy.Image);
        Assert.Null(legacy.Personality);
        Assert.Contains("角色扮演助手", legacy.Content);
    }

    [Fact]
    public void Empty_content_does_not_throw()
    {
        var p = new Persona("x", string.Empty);

        Assert.Null(p.Image);
        Assert.Null(p.Personality);
    }

    [Fact]
    public void Round_trip_preserves_sections()
    {
        var original = Persona.FromParts("小衣", "形象内容", "性格内容");

        // 再拼一次（模拟"读取 → 编辑 → 保存"）
        var roundTripped = Persona.FromParts(original.Name, original.Image, original.Personality);

        Assert.Equal(original.Content, roundTripped.Content);
    }

    [Fact]
    public void Multi_line_section_content_is_preserved()
    {
        var p = Persona.FromParts(
            "小衣",
            "第一行\n第二行\n第三行",
            "性格一\n性格二");

        Assert.Equal("第一行\n第二行\n第三行", p.Image);
        Assert.Equal("性格一\n性格二", p.Personality);
    }
}
