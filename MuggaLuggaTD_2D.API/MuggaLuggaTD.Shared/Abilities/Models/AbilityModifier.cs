using Enums;

namespace Abilities.Models
{
    public class AbilityModifier
    {
        public AbilityUpgradeModifierTypes UpgradeModifierType { get; set; }
        public double? Value { get; set; }
        public AffinityTypes? AffinityType { get; set; }
        public string Property { get; set; }
    }
}
