using System.Collections.Generic;
using Core.Models;

namespace Abilities.Models
{
    public interface IAbilityModifiableProperty<T> : IUpgradeableProperty<T>
    {
        T GetCurrentValue();
        void Reset();
    }
}