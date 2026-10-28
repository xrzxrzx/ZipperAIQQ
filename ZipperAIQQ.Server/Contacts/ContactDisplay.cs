using ZipperAIQQ.Channel;

namespace ZipperAIQQ.Server.Contacts;

/// <summary>联系人显示名：有备注时「昵称（备注）」，否则只用昵称。</summary>
/// <remarks>
/// 三个入口（人员与名单、主人候选、会话名解析）原来各拼一遍，改格式时容易漏。
/// </remarks>
public static class ContactDisplay
{
    public static string Name(ContactInfo contact) =>
        string.IsNullOrWhiteSpace(contact.Remark)
            ? contact.Nickname
            : $"{contact.Nickname}（{contact.Remark}）";
}
