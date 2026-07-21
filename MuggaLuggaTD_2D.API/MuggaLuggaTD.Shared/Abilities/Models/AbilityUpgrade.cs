using System.Collections.Generic;

namespace Abilities.Models
{
    public class AbilityUpgrade
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string SpriteLibraryAssetLocation { get; set; }
        public List<AbilityModifier> Modifiers { get; set; }
    }
}
