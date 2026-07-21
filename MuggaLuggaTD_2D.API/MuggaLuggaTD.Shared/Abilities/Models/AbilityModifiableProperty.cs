using System.Collections.Generic;
using Abilities.Strategies;
using Core.Models;

namespace Abilities.Models
{
    public class AbilityModifiableProperty<T> : ModifiableProperty<T>, IAbilityModifiableProperty<T>
    {
        public T GetCurrentValue()
        {
            // Use EqualityComparer to properly check for default values (works for both value and reference types)
            var comparer = EqualityComparer<T>.Default;

            if (!comparer.Equals(this.AdjustedValue, default(T)))
            {
                return this.AdjustedValue;
            }
            if (!comparer.Equals(this.AdjustedBaseValue, default(T)))
            {
                return this.AdjustedBaseValue;
            }
            return this.BaseValue;
        }

        public void Reset()
        {
            this.AdjustedBaseValue = this.BaseValue;
            this.AdjustedValue = this.BaseValue;
        }
    }
}