using Enums;

namespace Items.Models
{
    public class ItemAffinityStat
    {
        public AffinityTypes AffinityType { get; set; }
        public int BaseDefense { get; set; }
        public int BaseDamage { get; set; }
    }
}
