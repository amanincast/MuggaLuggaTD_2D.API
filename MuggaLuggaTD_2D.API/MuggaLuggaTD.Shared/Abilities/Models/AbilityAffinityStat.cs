using System.Data;
using Enums;

namespace Abilities.Models
{
    public class AbilityAffinityStat
    {
        public AffinityTypes AffinityType { get; set; }
        public AbilityModifiableProperty<long?> Damage { get; set; }

        public long GetDamageValue()
        {
            if (Damage == null)
                return 0;
            if (Damage.AdjustedValue.HasValue)
                return Damage.AdjustedValue.Value;
            if (Damage.AdjustedBaseValue.HasValue)
                return Damage.AdjustedBaseValue.Value;
            return Damage.BaseValue.GetValueOrDefault();
        }
    }
}
