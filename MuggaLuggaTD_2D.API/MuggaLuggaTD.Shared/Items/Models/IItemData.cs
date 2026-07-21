using System.Collections.Generic;
using Enums;

namespace Items.Models
{
    public interface IItemData
    {
        string Id { get; set; }
        string ItemName { get; set; }
        int ItemCount { get; set; }
        ItemTypes ItemType { get; set; }
        string EquippedByCharacterId { get; set; }
        List<ItemAffinityStat> AffinityStats { get; set; }
        ItemRarityTypes Rarity { get; set; }
        ItemPowerTier PowerTier { get; set; }
        List<ItemInfluence> Influences { get; set; }
        ItemImplicitAttribute ImplicitAttribute { get; set; }
        List<ItemExplicitAttribute> ExplicitAttributes { get; set; }
    }
}
