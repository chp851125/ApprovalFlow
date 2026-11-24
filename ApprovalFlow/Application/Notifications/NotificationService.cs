using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Notifications;

/// <summary>站內通知中心：查詢、未讀數、標示已讀（只能操作自己的通知）</summary>
public sealed class NotificationService(IRepository<Notification> notifications, ICurrentUser currentUser, IClock clock)
{
    private int Me => currentUser.Id ?? throw new ForbiddenException();

    public async Task<IReadOnlyList<Notification>> ListAsync(bool unreadOnly = false)
    {
        var me = Me;
        return (await notifications.QueryAsync(n => n.RecipientId == me && (!unreadOnly || !n.IsRead)))
            .OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).ToList();
    }

    public async Task<int> UnreadCountAsync()
    {
        if (currentUser.Id is not int me) return 0;
        return (await notifications.QueryAsync(n => n.RecipientId == me && !n.IsRead)).Count;
    }

    public async Task<IReadOnlyList<Notification>> RecentAsync(int take = 8)
    {
        if (currentUser.Id is not int me) return [];
        return (await notifications.QueryAsync(n => n.RecipientId == me))
            .OrderBy(n => n.IsRead).ThenByDescending(n => n.CreatedAt).Take(take).ToList();
    }

    /// <summary>標示單筆已讀並回傳連結（不是自己的通知會拒絕）</summary>
    public async Task<Notification> MarkReadAsync(int id)
    {
        var n = await notifications.GetByIdAsync(id) ?? throw new NotFoundException("通知", id);
        if (n.RecipientId != Me) throw new ForbiddenException();
        if (!n.IsRead) await SaveReadAsync(n);
        return n;
    }

    public async Task<int> MarkAllReadAsync()
    {
        var me = Me;
        var count = 0;
        foreach (var n in await notifications.QueryAsync(x => x.RecipientId == me && !x.IsRead))
            if (await SaveReadAsync(n)) count++;
        return count;
    }

    /// <summary>開啟單據時，把該單據的通知一併標示已讀</summary>
    public async Task MarkReadForDocumentAsync(int documentId)
    {
        if (currentUser.Id is not int me) return;
        foreach (var n in await notifications.QueryAsync(x => x.RecipientId == me && !x.IsRead && x.DocumentId == documentId))
            await SaveReadAsync(n);
    }

    private async Task<bool> SaveReadAsync(Notification n)
    {
        n.IsRead = true;
        n.ReadAt = clock.Now;
        try
        {
            await notifications.UpdateAsync(n);
            return true;
        }
        catch (ConcurrencyException)
        {
            return false; // 已被其他請求標示，忽略
        }
    }
}
