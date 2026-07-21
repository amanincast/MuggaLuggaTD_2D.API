using System.Collections.Generic;

namespace Abilities.Models
{
    public class UnlockableAbilityInfo
    {
        public string AbilityLinkName { get; set; }
        public List<string> ValidTargetTags { get; set; }
    }
}
