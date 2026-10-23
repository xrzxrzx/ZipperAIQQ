using ZipperAIQQ.Agent.Security;

namespace ZipperAIQQ.Tests.Security;

/// <summary>
/// 权限门禁测试：角色 → 能力，以及**硬底线不可被放宽**。
/// </summary>
/// <remarks>
/// 这是整个系统最该测的一块：门禁错了，AI 就能替人删文件、读别人的私聊。
/// </remarks>
public sealed class PermissionGateTests
{
    private readonly PermissionGate _gate = new();

    // ─────────────────────────── 默认允许集 ───────────────────────────

    [Theory]
    [InlineData(ToolCapability.ReadFile, Role.Normal, false)]
    [InlineData(ToolCapability.ReadFile, Role.Whitelist, true)]
    [InlineData(ToolCapability.ReadFile, Role.Admin, true)]
    [InlineData(ToolCapability.ReadFile, Role.Owner, true)]
    public void ReadFile_role_matrix(ToolCapability capability, Role role, bool expected)
        => Assert.Equal(expected, _gate.IsAllowed(capability, role));

    [Theory]
    [InlineData(ToolCapability.WriteFile, Role.Normal, false)]
    [InlineData(ToolCapability.WriteFile, Role.Whitelist, true)]
    [InlineData(ToolCapability.WriteFile, Role.Admin, true)]
    public void WriteFile_role_matrix(ToolCapability capability, Role role, bool expected)
        => Assert.Equal(expected, _gate.IsAllowed(capability, role));

    [Theory]
    [InlineData(ToolCapability.DeleteFile, Role.Whitelist, false)]
    [InlineData(ToolCapability.DeleteFile, Role.Admin, true)]
    [InlineData(ToolCapability.DeleteFile, Role.Owner, true)]
    public void DeleteFile_is_admin_only(ToolCapability capability, Role role, bool expected)
        => Assert.Equal(expected, _gate.IsAllowed(capability, role));

    // ─────────────────────────── 新增的高危能力 ───────────────────────────

    [Theory]
    [InlineData(ToolCapability.CrossSessionHistory)]
    [InlineData(ToolCapability.WriteProfile)]
    [InlineData(ToolCapability.SendPoke)]
    [InlineData(ToolCapability.SendToOtherSession)]
    public void Sensitive_capabilities_require_admin(ToolCapability capability)
    {
        Assert.False(_gate.IsAllowed(capability, Role.Normal));
        Assert.False(_gate.IsAllowed(capability, Role.Whitelist));
        Assert.True(_gate.IsAllowed(capability, Role.Admin));

        // **主人自动包含管理员**（role >= floor），不需要单独配
        Assert.True(_gate.IsAllowed(capability, Role.Owner));
    }

    // ─────────────────────────── 硬底线 ───────────────────────────

    [Fact]
    public void HardFloor_cannot_be_relaxed()
    {
        // 试图把"删文件"放宽到普通成员 —— 必须被拒
        var relaxed = new Dictionary<ToolCapability, Role>
        {
            [ToolCapability.DeleteFile] = Role.Normal,
        };

        Assert.Throws<InvalidOperationException>(() => PermissionGate.ValidateHardFloor(relaxed));
    }

    [Fact]
    public void HardFloor_allows_equal_or_stricter()
    {
        // ValidateHardFloor 要求**所有**硬底线能力都在字典里（缺一个就抛），
        // 所以这里给完整的一套：等于底线或更严都应该放过。
        var ok = new Dictionary<ToolCapability, Role>
        {
            [ToolCapability.ReadFile] = Role.Normal,
            [ToolCapability.WriteFile] = Role.Whitelist,
            [ToolCapability.DeleteFile] = Role.Admin,
            [ToolCapability.Schedule] = Role.Whitelist,
            [ToolCapability.CrossSessionHistory] = Role.Admin,
            [ToolCapability.WriteProfile] = Role.Admin,
            [ToolCapability.SendPoke] = Role.Admin,
            [ToolCapability.SendToOtherSession] = Role.Admin,
        };

        PermissionGate.ValidateHardFloor(ok);
    }

    [Fact]
    public void HardFloor_rejects_missing_capability()
    {
        // 少写一个能力 = 配置不完整，也必须拒（否则那条能力会走"未配置"默认值）
        var incomplete = new Dictionary<ToolCapability, Role>
        {
            [ToolCapability.DeleteFile] = Role.Admin,
        };

        Assert.Throws<InvalidOperationException>(() => PermissionGate.ValidateHardFloor(incomplete));
    }

    [Fact]
    public void Constructor_rejects_incomplete_config()
    {
        // 构造期就校验：传一份不完整的配置会直接抛，而不是留到运行时才发现。
        // （所以 MinRoleOf 里那个 "?? Role.Admin" 是防御性兜底，正常走不到）
        Assert.Throws<InvalidOperationException>(
            () => new PermissionGate(new Dictionary<ToolCapability, Role>()));
    }

    [Fact]
    public void HardFloor_covers_cross_session_history()
    {
        // 跨会话读别人的私聊：不能被配到 Admin 以下
        var relaxed = new Dictionary<ToolCapability, Role>
        {
            [ToolCapability.CrossSessionHistory] = Role.Whitelist,
        };

        Assert.Throws<InvalidOperationException>(() => PermissionGate.ValidateHardFloor(relaxed));
    }

    // ─────────────────────────── Check（带上下文）───────────────────────────

    [Fact]
    public void Check_denies_with_reason()
    {
        var context = new ToolCallContext
        {
            Chat = TestData.PrivateChat(),
            Sender = TestData.Sender("10001"),
            Role = Role.Normal,
        };

        var decision = _gate.Check(ToolCapability.DeleteFile, context);

        Assert.False(decision.Allowed);
        // 拒绝必须**带原因**：原因会回填给模型，让它知道"你调不了"而不是"工具坏了"
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void Check_allows_admin()
    {
        var context = new ToolCallContext
        {
            Chat = TestData.PrivateChat(),
            Sender = TestData.Sender("10001"),
            Role = Role.Admin,
        };

        Assert.True(_gate.Check(ToolCapability.DeleteFile, context).Allowed);
    }
}
