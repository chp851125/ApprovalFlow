namespace ApprovalFlow.Domain.Common;

/// <summary>所有可被 Repository 管理的實體都必須實作此介面。</summary>
public interface IEntity
{
    /// <summary>主鍵。新增時為 0，由儲存層指派。</summary>
    int Id { get; set; }

    /// <summary>樂觀並行控制版本號。每次更新成功 +1。</summary>
    int Version { get; set; }
}

public abstract class EntityBase : IEntity
{
    public int Id { get; set; }
    public int Version { get; set; }
}
