namespace ZipperAIQQ.Storage.Owners;

/// <summary>主人在某个平台上的账号（统一身份下的一条绑定）。</summary>
public sealed record OwnerAccount(string Platform, string UserId);
