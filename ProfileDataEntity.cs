using System.ComponentModel.DataAnnotations.Schema;

namespace HarmonySqliteMod.Entities;

[Table("profile_data")]
public class ProfileDataEntity
{
    /// <summary>
    /// MongoId 字符串，主键
    /// </summary>
    [Column("profile_id")]
    public string ProfileId { get; set; } = null!;

    /// <summary>
    /// 玩家信息 JSON
    /// </summary>
    [Column("info_json")]
    public string? InfoJson { get; set; }

    /// <summary>
    /// PMC 角色数据 JSON
    /// </summary>
    [Column("pmc_json")]
    public string? PmcJson { get; set; }

    /// <summary>
    /// Scav 角色数据 JSON
    /// </summary>
    [Column("scav_json")]
    public string? ScavJson { get; set; }

    /// <summary>
    /// 技能数据 JSON
    /// </summary>
    [Column("skills_json")]
    public string? SkillsJson { get; set; }

    /// <summary>
    /// 任务数据 JSON
    /// </summary>
    [Column("quests_json")]
    public string? QuestsJson { get; set; }

    /// <summary>
    /// 商人数据 JSON
    /// </summary>
    [Column("traders_json")]
    public string? TradersJson { get; set; }

    /// <summary>
    /// 藏身处数据 JSON
    /// </summary>
    [Column("hideout_json")]
    public string? HideoutJson { get; set; }

    /// <summary>
    /// 对话数据 JSON
    /// </summary>
    [Column("dialogues_json")]
    public string? DialoguesJson { get; set; }

    /// <summary>
    /// 保险数据 JSON
    /// </summary>
    [Column("insurance_json")]
    public string? InsuranceJson { get; set; }

    /// <summary>
    /// 配装数据 JSON
    /// </summary>
    [Column("builds_json")]
    public string? BuildsJson { get; set; }

    /// <summary>
    /// SPT 元数据 JSON
    /// </summary>
    [Column("spt_meta_json")]
    public string? SptMetaJson { get; set; }

    /// <summary>
    /// 更新时间
    /// </summary>
    [Column("updated_at")]
    public string UpdatedAt { get; set; } = null!;
}
