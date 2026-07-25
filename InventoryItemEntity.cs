using System.ComponentModel.DataAnnotations.Schema;

namespace HarmonySqliteMod.Entities;

[Table("inventory_items")]
public class InventoryItemEntity
{
    /// <summary>
    /// 物品 MongoId，主键
    /// </summary>
    [Column("item_id")]
    public string ItemId { get; set; } = null!;

    /// <summary>
    /// 所属 Profile ID，带索引
    /// </summary>
    [Column("profile_id")]
    public string ProfileId { get; set; } = null!;

    /// <summary>
    /// 物品模板 ID
    /// </summary>
    [Column("_tpl")]
    public string Tpl { get; set; } = null!;

    /// <summary>
    /// 父物品 ID，支持树形结构
    /// </summary>
    [Column("parent_id")]
    public string? ParentId { get; set; }

    /// <summary>
    /// 槽位 ID
    /// </summary>
    [Column("slot_id")]
    public string? SlotId { get; set; }

    /// <summary>
    /// 位置 X
    /// </summary>
    [Column("location_x")]
    public int? LocationX { get; set; }

    /// <summary>
    /// 位置 Y
    /// </summary>
    [Column("location_y")]
    public int? LocationY { get; set; }

    /// <summary>
    /// 旋转
    /// </summary>
    [Column("location_r")]
    public int? LocationR { get; set; }

    /// <summary>
    /// 物品更新数据 JSON
    /// </summary>
    [Column("upd_json")]
    public string? UpdJson { get; set; }

    /// <summary>
    /// 是 PMC 还是 Scav (0/1)
    /// </summary>
    [Column("is_pmc")]
    public int IsPmc { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    [Column("updated_at")]
    public string UpdatedAt { get; set; } = null!;
}
